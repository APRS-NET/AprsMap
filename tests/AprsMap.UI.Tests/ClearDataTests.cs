// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Tools › Clear data… (docs/features/F1.19-housekeeping.md, behaviors 1–5; F1.29 behavior 9).</summary>
public sealed class ClearDataTests
{
    [Fact]
    public async Task EachClearShowsItsCountAndAsksBeforeClearing()
    {
        var engine = new FakeEngine { ClearCounts = new ClearCounts(2140, 12, 30, 5000) };
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);

        await vm.OpenClearDataAsync();
        var clear = Assert.IsType<AprsMap.UI.Settings.ClearDataViewModel>(vm.Dialog!.Content); // Tools › Clear data… (F1.29)
        await WaitUntil(() => clear.Rows[0].Text.Contains("2,140", StringComparison.Ordinal));
        Assert.Equal(
            [
                "Stations, objects and items, with their tracks (2,140)",
                "Stale stations only (12)",
                "Tracks, keeping where each station is now (30)",
                "Packet history (5,000)",
                "Telemetry reports (0 stations)",
            ],
            clear.Rows.Select(r => r.Text)
        );

        clear.Rows[0].ClearCommand.Execute(null);
        Assert.True(clear.IsConfirming);
        Assert.Equal("Clear 2,140 stations, objects and items included? This can't be undone.", clear.ConfirmText);
        Assert.Empty(engine.Cleared);

        await clear.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal([ClearScope.Stations], engine.Cleared);
        Assert.False(clear.IsConfirming);
        Assert.Equal("Cleared 1 station.", clear.ResultText);
    }

    [Fact]
    public async Task CancelClearsNothing()
    {
        var engine = new FakeEngine { ClearCounts = new ClearCounts(1, 1, 1, 1) };
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        await vm.OpenClearDataAsync();
        var clear = Assert.IsType<AprsMap.UI.Settings.ClearDataViewModel>(vm.Dialog!.Content);

        clear.Rows[3].ClearCommand.Execute(null);
        clear.CancelCommand.Execute(null);

        Assert.False(clear.IsConfirming);
        Assert.Empty(engine.Cleared);
    }

    [Fact]
    public async Task ARefusedClearSaysWhy()
    {
        var engine = new FakeEngine
        {
            ClearCounts = new ClearCounts(1, 1, 1, 1),
            ClearFailure = new UnauthorizedAccessException("no"),
        };
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        await vm.OpenClearDataAsync();
        var clear = Assert.IsType<AprsMap.UI.Settings.ClearDataViewModel>(vm.Dialog!.Content);

        clear.Rows[2].ClearCommand.Execute(null);
        await clear.ConfirmCommand.ExecuteAsync(null);

        Assert.NotNull(clear.ResultText);
    }

    [Fact]
    public async Task AReadOnlyViewerIsntOfferedClear()
    {
        var engine = new FakeEngine { Settings = FakeEngine.TwoProfiles() with { ReadOnly = true } };
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);

        await vm.OpenSettingsAsync();

        Assert.False(vm.CanClearData);
        Assert.Null(vm.Settings!.Data!.Clear);
        await vm.OpenClearDataAsync();
        Assert.Null(vm.Dialog); // Tools › Clear data… opens nothing for a viewer
    }

    [Fact]
    public void ClearedPacketHistoryEmptiesTheList()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider());
        vm.Show(new EngineStatus(ConnectionState.Connected, "T2TEST", 1, "1.0"));
        vm.PacketList.Receive(new PacketView(DateTimeOffset.UnixEpoch, "N0CALL", "Status", "hi", "N0CALL>APRS:>hi"));
        Assert.Single(vm.PacketList.Rows);

        vm.Show(new EngineStatus(ConnectionState.Connected, "T2TEST", 1, "1.0", PacketHistory: 1));

        Assert.Empty(vm.PacketList.Rows);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
        }
        Assert.True(condition(), "Condition was not met in time.");
    }
}
