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
}
