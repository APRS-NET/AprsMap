// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Platform;
using AprsMap.UI.ViewModels;
using AprsMap.UI.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Sign-in and the signed-in operator on the toolbar (#423).</summary>
public sealed class ToolbarSignInTests
{
    private sealed class Session(AuthInfo info) : IOperatorSession
    {
        public Task<AuthInfo> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(info);

        public Task SignInAsync(SignInRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static async Task<(Window Window, MainViewModel Vm)> ShowAsync(AuthInfo? auth)
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider())
        {
            SharedStation = auth is not null,
            OperatorSession = auth is null ? null : new Session(auth),
        };
        await vm.LoadAuthAsync();
        var window = new Window
        {
            Content = new MainView { DataContext = vm },
            Width = 1000,
            Height = 600,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    private static Button Find(Window window, string name) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

    [AvaloniaFact]
    public async Task WhereSignInIsOfferedTheToolbarHasSignIn()
    {
        var (window, vm) = await ShowAsync(new AuthInfo(false, SignInOffered: true));

        var signIn = Find(window, "ToolbarSignIn");
        Assert.True(signIn.IsVisible);
        Assert.Equal("Sign in", signIn.Content);
        Assert.False(Find(window, "ToolbarOperator").IsVisible);
        signIn.Command!.Execute(null);
        Assert.NotNull(vm.SignIn); // the existing dialog
        window.Close();
    }

    [AvaloniaFact]
    public async Task SignedInTheToolbarNamesTheOperatorAndCanSignOut()
    {
        var (window, vm) = await ShowAsync(new AuthInfo(true, "Derek"));

        Assert.False(Find(window, "ToolbarSignIn").IsVisible);
        Assert.True(Find(window, "ToolbarOperator").IsVisible);
        Assert.Equal("Derek", vm.OperatorShortName);
        Assert.Equal("Signed in as Derek", vm.SignedInText);
        Assert.True(vm.CanSignOut);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AnAppWithTheKeyShowsAsTheKeyWithNoSignOut()
    {
        var (window, vm) = await ShowAsync(new AuthInfo(true, "API key", ApiKey: true));

        Assert.True(Find(window, "ToolbarOperator").IsVisible);
        Assert.Equal("API key", vm.OperatorShortName);
        Assert.False(vm.CanSignOut);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)] // the desktop's own station
    [InlineData(true)] // a public page's viewer, who can never sign in
    public async Task NeitherShowsWhereNoOneSignsIn(bool viewer)
    {
        var (window, _) = await ShowAsync(viewer ? AuthInfo.Viewer : null);

        Assert.False(Find(window, "ToolbarSignIn").IsVisible);
        Assert.False(Find(window, "ToolbarOperator").IsVisible);
        window.Close();
    }
}
