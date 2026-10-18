// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Platform;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Operator sign-in in the web app (docs/features/F2.9-operator-sign-in.md, behaviors 4–8, 10).</summary>
public sealed class SignInUiTests
{
    /// <summary>A gateway's sign-in: answers from a script and records what it was asked.</summary>
    private sealed class FakeSession(AuthInfo info) : IOperatorSession
    {
        public AuthInfo Info { get; set; } = info;

        public List<SignInRequest> Tries { get; } = [];

        public Exception? Refusal { get; set; }

        public int SignOuts { get; private set; }

        public Task<AuthInfo> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Info);

        public Task SignInAsync(SignInRequest request, CancellationToken cancellationToken = default)
        {
            Tries.Add(request);
            return Refusal is { } refusal ? Task.FromException(refusal) : Task.CompletedTask;
        }

        public Task SignOutAsync(CancellationToken cancellationToken = default)
        {
            SignOuts++;
            return Task.CompletedTask;
        }
    }

    private static EngineStatus Status =>
        new(
            ConnectionState.Connected,
            "T2TEST",
            0,
            "1.0",
            Verified: true,
            Toggles: StationToggles.AllOn,
            Transmit: new TransmitStatus(false)
        );

    private static (MainViewModel Vm, FakeSession Session, List<string> Restarts) Browser(
        AuthInfo info,
        bool readOnly = true
    )
    {
        var engine = new FakeEngine();
        var session = new FakeSession(info);
        var restarts = new List<string>();
        var vm = new MainViewModel(engine, new FakeTimeProvider())
        {
            SharedStation = true,
            OperatorSession = session,
            Restart = () => restarts.Add("restart"),
        };
        vm.Show(engine.Settings with { ReadOnly = readOnly });
        vm.Show(Status);
        return (vm, session, restarts);
    }

    [Fact]
    public void TheDesktopsUserIsTheOperatorAndIsOfferedNoSignIn()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        vm.Show(Status);

        Assert.True(vm.IsOperator);
        Assert.False(vm.CanSignIn);
        Assert.False(vm.IsSignedIn);
        Assert.True(vm.CanTransmit);
    }

    [Fact]
    public async Task AViewerIsOfferedSignInAndAWrongPasswordSaysSoAndIsCleared()
    {
        var (vm, session, restarts) = Browser(new AuthInfo(false, SignInOffered: true));
        await vm.LoadAuthAsync();
        Assert.True(vm.CanSignIn);
        Assert.False(vm.CanTransmit);

        vm.OpenSignInCommand.Execute(null);
        var dialog = vm.SignIn!;
        Assert.False(dialog.SignInCommand.CanExecute(null)); // nothing typed yet
        dialog.Name = " derek ";
        dialog.Password = "not the password";
        session.Refusal = EngineErrors.ToException(
            new EngineError(EngineErrorCodes.AuthWrongCredentials, "That name and password don't match.")
        );
        await dialog.SignInCommand.ExecuteAsync(null);

        Assert.Equal(new SignInRequest("derek", "not the password"), session.Tries.Single());
        Assert.Equal("That name and password don't match.", dialog.Error);
        Assert.Empty(dialog.Password);
        Assert.Same(dialog, vm.SignIn); // still open
        Assert.Empty(restarts);

        session.Refusal = EngineErrors.ToException(
            new EngineError(
                EngineErrorCodes.AuthTooManyTries,
                "Too many tries.",
                new Dictionary<string, string> { [EngineErrorArgs.Minutes] = "15" }
            )
        );
        dialog.Password = "another try";
        await dialog.SignInCommand.ExecuteAsync(null);
        Assert.Equal("Too many tries. Wait 15 min and try again.", dialog.Error);

        dialog.CancelCommand.Execute(null);
        Assert.Null(vm.SignIn);
    }

    [Fact]
    public async Task SigningInStartsThePageAgainAsTheOperator()
    {
        var (vm, session, restarts) = Browser(new AuthInfo(false, SignInOffered: true));
        await vm.LoadAuthAsync();
        vm.OpenSignInCommand.Execute(null);
        vm.SignIn!.Name = "derek";
        vm.SignIn.Password = "correct horse battery";

        await vm.SignIn.SignInCommand.ExecuteAsync(null);

        Assert.Null(vm.SignIn);
        Assert.Equal(["restart"], restarts);
    }

    [Fact]
    public async Task ASignedInOperatorCanTransmitAndSignOut()
    {
        var (vm, session, restarts) = Browser(
            new AuthInfo(true, "Derek", DateTimeOffset.UnixEpoch, SignInOffered: true, FailedSignIns: 2),
            readOnly: false
        );
        await vm.LoadAuthAsync();

        Assert.True(vm.IsSignedIn);
        Assert.False(vm.CanSignIn);
        Assert.Equal("Signed in as Derek", vm.SignedInText);
        Assert.True(vm.CanTransmit);

        await vm.SignOutCommand.ExecuteAsync(null);
        Assert.Equal(1, session.SignOuts);
        Assert.Equal(["restart"], restarts);
    }

    [Fact]
    public async Task OverPlainHttpTheDialogSaysWhyInsteadOfAskingForAPassword()
    {
        var (vm, _, _) = Browser(new AuthInfo(false, NeedsHttps: true));
        await vm.LoadAuthAsync();
        Assert.True(vm.CanSignIn);

        vm.OpenSignInCommand.Execute(null);

        Assert.False(vm.SignIn!.CanSignIn);
        Assert.StartsWith(
            "Signing in needs a secure (https://) address",
            vm.SignIn.NeedsHttpsText,
            StringComparison.Ordinal
        );
        Assert.False(vm.SignIn.SignInCommand.CanExecute(null));
    }

    [Fact]
    public async Task AGatewayWithNoOperatorOffersNoSignIn()
    {
        var (vm, _, _) = Browser(AuthInfo.Viewer);
        await vm.LoadAuthAsync();

        Assert.False(vm.CanSignIn);
        Assert.False(vm.IsSignedIn);
    }

    [Fact]
    public async Task AboutTellsASignedInOperatorAboutFailedSignIns()
    {
        var (vm, _, _) = Browser(new AuthInfo(true, "Derek", FailedSignIns: 3), readOnly: false);
        await vm.LoadAuthAsync();

        await vm.OpenAboutAsync();

        var about = Assert.IsType<AprsMap.UI.Settings.AboutViewModel>(vm.Dialog!.Content);
        Assert.Equal("3 failed sign-ins in the last day", about.SignInsText);
    }
}
