// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Settings;

namespace AprsMap.UI.Tests;

public class SourcesSectionTests
{
    private static readonly SourceSettings Euro = new("euro", "euro", SourceKind.AprsIs, "euro.aprs2.net:14580");

    private static (SourcesSectionViewModel Section, FakeEngine Engine) Create(AppSettings settings)
    {
        var engine = new FakeEngine { Settings = settings };
        var section = new SourcesSectionViewModel(engine, Localizer.Instance, _ => { });
        section.Show(settings);
        return (section, engine);
    }

    private static AppSettings BothOnRotate() => FakeEngine.TwoProfiles() with { Sources = [FakeEngine.Rotate, Euro] };

    [Fact]
    public void EachSourceSaysWhoUsesItAndAprsIsOrAKissTncCanBeAdded()
    {
        var (section, _) = Create(BothOnRotate());

        Assert.Equal(["rotate.aprs2.net", "euro"], section.Rows.Select(r => r.Name));
        Assert.Equal("Used by: Home, Portable", section.Rows[0].UsedByText);
        Assert.Equal("Not used by any profile", section.Rows[1].UsedByText);
        Assert.Equal(
            ["APRS-IS", "KISS over TCP", "KISS over serial", "Radio in APRS mode (receive only)", "GPS · coming"],
            section.Kinds.Select(k => k.Name)
        );
        Assert.Equal([true, true, true, true, false], section.Kinds.Select(k => k.IsAvailable));
    }

    [Fact]
    public async Task AddingAKissSourceTicksItForTheActiveProfileByDefault()
    {
        var (section, engine) = Create(BothOnRotate());
        section.AddCommand.Execute(null);
        section.Kinds[1].IsSelected = true; // KISS over TCP

        Assert.True(section.IsKiss);
        Assert.False(section.IsAprsIs);
        Assert.Equal([true, false], section.EditProfiles.Select(p => p.IsChecked)); // the active profile
        section.EditHost = " 10.10.20.144 ";
        await section.SaveCommand.ExecuteAsync(null);

        var tnc = engine.Settings.SourceList.Single(s => s.Kind == SourceKind.KissTcp);
        Assert.Equal(("10.10.20.144", "10.10.20.144", null, null), (tnc.Name, tnc.Host, tnc.Port, tnc.TncPort));
        Assert.Equal("10.10.20.144:8001", section.Rows[^1].Server);
        Assert.Contains(tnc.Id, engine.Settings.Profiles[0].SourceIds);
        Assert.DoesNotContain(tnc.Id, engine.Settings.Profiles[1].SourceIds);
        Assert.False(section.IsEditing);
    }

    [Fact]
    public async Task AddingASerialTncListsThePortsPresentAndSavesItsSpeedAndControlLines()
    {
        var (section, engine) = Create(BothOnRotate());
        engine.SerialPorts.Clear();
        engine.SerialPorts.Add("/dev/ttyUSB0");
        section.AddCommand.Execute(null);
        section.Kinds[2].IsSelected = true; // KISS over serial

        Assert.True(section.IsKiss);
        Assert.True(section.IsKissSerial);
        Assert.False(section.IsKissTcp);
        await section.RefreshSerialPortsCommand.ExecuteAsync(null);
        Assert.Equal(["/dev/ttyUSB0"], section.SerialPorts);
        Assert.Equal("/dev/ttyUSB0", section.EditSerialPort); // the one TNC plugged in
        Assert.Null(section.SerialPortsNote);
        section.EditBaud = "19200";
        section.EditRts = false;
        await section.SaveCommand.ExecuteAsync(null);

        var tnc = engine.Settings.SourceList.Single(s => s.Kind == SourceKind.KissSerial);
        Assert.Equal(
            ("/dev/ttyUSB0", "/dev/ttyUSB0", 19200, (bool?)null, (bool?)false),
            (tnc.Name, tnc.SerialPort, tnc.Baud, tnc.DtrEnable, tnc.RtsEnable)
        );
        Assert.Equal("/dev/ttyUSB0@19200", section.Rows[^1].Server);
        Assert.Contains(tnc.Id, engine.Settings.Profiles[0].SourceIds);

        section.Rows[^1].EditCommand.Execute(null);
        Assert.Equal(
            ("/dev/ttyUSB0", "19200", true, false),
            (section.EditSerialPort, section.EditBaud, section.EditDtr, section.EditRts)
        );
    }

    [Fact]
    public async Task ASerialTncCanBeGivenARadiosTypeOrItsOwnScripts()
    {
        var (section, engine) = Create(BothOnRotate());
        section.AddCommand.Execute(null);
        section.Kinds[2].IsSelected = true; // KISS over serial
        await Task.Yield();

        Assert.Equal(["Plain KISS", "Kenwood TH-D72", "Custom"], section.TncTypes.Select(t => t.Name));
        Assert.Null(section.SelectedTncType?.Id); // Plain KISS
        section.EditSerialPort = "/dev/ttyUSB0";
        section.SelectedTncType = section.TncTypes[1];
        Assert.False(section.IsCustomTnc);
        await section.SaveCommand.ExecuteAsync(null);
        Assert.Equal("th-d72", engine.Settings.SourceList.Single(s => s.Kind == SourceKind.KissSerial).TncType);

        section.Rows[^1].EditCommand.Execute(null);
        await Task.Yield();
        Assert.Equal("th-d72", section.SelectedTncType?.Id);
        section.SelectedTncType = section.TncTypes[^1];
        Assert.True(section.IsCustomTnc);
        section.EditEnterScript = "send KISS ON\nsend RESTART";
        await section.SaveCommand.ExecuteAsync(null);
        var custom = engine.Settings.SourceList.Single(s => s.Kind == SourceKind.KissSerial);
        Assert.Equal(
            ("custom", "send KISS ON\nsend RESTART", (string?)null),
            (custom.TncType, custom.EnterScript, custom.LeaveScript)
        );
    }

    [Fact]
    public async Task ARadioInAprsModeHasNoTxEnabledTncPortOrType()
    {
        var (section, engine) = Create(BothOnRotate());
        section.AddCommand.Execute(null);
        section.EditTransmit = true;
        section.Kinds[3].IsSelected = true; // Radio in APRS mode

        Assert.True(section.IsSerial);
        Assert.True(section.IsTnc);
        Assert.False(section.IsKiss);
        Assert.False(section.IsKissSerial);
        Assert.False(section.CanTransmitKind);
        Assert.False(section.EditTransmit); // unticked: it can't send
        section.EditSerialPort = "/dev/ttyUSB0";
        await section.SaveCommand.ExecuteAsync(null);

        var radio = engine.Settings.SourceList.Single(s => s.Kind == SourceKind.AprsTextSerial);
        Assert.Equal(("/dev/ttyUSB0", false, (string?)null), (radio.SerialPort, radio.Transmit, radio.TncType));
        Assert.Contains(radio.Id, engine.Settings.Profiles[0].SourceIds);
        Assert.Equal("/dev/ttyUSB0@9600", section.Rows[^1].Server);
    }

    [Fact]
    public async Task WithNoSerialPortsTheFormSaysSoAndAPortCanBeTyped()
    {
        var (section, engine) = Create(BothOnRotate());
        engine.SerialPorts.Clear();
        section.AddCommand.Execute(null);
        section.Kinds[2].IsSelected = true;

        await section.RefreshSerialPortsCommand.ExecuteAsync(null);

        Assert.Empty(section.SerialPorts);
        Assert.Equal(
            "No serial ports found. Plug the TNC in and press Refresh, or type the port.",
            section.SerialPortsNote
        );
        section.EditSerialPort = "COM3";
        await section.SaveCommand.ExecuteAsync(null);
        Assert.Equal("COM3", engine.Settings.SourceList.Single(s => s.Kind == SourceKind.KissSerial).SerialPort);
    }

    [Fact]
    public async Task EditingAKissSourceKeepsItsKindAndMovesItBetweenProfiles()
    {
        var tnc = new SourceSettings("tnc", "Direwolf", SourceKind.KissTcp, Host: "10.10.20.144");
        var settings = BothOnRotate() with { Sources = [FakeEngine.Rotate, Euro, tnc] };
        settings = settings with
        {
            Profiles =
            [
                settings.Profiles[0] with
                {
                    SourceIds = [.. settings.Profiles[0].SourceIds, "tnc"],
                },
                settings.Profiles[1],
            ],
        };
        var (section, engine) = Create(settings);

        section.Rows[2].EditCommand.Execute(null);
        Assert.False(section.CanPickKind);
        Assert.Equal([true, false], section.EditProfiles.Select(p => p.IsChecked));
        section.EditPort = "8002";
        section.EditTncPort = "1";
        section.EditProfiles[0].IsChecked = false;
        section.EditProfiles[1].IsChecked = true;
        await section.SaveCommand.ExecuteAsync(null);

        var saved = engine.Settings.SourceList.Single(s => s.Id == "tnc");
        Assert.Equal((8002, 1), (saved.Port, saved.TncPort));
        Assert.DoesNotContain("tnc", engine.Settings.Profiles[0].SourceIds);
        Assert.Contains("tnc", engine.Settings.Profiles[1].SourceIds);
    }

    [Fact]
    public async Task AddingCreatesAnAprsIsSourceNamedAfterItsServer()
    {
        var (section, engine) = Create(BothOnRotate());

        section.AddCommand.Execute(null);
        section.EditServer = " noam.aprs2.net:14580 ";
        section.EditFilter = "t/m";
        await section.SaveCommand.ExecuteAsync(null);

        var added = engine.Saved[^1].SourceList[^1];
        Assert.Equal(
            (SourceKind.AprsIs, "noam.aprs2.net", "noam.aprs2.net:14580", "t/m"),
            (added.Kind, added.Name, added.Server, added.Filter)
        );
        Assert.False(section.IsEditing);
    }

    [Fact]
    public async Task EditingShowsWhoUsesItAndKeepsItsId()
    {
        var (section, engine) = Create(BothOnRotate());

        section.Rows[0].EditCommand.Execute(null);
        Assert.Equal("Used by: Home, Portable", section.EditUsedBy);
        section.EditName = "Rotate";
        await section.SaveCommand.ExecuteAsync(null);

        var saved = engine.Saved[^1].SourceList[0];
        Assert.Equal(("rotate", "Rotate"), (saved.Id, saved.Name));
    }

    [Fact]
    public async Task DeletingASourceInUseAsksFirstAndUnticksIt()
    {
        var (section, engine) = Create(BothOnRotate());

        await section.Rows[1].DeleteCommand.ExecuteAsync(null); // unused: at once
        Assert.Equal(["rotate"], engine.Saved[^1].SourceList.Select(s => s.Id));

        await section.Rows[0].DeleteCommand.ExecuteAsync(null);
        Assert.True(section.IsConfirmingDelete);
        Assert.Equal(
            "rotate.aprs2.net is used by Home, Portable. Delete it and untick it there?",
            section.PendingDeleteText
        );
        Assert.Single(engine.Saved);

        await section.ConfirmDeleteCommand.ExecuteAsync(null);
        var saved = engine.Saved[^1];
        Assert.Empty(saved.SourceList);
        Assert.All(saved.Profiles, p => Assert.Empty(p.SourceIds));
    }
}
