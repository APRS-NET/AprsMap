// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Gps;
using AprsMap.UI.Localization;
using AprsMap.UI.Settings;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>A radio's GPS on its TNC port, in the UI (docs/features/F3.13-tnc-gps.md, acceptance test 6).</summary>
public sealed class TncGpsUiTests
{
    private static readonly GpsStatus RadioFix = new(
        "radio",
        GpsFixKind.Fix3D,
        DateTimeOffset.UnixEpoch,
        true,
        49.0583,
        -72.0292,
        Satellites: 7
    );

    [Fact]
    public async Task OnlyTextOutputOffersTheRadiosGpsAndItsChoiceIsSaved()
    {
        var engine = new FakeEngine();
        var section = new SourcesSectionViewModel(engine, Localizer.Instance, _ => { });
        section.Show(engine.Settings);
        section.AddCommand.Execute(null);

        section.EditKind = SourceKind.KissSerial;
        Assert.False(section.IsTextSerial);
        section.EditKind = SourceKind.AprsTextSerial;
        Assert.True(section.IsTextSerial);
        Assert.True(section.TncGpsAuto); // the default

        section.TncGpsOn = true;
        section.EditSerialPort = "/dev/ttyUSB0";
        await section.SaveCommand.ExecuteAsync(null);

        var radio = engine.Settings.SourceList.Single(s => s.Kind == SourceKind.AprsTextSerial);
        Assert.Equal(TncGps.On, radio.TncGps);
    }

    [Fact]
    public async Task AutoIsNotWrittenDown()
    {
        var engine = new FakeEngine();
        var section = new SourcesSectionViewModel(engine, Localizer.Instance, _ => { });
        section.Show(engine.Settings);
        section.AddCommand.Execute(null);
        section.EditKind = SourceKind.AprsTextSerial;
        section.EditSerialPort = "/dev/ttyUSB0";

        await section.SaveCommand.ExecuteAsync(null);

        Assert.Null(engine.Settings.SourceList.Single(s => s.Kind == SourceKind.AprsTextSerial).TncGps);
    }

    [Fact]
    public void ARadioWhoseGpsIsInUseSaysItsFix()
    {
        var pill = new SourcePill(
            "radio",
            Localizer.Instance,
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            _ => Task.CompletedTask
        );
        var radio = new SourceStatus("radio", "TH-D72A", SourceKind.AprsTextSerial, ConnectionState.Connected);

        pill.Show(radio, true);
        Assert.DoesNotContain("fix", pill.Text, StringComparison.Ordinal); // no GPS in use: just the radio

        pill.Show(radio, true, gps: RadioFix, hasFixedLocation: true);
        Assert.Equal("TH-D72A · 3D fix · 7 satellites · RX only", pill.Text); // text output never sends
        Assert.False(pill.IsWarning);
    }

    [Fact]
    public void TheGpsPanelSaysWhichGpsItIs()
    {
        var panel = new GpsPanelViewModel(Localizer.Instance, new FakeTimeProvider(), _ => Task.CompletedTask);

        panel.Show(RadioFix, UnitSettings.Metric, "TH-D72A");

        Assert.Equal("From TH-D72A", panel.FromText);
    }
}
