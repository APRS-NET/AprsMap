// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Capture;
using AprsMap.UI.Localization;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Packet capture in the UI (docs/features/F1.27-packet-capture.md, behaviors 4–10).</summary>
public sealed class CaptureUiTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-18T12:00:00Z", null);

    private static CaptureView View() =>
        new(
            [
                new CapturedPacket(
                    At,
                    "rotate",
                    "N0BAD",
                    "N0BAD>APRS:!49X3.50N/07201.75W>bad lat",
                    [new EngineError("aprs.parse.invalid-latitude", "x")]
                ),
                new CapturedPacket(
                    At.AddMinutes(-5),
                    "rotate",
                    "N0UNK",
                    "N0UNK>APRS:%abcdef",
                    [new EngineError(EngineErrorCodes.CaptureUnsupported, "x")]
                ),
            ],
            [new CaptureDay(new DateOnly(2026, 10, 18), 2)],
            ["aprs.parse.invalid-latitude", EngineErrorCodes.CaptureUnsupported],
            RepeatsLeftOut: 3
        );

    [Fact]
    public async Task CaptureIsSwitchedOnWithItsLimitsAndBadLimitsAreRefused()
    {
        var engine = new FakeEngine();
        var section = new CaptureSettingsViewModel(engine, Localizer.Instance, _ => { });
        section.Show(engine.Settings);
        Assert.False(section.On);
        Assert.Equal(("50", "30"), (section.MaxMegabytes, section.Days));

        section.On = true;
        section.Days = "7";
        await section.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new CaptureSettings(true, 50, 7), engine.Settings.Capture);
        Assert.Equal("Saved", section.SavedText);

        section.MaxMegabytes = "0";
        await section.SaveCommand.ExecuteAsync(null);
        Assert.Equal("The limits are whole numbers above 0.", section.Error);
        Assert.Equal(new CaptureSettings(true, 50, 7), engine.Settings.Capture); // unchanged
    }

    [Fact]
    public async Task ThePanelListsWhatWasCapturedByDayAndReason()
    {
        var engine = new FakeEngine { Capture = View() };
        var opened = new List<string>();
        var panel = new CapturedPacketsViewModel(engine, Localizer.Instance, new FakeTimeProvider(At), opened.Add);

        await panel.LoadAsync();

        Assert.Equal(["N0BAD", "N0UNK"], panel.Rows.Select(r => r.Station));
        Assert.Equal("The latitude isn't valid.", panel.Rows[0].ReasonsText);
        Assert.Equal("Data type not supported", panel.Rows[1].ReasonsText);
        Assert.Equal(2, panel.Days.Count); // All days, and the one day with its count
        Assert.EndsWith("(2)", panel.Days[1].Name, StringComparison.Ordinal);
        Assert.Equal(
            ["Any reason", "The latitude isn't valid.", "Data type not supported"],
            panel.Reasons.Select(r => r.Name)
        );
        Assert.Equal("3 repeats today were counted, not written again.", panel.Summary);

        panel.SelectedReason = panel.Reasons[1];
        await Task.Yield();
        Assert.Equal("aprs.parse.invalid-latitude", engine.CaptureQueries[^1].Reason);

        panel.Rows[0].OpenInPacketsCommand.Execute(null);
        Assert.Equal(["N0BAD"], opened);
    }

    [Fact]
    public async Task ExportAndReportUseTheEnginesExportAndClearAsksFirst()
    {
        var engine = new FakeEngine { Capture = View() };
        var panel = new CapturedPacketsViewModel(engine, Localizer.Instance, new FakeTimeProvider(At), _ => { });
        await panel.LoadAsync();

        var result = await panel.ExportAsync();
        Assert.NotNull(result);
        Assert.True(engine.CaptureExports[^1].Anonymized);
        panel.Exported(result);
        Assert.Equal("Saved 1 lines as capture-2026-10-18.log.", panel.Notice);

        var uri = await panel.ReportUriAsync();
        Assert.StartsWith(CapturedPacketsViewModel.NewIssueUrl + "?title=", uri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("body=note", uri.AbsoluteUri, StringComparison.Ordinal);

        panel.ClearCommand.Execute(null);
        Assert.True(panel.IsConfirmingClear);
        Assert.Equal(0, engine.CaptureCleared);
        await panel.ConfirmClearCommand.ExecuteAsync(null);
        Assert.Equal(1, engine.CaptureCleared);
        Assert.Empty(panel.Rows);
        Assert.Equal("No packets captured yet. Switch capture on in Settings › Data.", panel.Summary);
    }

    [Fact]
    public void TheStatusSaysHowManyWereCapturedTodayWhileItsOn()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider(At));
        vm.Show(engine.Settings);

        vm.Show(new EngineStatus(ConnectionState.Connected, "T2TEST", 0, "1.0"));
        Assert.Null(vm.CaptureText);
        vm.Show(new EngineStatus(ConnectionState.Connected, "T2TEST", 0, "1.0", CapturedToday: 37));
        Assert.Equal("Capturing unexpected packets: 37 today", vm.CaptureText);
    }
}
