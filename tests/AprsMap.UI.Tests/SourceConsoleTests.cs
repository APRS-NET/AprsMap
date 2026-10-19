// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Traffic;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>
/// The Source console in the UI (docs/features/F1.28-source-console.md, behaviors 1–5, 9, 10; acceptance tests 4, 5)
/// and typing to a TNC (docs/features/F3.2-built-in-tncs.md, behaviors 8–11).
/// </summary>
public sealed class SourceConsoleTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-18T12:00:00Z", null);

    private static readonly SourceStatus Radio = new(
        "radio",
        "TH-D72",
        SourceKind.KissSerial,
        ConnectionState.Connected
    );

    private static readonly SourceStatus Direwolf = new(
        "tnc",
        "Direwolf",
        SourceKind.KissTcp,
        ConnectionState.Connected
    );

    private static TrafficEntry Entry(
        TrafficKind kind,
        string? text,
        TrafficDirection direction = TrafficDirection.In,
        bool endsLine = true,
        string sourceId = "radio",
        byte[]? bytes = null
    ) => new(At, sourceId, direction, kind, text, bytes, EndsLine: endsLine);

    private static (SourceConsoleViewModel Console, FakeEngine Engine) Create()
    {
        var engine = new FakeEngine();
        engine.Traffic["radio"] =
        [
            Entry(TrafficKind.Unframed, "cmd:", endsLine: false),
            Entry(TrafficKind.Typed, "KISS ON", TrafficDirection.Out),
            Entry(TrafficKind.Unframed, "cmd:", endsLine: false),
            Entry(TrafficKind.Unframed, "OK"),
            Entry(TrafficKind.Packet, "N0CAR-9>APRS:>hi", bytes: [0x82, 0xA0]),
            Entry(TrafficKind.DroppedFrame, null, bytes: [0xC0, 0xDB, 0x41]) with
            {
                Detail = "InvalidEscape",
            },
        ];
        var console = new SourceConsoleViewModel(engine, Localizer.Instance, new FakeTimeProvider(At) { }, () => null);
        return (console, engine);
    }

    /// <summary>Waits for the watch, which runs on its own, to have done what's expected.</summary>
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(condition(), "Condition was not met in time.");
    }

    [Fact]
    public async Task EachEntryIsALineInWordsAndAPromptKeepsWhatFollowsOnItsLine()
    {
        var (console, _) = Create();
        console.ShowSources([Radio, Direwolf]);
        await WaitUntil(() => console.Lines.Count == 5);

        Assert.Equal("TH-D72", console.SelectedSource?.Name);
        Assert.Equal(
            ["Text cmd:", "Typed KISS ON", "Text cmd:OK", "Packet N0CAR-9>APRS:>hi", "Dropped C0 DB 41"],
            console.Lines.Select(l => $"{l.KindText} {l.Content}")
        );
        Assert.Equal(["In", "Out", "In", "In", "In"], console.Lines.Select(l => l.DirectionText));
        Assert.True(console.Lines[^1].IsProblem);
        Assert.Equal("InvalidEscape", console.Lines[^1].Detail);

        console.ShowHex = true;
        Assert.True(console.Lines[3].ShowsHex);
        Assert.Equal("82 A0", console.Lines[3].Hex);
        Assert.False(console.Lines[^1].ShowsHex); // its hex is its content already
    }

    [Fact]
    public async Task FiltersShowProblemsPacketsOrMatchingTextAndCopyGivesTheLinesShown()
    {
        var (console, _) = Create();
        console.ShowSources([Radio]);
        await WaitUntil(() => console.Lines.Count == 5);

        console.SelectedFilter = console.Filters.Single(f => f.Filter == ConsoleFilter.Problems);
        Assert.Equal(["Text", "Text", "Dropped"], console.Lines.Select(l => l.KindText));
        console.SelectedFilter = console.Filters.Single(f => f.Filter == ConsoleFilter.Packets);
        Assert.Equal(["Packet"], console.Lines.Select(l => l.KindText));
        console.SelectedFilter = console.Filters[0];
        console.FilterText = "kiss";
        Assert.Equal(["KISS ON"], console.Lines.Select(l => l.Content));

        var copied = SourceConsoleViewModel.CopyText(console.Lines);
        Assert.Matches(@"^\d\d:\d\d:\d\d\.000 Out Typed KISS ON$", copied);
    }

    [Fact]
    public async Task ScrollingUpPausesAndTheBottomCatchesUp()
    {
        var (console, engine) = Create();
        console.ShowSources([Radio]);
        await WaitUntil(() => console.Lines.Count == 5);

        console.Scrolled(atBottom: false);
        Assert.True(console.IsPaused);
        await engine.LiveTraffic.Writer.WriteAsync(
            Entry(TrafficKind.Packet, "N0NEW>APRS:>new"),
            TestContext.Current.CancellationToken
        );
        await WaitUntil(() => console.PausedText == "1 new · scroll to the bottom to see them");
        Assert.Equal(5, console.Lines.Count);

        console.Scrolled(atBottom: true);
        Assert.False(console.IsPaused);
        Assert.Equal("N0NEW>APRS:>new", console.Lines[^1].Content);
    }

    [Fact]
    public async Task OnlyASerialTncHasACommandLineWhichPutsItInManualMode()
    {
        var (console, engine) = Create();
        console.ShowSources([Radio, Direwolf]);
        console.SelectedSource = console.Sources[1];
        Assert.False(console.CanType); // KISS over TCP: no prompt

        console.SelectedSource = console.Sources[0];
        Assert.True(console.CanType);
        await console.ToggleCommandLineCommand.ExecuteAsync(null);
        Assert.True(console.IsCommandLineOpen);
        Assert.True(engine.Manual["radio"]);

        console.CommandText = "KISS ON";
        await console.SendCommand.ExecuteAsync(null);
        console.SendBytes = true;
        console.CommandText = "C0 FF C0";
        await console.SendCommand.ExecuteAsync(null);
        await console.EnterKissCommand.ExecuteAsync(null);
        await console.LeaveKissCommand.ExecuteAsync(null);
        Assert.Equal(
            [
                new TncCommand(TncCommandKind.Line, "KISS ON"),
                new TncCommand(TncCommandKind.Bytes, "C0 FF C0"),
                new TncCommand(TncCommandKind.EnterKiss),
                new TncCommand(TncCommandKind.LeaveKiss),
            ],
            engine.TncCommands.Select(c => c.Command)
        );
        Assert.Empty(console.CommandText);

        engine.TncCommandFailure = EngineErrors.ToException(new EngineError(EngineErrorCodes.TransmitNotLicensed, "x"));
        console.CommandText = "ID";
        console.SendBytes = false;
        await console.SendCommand.ExecuteAsync(null);
        Assert.NotNull(console.Error);
        Assert.Equal("ID", console.CommandText); // kept to try again

        await console.ToggleCommandLineCommand.ExecuteAsync(null);
        Assert.False(console.IsCommandLineOpen);
        Assert.False(engine.Manual["radio"]);
    }

    [Fact]
    public void ASerialTncsPillOffersTheConsoleAndTncCommandsAndSaysWhenManual()
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
                Sources: [Radio with { Manual = true, MaySend = true }, Direwolf]
            )
        );

        var radio = vm.SourcePills.Single(p => p.Id == "radio");
        var direwolf = vm.SourcePills.Single(p => p.Id == "tnc");
        Assert.Equal("TH-D72: manual", radio.Text);
        Assert.True(radio.CanType);
        Assert.False(direwolf.CanType);
        Assert.True(direwolf.ConsoleCommand.CanExecute(null));
    }
}
