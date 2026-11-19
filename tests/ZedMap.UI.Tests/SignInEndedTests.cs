// SPDX-License-Identifier: AGPL-3.0-only
using Microsoft.Extensions.Time.Testing;
using ZedMap.Contracts;
using ZedMap.UI.Localization;
using ZedMap.UI.Platform;
using ZedMap.UI.Settings;
using ZedMap.UI.ViewModels;

namespace ZedMap.UI.Tests;

/// <summary>
/// A page whose operator sign-in ended (a gateway restart signs everyone out, F2.9) stops asking for what only the
/// operator may have and starts again as a viewer; a setting that applies as it's switched says it was saved, or why not.
/// </summary>
public sealed class SignInEndedTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EngineAccessException OperatorOnly() =>
        new(new EngineError(EngineErrorCodes.OperatorOnly, EngineHub.OperatorOnlyMessage));

    /// <summary>Who the gateway says this page is: the operator for the first asks, then whatever the test says.</summary>
    private sealed class Session(int operatorAsks = int.MaxValue) : IOperatorSession
    {
        private int _asks;

        public AuthInfo After { get; set; } = AuthInfo.Viewer with { SignInOffered = true };

        public Task<AuthInfo> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(++_asks <= operatorAsks ? new AuthInfo(true, "derek") : After);

        public Task SignInAsync(SignInRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }
        Assert.True(condition());
    }

    [Fact]
    public async Task AnEndedSignInStopsTheUpdateFeedAndStartsThePageAgainAsAViewer()
    {
        var engine = new FakeEngine { UpdateWatchFailure = OperatorOnly() };
        var session = new Session(operatorAsks: 2); // the page loads as the operator, then the gateway restarts
        var restarts = 0;
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-15T14:15:00Z", null));
        var vm = new MainViewModel(engine, time)
        {
            SharedStation = true,
            OperatorSession = session,
            Restart = () => restarts++,
        };
        vm.Show(engine.Settings);
        await vm.LoadAuthAsync();
        Assert.True(vm.IsOperator);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = vm.StartAsync(cts.Token);
        await WaitUntil(() => restarts == 1);
        Assert.Equal(Localizer.Instance["Operator.SignInEnded"], vm.Toast);
        for (var i = 0; i < 5; i++)
        {
            time.Advance(MainViewModel.RetryDelay);
            await Task.Delay(10, Ct);
        }

        Assert.Equal(1, engine.UpdateWatches); // asked once, not every two seconds
        Assert.Equal(1, restarts);
        Assert.False(vm.IsOperator);
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task AStillSignedInOperatorRefusedSomethingElseIsntSignedOut()
    {
        var engine = new FakeEngine { SaveFailure = OperatorOnly() };
        var session = new Session();
        var restarts = 0;
        var vm = new MainViewModel(engine, new FakeTimeProvider())
        {
            SharedStation = true,
            OperatorSession = session,
            Restart = () => restarts++,
        };
        vm.Show(engine.Settings);
        await vm.LoadAuthAsync();

        await vm.SignInEndedAsync();

        Assert.Equal(0, restarts);
        Assert.True(vm.IsOperator);
    }

    [Fact]
    public async Task ASwitchThatSavesAsItsSwitchedSaysSavedThenTheNoticeGoes()
    {
        var engine = new FakeEngine();
        var time = new FakeTimeProvider();
        var vm = new MainViewModel(engine, time);
        vm.Show(engine.Settings);
        await vm.OpenSettingsAsync();
        var sources = vm.Settings!.Sources!;

        sources.IGateOn = true;
        await WaitUntil(() => vm.Toast == "Saved");
        Assert.False(vm.ToastIsWarning);

        time.Advance(MainViewModel.ToastTime);
        await WaitUntil(() => vm.Toast is null);
    }

    [Fact]
    public async Task ARefusedSaveFlipsTheSwitchBackAndSaysWhy()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        await vm.OpenSettingsAsync();
        var sources = vm.Settings!.Sources!;
        engine.SaveFailure = new ArgumentException("The profile has no APRS-IS source to gate to.");

        sources.IGateOn = true;

        await WaitUntil(() => vm.Toast is not null);
        Assert.True(vm.ToastIsWarning);
        Assert.Contains("APRS-IS", vm.Toast, StringComparison.Ordinal);
        Assert.False(sources.IGateOn); // it didn't take effect
        Assert.False(engine.Settings.GetActiveProfile()!.EffectiveIGate);
    }
}
