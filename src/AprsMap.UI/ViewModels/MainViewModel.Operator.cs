// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.ViewModels;

/// <summary>Operator sign-in on a gateway (docs/features/F2.9-operator-sign-in.md, behaviors 4–6).</summary>
public sealed partial class MainViewModel
{
    /// <summary>The gateway's sign-in, in the browser; null on the desktop, whose user is always the operator.</summary>
    public IOperatorSession? OperatorSession { get; init; }

    /// <summary>Who this viewer is on the gateway; null before it's known, and on the desktop.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsOperator),
        nameof(CanSignIn),
        nameof(IsSignedIn),
        nameof(SignedInText),
        nameof(OperatorShortName),
        nameof(CanSignOut),
        nameof(GatewayClientText),
        nameof(GatewayClientWarn),
        nameof(GatewayClientTip)
    )]
    public partial AuthInfo? Auth { get; private set; }

    /// <summary>Whether this viewer acts for the station: the desktop's user, or a signed-in gateway operator.</summary>
    public bool IsOperator => !SharedStation || Auth?.Operator == true;

    /// <summary>Whether the app menu offers Sign in…: an operator is set up on this gateway.</summary>
    public bool CanSignIn =>
        OperatorSession is not null
        && Auth is { Operator: false } a
        && (a.SignInOffered || a.NeedsHttps || a.NoOperator);

    /// <summary>Whether this browser is signed in as an operator.</summary>
    public bool IsSignedIn => OperatorSession is not null && Auth?.Operator == true;

    /// <summary>"Signed in as Derek", for the app menu; null when not signed in.</summary>
    public string? SignedInText =>
        !IsSignedIn ? null
        : Auth!.ApiKey ? _text["Auth.SignedInWithKey"] // the desktop as a gateway's client (F1.39 behavior 4)
        : _text.Format("Auth.SignedInAs", new Dictionary<string, object?> { ["name"] = Auth.Name });

    /// <summary>The operator's name on the toolbar (#423): theirs, or "API key" for an app let in by the key.</summary>
    public string? OperatorShortName =>
        !IsSignedIn ? null
        : Auth!.ApiKey ? _text["Auth.ApiKeyShort"]
        : Auth.Name;

    /// <summary>Whether Sign out is offered: a signed-in person, not an app with the key, which has no session.</summary>
    public bool CanSignOut => IsSignedIn && Auth is { ApiKey: false };

    /// <summary>
    /// The desktop as a gateway's client (#428): "Viewing pi.local:8081", or "pi.local:8081 · operator" with the API key,
    /// on the status bar; null for the desktop's own station and in the browser.
    /// </summary>
    public string? GatewayClientText =>
        GatewayClient?.Connected is not { } gateway
            ? null
            : _text.Format(
                Auth is { Operator: true } ? "Connection.Bar.Operator" : "Connection.Bar.Viewing",
                new Dictionary<string, object?> { ["gateway"] = gateway.Authority }
            );

    /// <summary>Whether the gateway refused the key (or it went over plain HTTP), for the warning colour.</summary>
    public bool GatewayClientWarn => GatewayClient?.Connected is not null && Auth is { Operator: false, ApiKey: true };

    /// <summary>Why it's only watching, when the gateway refused the key; the gateway's address otherwise.</summary>
    public string? GatewayClientTip =>
        GatewayClient?.Connected is not { } gateway ? null
        : Auth is { Operator: false, ApiKey: true, WaitMinutes: { } minutes }
            ? _text.Format("Connection.Bar.Waiting", new Dictionary<string, object?> { ["minutes"] = minutes })
        : Auth is { Operator: false, ApiKey: true, NeedsHttps: true } ? _text["Connection.Bar.KeyNeedsHttps"]
        : Auth is { Operator: false, ApiKey: true } ? _text["Connection.Bar.KeyRefused"]
        : gateway.ToString();

    /// <summary>The status bar's gateway notice opens Settings › Connection.</summary>
    [RelayCommand]
    private Task OpenConnectionAsync() => OpenSettingsSectionAsync("connection");

    /// <summary>The Sign in dialog while it's open.</summary>
    [ObservableProperty]
    public partial SignInViewModel? SignIn { get; private set; }

    partial void OnAuthChanged(AuthInfo? value)
    {
        Settings?.Connection?.ShowAuth(value);
        ShowTransmit();
        ShowViewerOnly();
    }

    /// <summary>
    /// A gateway viewer who can never act on the station here: settings read-only, not an operator, and no sign-in
    /// offered (the public port, F1.26 behavior 9). The controls that change the station are left out, not greyed.
    /// </summary>
    public bool IsViewerOnly => SharedStation && !CanToggle && !IsOperator && !CanSignIn;

    private void ShowViewerOnly()
    {
        OnPropertyChanged(nameof(IsViewerOnly));
        AprsIsSwitch.IsShown = !IsViewerOnly;
        foreach (var pill in SourcePills)
        {
            pill.Switch.IsShown = !IsViewerOnly;
        }
    }

    /// <summary>Asks the gateway who this browser is.</summary>
    internal async Task LoadAuthAsync()
    {
        if (OperatorSession is null)
        {
            return;
        }
        try
        {
            Auth = await OperatorSession.GetAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            Auth = AuthInfo.Viewer;
        }
    }

    /// <summary>Opens the Sign in dialog.</summary>
    [RelayCommand]
    private void OpenSignIn()
    {
        if (OperatorSession is null)
        {
            return;
        }
        SignIn = new SignInViewModel(
            OperatorSession,
            _text,
            Auth?.NeedsHttps == true,
            SignedIn,
            () => SignIn = null,
            noOperator: Auth?.NoOperator == true,
            unencrypted: Auth?.Unencrypted == true
        );
    }

    /// <summary>Signed in: the page starts again, so the hub connects with the session and the settings open up.</summary>
    private void SignedIn()
    {
        SignIn = null;
        if (Restart is { } restart)
        {
            restart();
        }
        else
        {
            _ = LoadAuthAsync();
        }
    }

    /// <summary>Signs out on the gateway, then starts again as a viewer (behavior 6).</summary>
    [RelayCommand]
    private async Task SignOutAsync()
    {
        if (OperatorSession is null)
        {
            return;
        }
        try
        {
            await OperatorSession.SignOutAsync();
        }
        catch (HttpRequestException)
        {
            // The gateway ends the session anyway once it's unused; the page still starts again as a viewer.
        }
        if (Restart is { } restart)
        {
            restart();
        }
        else
        {
            await LoadAuthAsync();
        }
    }
}
