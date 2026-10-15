// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

public class MainViewModelTests
{
    [Theory]
    [InlineData(ConnectionState.Connected, "APRS-IS connected · T2TEST")]
    [InlineData(ConnectionState.Connecting, "APRS-IS connecting to T2TEST…")]
    [InlineData(ConnectionState.Reconnecting, "APRS-IS reconnecting… · ")]
    [InlineData(ConnectionState.Refused, "APRS-IS login refused · ")]
    [InlineData(ConnectionState.Disconnected, "APRS-IS disconnected")]
    [InlineData(ConnectionState.NotConfigured, "APRS-IS not set up · add a callsign")]
    public void ShowDescribesTheConnection(ConnectionState state, string expected)
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider());

        vm.Show(new EngineStatus(state, "T2TEST", 42, "1.0.0"));

        Assert.Equal(expected, vm.ConnectionText);
        Assert.Equal(42, vm.PacketCount);
    }

    [Fact]
    public void AConnectedLoginSaysWhetherItIsVerifiedAndHowOftenItReconnected()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider());

        vm.Show(new EngineStatus(ConnectionState.Connected, "T2TEST", 0, "1.0.0", Verified: true, Reconnects: 2));

        Assert.Equal("APRS-IS connected · T2TEST · verified", vm.ConnectionText);
        Assert.Equal("Login verified · reconnected 2 times", vm.ConnectionDetails);

        vm.Show(new EngineStatus(ConnectionState.Connected, "T2TEST", 0, "1.0.0"));

        Assert.Equal("Login not verified: receive-only · no reconnects", vm.ConnectionDetails);
    }

    [Fact]
    public void ARefusedLoginShowsTheServersReason()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider());
        var error = new EngineError(
            EngineErrorCodes.AprsIsLoginRefused,
            "refused",
            new Dictionary<string, string> { [EngineErrorArgs.Reason] = "Login by user not allowed" }
        );

        vm.Show(new EngineStatus(ConnectionState.Refused, null, 0, "1.0.0", ConnectionError: error));

        Assert.Equal("APRS-IS login refused · Login by user not allowed", vm.ConnectionText);
        Assert.Null(vm.ConnectionDetails);
    }

    [Fact]
    public async Task AReconnectCountsDownEachSecondWithItsReason()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        var engine = new FakeEngine();
        engine.Status = engine.Status with
        {
            Connection = ConnectionState.Reconnecting,
            RetryAt = time.GetUtcNow() + TimeSpan.FromSeconds(10),
            ConnectionError = new EngineError(EngineErrorCodes.AprsIsServerClosed, "closed"),
        };
        var vm = new MainViewModel(engine, time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var run = vm.StartAsync(cts.Token);
        await WaitUntil(() => vm.ConnectionText == "APRS-IS reconnecting in 10 s · Server closed the connection");
        await Task.Delay(TimeSpan.FromMilliseconds(20), TimeProvider.System, cts.Token);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() => vm.ConnectionText.Contains("in 9 s", StringComparison.Ordinal));
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, cts.Token);
            time.Advance(TimeSpan.FromSeconds(1));
        }
        await WaitUntil(() => vm.ConnectionText == "APRS-IS reconnecting… · Server closed the connection");

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task ReconnectNowIsOfferedOnlyWhereSettingsCanChange()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        vm.Show(engine.Status);

        Assert.True(vm.ReconnectCommand.CanExecute(null));
        await vm.ReconnectCommand.ExecuteAsync(null);
        Assert.Equal(1, engine.Reconnects);

        vm.Show(engine.Settings with { ReadOnly = true });
        Assert.False(vm.CanReconnect);
        vm.Show(engine.Settings);
        vm.Show(engine.Status with { Connection = ConnectionState.NotConfigured });
        Assert.False(vm.CanReconnect);
    }

    [Fact]
    public async Task AFailedReconnectShowsWhy()
    {
        var engine = new FakeEngine
        {
            ReconnectFailure = EngineErrors.Create(EngineErrorCodes.OperatorOnly, "operator only"),
        };
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        vm.Show(engine.Status);

        await vm.ReconnectCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorText);
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
        Assert.Equal(Localizer.Instance["Status.EngineUnreachable"], vm.ConnectionText);

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
    public void SettingsShowTheProfilesAndTheActiveOne()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider());

        vm.Show(FakeEngine.TwoProfiles());

        Assert.Equal(["Home", "Portable"], vm.Profiles.Select(p => p.Name));
        Assert.Equal("home", vm.SelectedProfile?.Id);
        Assert.True(vm.CanSwitchProfiles);

        vm.Show(FakeEngine.TwoProfiles() with { ReadOnly = true });
        Assert.False(vm.CanSwitchProfiles);
        vm.Show(AppSettings.Empty);
        Assert.False(vm.CanSwitchProfiles);
        Assert.Null(vm.SelectedProfile);
    }

    [Fact]
    public async Task ChoosingAnotherProfileSwitchesTheEngine()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);

        vm.SelectedProfile = vm.Profiles[1];
        await WaitUntil(() => engine.Saved.Count == 1);

        Assert.Equal("portable", engine.Saved[0].ActiveProfileId);
        Assert.Equal(engine.Settings.Profiles, engine.Saved[0].Profiles);
        Assert.Null(vm.ErrorText);

        vm.SelectedProfile = vm.Profiles[1]; // already active: nothing to do
        Assert.Single(engine.Saved);
    }

    [Fact]
    public void CountsReadAsWords()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider(), new Localizer(CultureInfo("en-US")));

        Assert.Equal("0 packets", vm.PacketCountText);
        vm.Show(new EngineStatus(ConnectionState.Connected, "T2TEST", 1, "1.0.0"));
        Assert.Equal("1 packet", vm.PacketCountText);
        vm.Show(new EngineStatus(ConnectionState.Connected, "T2TEST", 12345, "1.0.0"));
        Assert.Equal("12,345 packets", vm.PacketCountText);
        vm.Apply(new StationBatch([FakeEngine.Station("N0CALL")], []));
        Assert.Equal("1 station", vm.StationCountText);
    }

    [Fact]
    public async Task EngineErrorsAreShownInTheViewersLanguageAndFollowAChange()
    {
        var text = new Localizer(CultureInfo("en-US"));
        var engine = new FakeEngine
        {
            SaveFailure = EngineErrors.Create(EngineErrorCodes.OperatorOnly, "English from the gateway"),
        };
        var vm = new MainViewModel(engine, new FakeTimeProvider(), text);
        vm.Show(engine.Settings);
        vm.Show(new EngineStatus(ConnectionState.Connected, "T2TEST", 2, "1.0.0"));

        await vm.SwitchProfileAsync(engine.Settings, vm.Profiles[1]);
        Assert.Equal(text["Error.gateway.operator-only"], vm.ErrorText);
        Assert.NotEqual("English from the gateway", vm.ErrorText);

        text.SetLanguage(Localizer.PseudoLanguage);

        Assert.StartsWith("[Öñļý", vm.ErrorText, StringComparison.Ordinal);
        Assert.StartsWith("[ÅÞŔŠ-ÎŠ çöññéçţéð · T2TEST", vm.ConnectionText, StringComparison.Ordinal);
        Assert.StartsWith("[2 þåçķéţš", vm.PacketCountText, StringComparison.Ordinal);
    }

    private static System.Globalization.CultureInfo CultureInfo(string name) =>
        System.Globalization.CultureInfo.GetCultureInfo(name);

    [Fact]
    public async Task ARefusedSwitchShowsWhyAndKeepsTheActiveProfile()
    {
        var engine = new FakeEngine { SaveFailure = new UnauthorizedAccessException("Not allowed here") };
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);

        await vm.SwitchProfileAsync(engine.Settings, vm.Profiles[1]);

        Assert.Equal("Not allowed here", vm.ErrorText);
        Assert.Equal("home", vm.SelectedProfile?.Id);
        Assert.Empty(engine.Saved);
    }

    [Fact]
    public async Task AProfileSwitchedElsewhereIsPickedUpByTheStatusPoll()
    {
        var engine = new FakeEngine();
        var time = new FakeTimeProvider();
        var vm = new MainViewModel(engine, time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var run = vm.StartAsync(cts.Token);
        await WaitUntil(() => vm.SelectedProfile?.Id == "home");

        engine.Settings = engine.Settings with { ActiveProfileId = "portable" };
        engine.Status = engine.Status with { ProfileId = "portable" };
        await AdvanceUntil(time, () => vm.SelectedProfile?.Id == "portable");
        Assert.Empty(engine.Saved); // shown, not switched again

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

        engine.Status = engine.Status with { Connection = ConnectionState.Reconnecting };
        time.Advance(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 200 && !vm.ConnectionText.Contains("reconnecting", StringComparison.Ordinal); i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, cts.Token);
        }
        Assert.Contains("reconnecting", vm.ConnectionText, StringComparison.Ordinal);

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
