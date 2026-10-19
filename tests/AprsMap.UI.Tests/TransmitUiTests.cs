// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Packets;
using AprsMap.UI.Settings;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Own station and transmitting in the UI (docs/features/F2.1-own-station.md, behaviors 1, 2, 4–13).</summary>
public sealed class TransmitUiTests
{
    private static EngineStatus Status(StationToggles toggles, TransmitStatus transmit) =>
        new(ConnectionState.Connected, "T2TEST", 0, "1.0", Verified: true, Toggles: toggles, Transmit: transmit);

    private static MainViewModel Shell(FakeEngine engine, bool shared = false)
    {
        var vm = new MainViewModel(engine, new FakeTimeProvider()) { SharedStation = shared };
        vm.Show(engine.Settings);
        return vm;
    }

    [Fact]
    public async Task TransmitTurnsOnFromTheMenuAndSaysSoInTheStatusBar()
    {
        var engine = new FakeEngine();
        var vm = Shell(engine);
        vm.Show(Status(StationToggles.AllOn, new TransmitStatus(false)));
        Assert.True(vm.CanTransmit);
        Assert.False(vm.TransmitOn);
        Assert.Null(vm.TransmitText); // nothing to say while it's off
        Assert.False(vm.SendPositionCommand.CanExecute(null));

        await vm.ToggleCommand.ExecuteAsync(Toggle.Transmit);

        Assert.Equal([(Toggle.Transmit, true)], engine.Toggled);
        Assert.True(vm.TransmitOn);
        Assert.Equal("Transmitting", vm.TransmitText);
        Assert.False(vm.IsTransmitHeld);
        Assert.True(vm.SendPositionCommand.CanExecute(null));
        Assert.Equal("Transmit on", vm.ToggleAnnouncement);
    }

    [Fact]
    public async Task TheStatusBarSwitchIsWhereTransmitIsTurnedOnAndARefusalSpringsBack()
    {
        var engine = new FakeEngine();
        var vm = Shell(engine);
        vm.Show(Status(StationToggles.AllOn, new TransmitStatus(false)));
        Assert.True(vm.TransmitSwitch.IsShown);
        Assert.True(vm.TransmitSwitch.CanChange);
        Assert.False(vm.TransmitSwitch.IsOn);
        Assert.Equal("Transmit", vm.TransmitPillText);
        Assert.StartsWith("Transmit is off", vm.TransmitTip, StringComparison.Ordinal);

        vm.TransmitSwitch.IsOn = true;
        await WaitFor(() => vm.TransmitOn);

        Assert.Equal([(Toggle.Transmit, true)], engine.Toggled);
        Assert.True(vm.IsTransmitting);
        Assert.Equal("Transmitting", vm.TransmitPillText);

        vm.TransmitSwitch.IsOn = false;
        await WaitFor(() => !vm.TransmitOn);
        engine.ToggleFailure = EngineErrors.ToException(
            new EngineError(
                EngineErrorCodes.TransmitSourceOff,
                "Direwolf is switched off.",
                new Dictionary<string, string> { [EngineErrorArgs.Source] = "Direwolf" }
            )
        );

        vm.TransmitSwitch.IsOn = true;
        await WaitFor(() => vm.ErrorText is not null);

        Assert.False(vm.TransmitSwitch.IsOn); // springs back
        Assert.Equal("Direwolf is switched off. Turn it on in the status bar.", vm.ErrorText);
    }

    [Fact]
    public void AGatewayViewerDoesntSeeTheTransmitSwitch()
    {
        var engine = new FakeEngine();
        var vm = Shell(engine, shared: true);

        vm.Show(Status(StationToggles.AllOn, new TransmitStatus(false)));

        Assert.False(vm.TransmitSwitch.IsShown);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(condition());
    }

    [Fact]
    public void AHeldTransmitSaysWhyAndOffersNoSend()
    {
        var engine = new FakeEngine();
        var vm = Shell(engine);

        vm.Show(
            Status(
                StationToggles.AllOn with
                {
                    Transmit = true,
                },
                new TransmitStatus(
                    true,
                    new EngineError(EngineErrorCodes.TransmitNotConnected, "APRS-IS isn't connected.")
                )
            )
        );

        Assert.True(vm.IsTransmitHeld);
        Assert.Equal("Transmit held: APRS-IS isn't connected.", vm.TransmitText);
        Assert.False(vm.SendPositionCommand.CanExecute(null));
    }

    [Fact]
    public async Task SendPositionNowSendsAndARefusalShowsInTheErrorLine()
    {
        var engine = new FakeEngine();
        var vm = Shell(engine);
        vm.Show(Status(StationToggles.AllOn with { Transmit = true }, new TransmitStatus(true)));

        await vm.SendPositionCommand.ExecuteAsync(null);

        Assert.Equal(1, engine.PositionsSent);
        Assert.Equal([null], engine.PositionSources); // every source that can
        Assert.StartsWith("Position sent on", vm.TransmitAnnouncement);

        engine.SendFailure = EngineErrors.ToException(
            new EngineError(EngineErrorCodes.TransmitNoPosition, "Set the station's location first.")
        );
        await vm.SendPositionCommand.ExecuteAsync(null);

        Assert.Equal("Set the station's location first, in Settings › Profiles.", vm.ErrorText);
    }

    [Fact]
    public void AGatewaysBrowserAndAReceiveOnlyEngineCantTransmit()
    {
        var shared = Shell(new FakeEngine(), shared: true);
        shared.Show(Status(StationToggles.AllOn, new TransmitStatus(false)));
        Assert.False(shared.CanTransmit);

        var receiveOnly = Shell(new FakeEngine());
        receiveOnly.Show(Status(StationToggles.AllOn, new TransmitStatus(false, ReceiveOnly: true)));
        Assert.False(receiveOnly.CanTransmit);
    }

    [Fact]
    public void ASentPacketCarriesTheSentMarker()
    {
        var text = new Localizer(CultureInfo.GetCultureInfo("en-US"));
        var row = new PacketRow(
            new PacketView(
                DateTimeOffset.UnixEpoch,
                "N0TEST",
                "Position",
                "Position",
                "N0TEST>APZAMP:!",
                Markers: PacketMarkers.Sent
            ),
            text,
            TimeZoneInfo.Utc
        );

        Assert.True(row.IsSent);
        Assert.False(row.IsDirect);
    }

    [Fact]
    public async Task TheProfileEditorKeepsAndSavesTheSymbolAndComment()
    {
        var two = FakeEngine.TwoProfiles();
        var settings = two with
        {
            Profiles =
            [
                two.Profiles[0] with
                {
                    Callsign = "AB1CD-9",
                    Symbol = "/>",
                    Comment = "Mobile",
                },
                two.Profiles[1],
            ],
        };
        var engine = new FakeEngine { Settings = settings };
        var editing = settings.Profiles[0];
        var wizard = new SetupWizardViewModel(
            engine,
            new Localizer(CultureInfo.GetCultureInfo("en-US")),
            null,
            WizardMode.Edit,
            settings,
            editing
        );
        await wizard.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("/>", wizard.SelectedSymbol?.Symbol);
        Assert.Equal("Mobile", wizard.Comment);
        Assert.Contains(SetupWizardViewModel.Symbols, s => s.Symbol == "/-"); // the house, offered

        wizard.SelectedSymbol = SymbolChoice.For("/[");
        wizard.Comment = "  Walking  ";
        await wizard.FinishCommand.ExecuteAsync(null);

        var profile = Assert.Single(engine.Saved).Profiles.Single(p => p.Id == editing.Id);
        Assert.Equal("/[", profile.Symbol);
        Assert.Equal("Walking", profile.Comment);
    }
}

/// <summary>Transmitting per source and per profile in the UI (docs/features/F2.1-own-station.md, behaviors 4, 5, 9, 10).</summary>
public sealed class PerSourceTransmitUiTests
{
    private static readonly SourceSettings Tnc = new("tnc", "Direwolf", SourceKind.KissTcp, Host: "10.10.20.144");

    private static Localizer English() => new(CultureInfo.GetCultureInfo("en-US"));

    [Fact]
    public async Task ANewProfileListensWithAListenerIdUntilTheLicensedBoxIsTicked()
    {
        var engine = new FakeEngine { Settings = AppSettings.Empty };
        var wizard = new SetupWizardViewModel(engine, English());
        await wizard.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(wizard.IsLicensedOperator); // unlicensed by default
        Assert.True(wizard.ListenOnly);
        Assert.Equal("SWL123456", wizard.Callsign);
        Assert.Equal(3, wizard.StepCount); // first run: callsign, location, then where traffic comes from
        Assert.Equal(
            "You'll listen as SWL123456, an ID made for listeners: it isn't a callsign and never transmits.",
            wizard.ListeningAsText
        );
        Assert.False(wizard.HasInput); // nothing typed yet

        wizard.IsLicensedOperator = true;
        Assert.Equal(string.Empty, wizard.Callsign); // now the callsign is theirs to type
        Assert.Equal(3, wizard.StepCount); // the passcode is asked with APRS-IS, in Add a source
        wizard.Callsign = "AB1CD-9";

        wizard.IsLicensedOperator = false;
        Assert.Equal("SWL123456", wizard.Callsign);
        wizard.IsLicensedOperator = true;
        Assert.Equal("AB1CD-9", wizard.Callsign); // what they typed comes back
    }

    [Fact]
    public async Task AnExistingCallsignOpensLicensedAndSavesTheLicence()
    {
        var two = FakeEngine.TwoProfiles();
        var settings = two with { Profiles = [two.Profiles[0] with { Callsign = "AB1CD-9" }, two.Profiles[1]] };
        var engine = new FakeEngine { Settings = settings };
        var wizard = new SetupWizardViewModel(engine, English(), null, WizardMode.Edit, settings, settings.Profiles[0]);
        await wizard.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(wizard.IsLicensedOperator); // it has a callsign
        Assert.Equal("AB1CD-9", wizard.Callsign);

        await wizard.FinishCommand.ExecuteAsync(null);

        var profile = Assert.Single(engine.Saved).Profiles.Single(p => p.Id == settings.Profiles[0].Id);
        Assert.True(profile.Licensed);
    }

    [Fact]
    public async Task AListenersProfileKeepsItsIdAndSavesUnlicensedWithoutAPasscode()
    {
        var two = FakeEngine.TwoProfiles();
        var settings = two with { Profiles = [two.Profiles[0] with { Callsign = "SWL999000" }, two.Profiles[1]] };
        var engine = new FakeEngine { Settings = settings };
        var wizard = new SetupWizardViewModel(engine, English(), null, WizardMode.Edit, settings, settings.Profiles[0]);
        await wizard.LoadAsync(TestContext.Current.CancellationToken);
        Assert.False(wizard.IsLicensedOperator);
        Assert.Equal("SWL999000", wizard.Callsign);

        await wizard.FinishCommand.ExecuteAsync(null);

        var profile = Assert.Single(engine.Saved).Profiles.Single(p => p.Id == settings.Profiles[0].Id);
        Assert.Equal(("SWL999000", -1, false), (profile.Callsign, profile.Passcode, profile.Licensed));
    }

    [Fact]
    public async Task TheSourceFormSavesRxOnlyAndTheTxPath()
    {
        var settings = FakeEngine.TwoProfiles() with { Sources = [FakeEngine.Rotate, Tnc with { RxOnly = true }] };
        var engine = new FakeEngine { Settings = settings };
        var section = new SourcesSectionViewModel(engine, English(), _ => { });
        section.Show(settings);
        Assert.Equal("RX only", section.Rows[1].TransmitText);
        Assert.False(section.Rows[1].CanTestSend);

        section.Edit(section.Rows[1]);
        Assert.True(section.EditRxOnly);
        section.EditRxOnly = false;
        section.EditTxPath = "wide2-1";
        await section.SaveCommand.ExecuteAsync(null);

        var saved = Assert.Single(engine.Saved).SourceList.Single(s => s.Id == Tnc.Id);
        Assert.False(saved.RxOnly);
        Assert.Equal("WIDE2-1", saved.TxPath);
    }

    [Fact]
    public async Task TestSendSaysWhoRepeatedItOrWhyItCouldnt()
    {
        var settings = FakeEngine.TwoProfiles() with { Sources = [FakeEngine.Rotate, Tnc] };
        var engine = new FakeEngine { Settings = settings, TestHeardThrough = "K0HQ-1" };
        var section = new SourcesSectionViewModel(engine, English(), _ => { });
        section.Show(settings);
        var row = section.Rows[1];
        Assert.Equal("Can send · WIDE1-1,WIDE2-1", row.TransmitText);
        Assert.True(row.CanTestSend);

        await row.TestSendCommand.ExecuteAsync(null);

        Assert.Equal([Tnc.Id], engine.TestSends);
        Assert.Equal("Sent, and heard back through K0HQ-1.", row.TestResult);

        engine.SendFailure = EngineErrors.ToException(
            new EngineError(EngineErrorCodes.TransmitOff, "Transmit is off.")
        );
        await row.TestSendCommand.ExecuteAsync(null);
        Assert.Equal("Transmit is off. Turn it on in the status bar.", row.TestResult);
        Assert.True(row.OffersTransmitOn); // Turn Transmit on, beside the reason (behavior 9)

        await row.TurnTransmitOnCommand.ExecuteAsync(null);
        Assert.Contains((Toggle.Transmit, true), engine.Toggled);
        Assert.False(row.OffersTransmitOn);
        Assert.Null(row.TestResult);
    }

    [Fact]
    public async Task ASourcesPillSendsThePositionOnThatSourceAlone()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        vm.Show(
            new EngineStatus(
                ConnectionState.Connected,
                "T2TEST",
                0,
                "1.0",
                Verified: true,
                Sources:
                [
                    new SourceStatus("rotate", "rotate.aprs2.net", SourceKind.AprsIs, ConnectionState.Connected),
                    new SourceStatus(Tnc.Id, Tnc.Name, SourceKind.KissTcp, ConnectionState.Connected, MaySend: true),
                    new SourceStatus("rx", "Receiver", SourceKind.KissTcp, ConnectionState.Connected), // RX only
                ],
                Toggles: StationToggles.AllOn with
                {
                    Transmit = true,
                },
                Transmit: new TransmitStatus(true)
            )
        );
        var tnc = vm.SourcePills.Single(p => p.Id == Tnc.Id);
        var receiver = vm.SourcePills.Single(p => p.Id == "rx");
        Assert.True(tnc.IsTransmitEnabled);
        Assert.False(receiver.IsTransmitEnabled); // RX only: no Send position now in its menu
        Assert.Equal("Receiver connected · RX only", receiver.Text);
        Assert.False(vm.IsAprsIsTransmitEnabled);

        await tnc.SendPositionCommand.ExecuteAsync(null);

        Assert.Equal([Tnc.Id], engine.PositionSources);
    }
}
