// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

public class MainViewModelTests
{
    [Theory]
    [InlineData(ConnectionState.Connected, "APRS-IS connected · T2TEST")]
    [InlineData(ConnectionState.Connecting, "APRS-IS connecting…")]
    [InlineData(ConnectionState.Faulted, "APRS-IS connection failed · retrying")]
    [InlineData(ConnectionState.Disconnected, "APRS-IS disconnected")]
    public void ShowDescribesTheConnection(ConnectionState state, string expected)
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider());

        vm.Show(new EngineStatus(state, "T2TEST", 42, "1.0.0"));

        Assert.Equal(expected, vm.ConnectionText);
        Assert.Equal(42, vm.PacketCount);
    }

    [Fact]
    public void NewestPacketIsFirstAndTheListIsCapped()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider());

        for (var i = 0; i < MainViewModel.MaxPackets + 5; i++)
        {
            vm.Add(FakeEngine.Packet($"N{i}"));
        }

        Assert.Equal(MainViewModel.MaxPackets, vm.Packets.Count);
        Assert.Equal($"N{MainViewModel.MaxPackets + 4}", vm.Packets[0].Source);
    }

    [Fact]
    public void StationBatchesAddUpdateAndRemoveStations()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider());

        vm.Apply(new StationBatch([FakeEngine.Station("N0CALL"), FakeEngine.Station("LEADER")], []));
        vm.Apply(new StationBatch([FakeEngine.Station("N0CALL") with { Latitude = 50 }], ["LEADER"]));

        Assert.Equal(1, vm.StationCount);
    }

    [Fact]
    public async Task EngineErrorsAreShownAndRetriedUntilTheEngineAnswers()
    {
        var engine = new FakeEngine { StatusFailure = new HttpRequestException("Gateway unreachable") };
        var time = new FakeTimeProvider();
        var vm = new MainViewModel(engine, time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var run = vm.StartAsync(cts.Token);
        await WaitUntil(() => vm.ErrorText == "Gateway unreachable");
        Assert.Equal(MainViewModel.EngineUnreachableText, vm.ConnectionText);

        engine.StatusFailure = null;
        await AdvanceUntil(time, () => vm.ErrorText is null);
        Assert.Equal("APRS-IS connected · T2TEST", vm.ConnectionText);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task AnEndedStationStreamIsResubscribedFromAFreshSnapshot()
    {
        var engine = new FakeEngine();
        var time = new FakeTimeProvider();
        var vm = new MainViewModel(engine, time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var run = vm.StartAsync(cts.Token);
        engine.Push(new StationBatch([FakeEngine.Station("N0CALL"), FakeEngine.Station("LEADER")], []));
        await WaitUntil(() => vm.StationCount == 2);

        engine.EndStations();
        await AdvanceUntil(time, () => engine.StationSubscriptions == 2);
        Assert.Equal(2, vm.StationCount); // kept until the new snapshot arrives
        engine.Push(new StationBatch([FakeEngine.Station("N0CALL")], []));
        await AdvanceUntil(time, () => engine.StationSubscriptions == 2 && vm.StationCount == 1);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task AnEndedPacketStreamIsResubscribedWithoutDuplicates()
    {
        var engine = new FakeEngine();
        var time = new FakeTimeProvider();
        var vm = new MainViewModel(engine, time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var run = vm.StartAsync(cts.Token);
        engine.Push(FakeEngine.Packet("N0CALL"));
        await WaitUntil(() => vm.Packets.Count == 1);

        // A new subscription replays the engine's recent packets, including the one already shown.
        engine.EndPackets();
        await AdvanceUntil(time, () => engine.PacketSubscriptions == 2);
        Assert.Single(vm.Packets); // kept until the replay starts
        engine.Push(FakeEngine.Packet("N0CALL"));
        engine.Push(FakeEngine.Packet("N0NEW"));
        await AdvanceUntil(time, () => vm.Packets.Count == 2);
        Assert.Equal(["N0NEW", "N0CALL"], vm.Packets.Select(p => p.Source));

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task StartWatchesPacketsAndPollsStatusUntilCancelled()
    {
        var engine = new FakeEngine();
        var time = new FakeTimeProvider();
        var vm = new MainViewModel(engine, time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var run = vm.StartAsync(cts.Token);
        engine.Push(FakeEngine.Packet("N0CALL"));
        for (var i = 0; i < 200 && vm.Packets.Count == 0; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, cts.Token);
        }
        Assert.Equal("N0CALL", Assert.Single(vm.Packets).Source);
        Assert.Equal("APRS-IS connected · T2TEST", vm.ConnectionText);

        engine.Status = engine.Status with { Connection = ConnectionState.Faulted };
        time.Advance(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 200 && !vm.ConnectionText.Contains("failed", StringComparison.Ordinal); i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, cts.Token);
        }
        Assert.Contains("failed", vm.ConnectionText, StringComparison.Ordinal);

        await cts.CancelAsync();
        await run;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
        }
        Assert.True(condition(), "Condition was not met in time.");
    }

    /// <summary>Moves the fake clock past the retry delay until the condition holds.</summary>
    private static async Task AdvanceUntil(FakeTimeProvider time, Func<bool> condition)
    {
        for (var step = 0; step < 20 && !condition(); step++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, TestContext.Current.CancellationToken);
            time.Advance(MainViewModel.RetryDelay);
            for (var i = 0; i < 20 && !condition(); i++)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(5),
                    TimeProvider.System,
                    TestContext.Current.CancellationToken
                );
            }
        }
        Assert.True(condition(), "Condition was not met in time.");
    }
}
