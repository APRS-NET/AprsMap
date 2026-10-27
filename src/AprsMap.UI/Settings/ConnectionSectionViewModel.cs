// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Settings;

/// <summary>
/// Settings › Connection (docs/features/F1.39-desktop-gateway-client.md, behaviors 1, 3, 4): the desktop's own station
/// or a gateway's, with its address and API key, and Apply, which saves the choice and restarts the app into it. The
/// desktop's only.
/// </summary>
public sealed partial class ConnectionSectionViewModel : ObservableObject
{
    private readonly IGatewayClientHost _host;
    private readonly Localizer _text;
    private readonly string _appVersion;
    private AuthInfo? _auth;
    private bool _showing;

    // What the last test passed for (#419): Apply needs a pass for exactly what's in the fields.
    private GatewayClientSettings? _passed;

    internal ConnectionSectionViewModel(IGatewayClientHost host, Localizer text, string? appVersion = null)
    {
        _host = host;
        _text = text;
        _appVersion = (appVersion ?? Brand.Version).Split('+')[0];
        StatusText = string.Empty;
        Show();
    }

    /// <summary>A gateway's station; otherwise this computer's own.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThisComputer))]
    public partial bool UseGateway { get; set; }

    /// <summary>This computer's own station (the radio's other choice).</summary>
    public bool ThisComputer
    {
        get => !UseGateway;
        set => UseGateway = !value;
    }

    /// <summary>The gateway's address, as typed.</summary>
    [ObservableProperty]
    public partial string Address { get; set; } = string.Empty;

    /// <summary>The API key, as pasted; empty to only watch.</summary>
    [ObservableProperty]
    public partial string ApiKey { get; set; } = string.Empty;

    /// <summary>Whether the key shows as typed rather than as dots.</summary>
    [ObservableProperty]
    public partial bool ShowKey { get; set; }

    /// <summary>Why the address can't be used; null otherwise.</summary>
    [ObservableProperty]
    public partial string? AddressError { get; private set; }

    /// <summary>What this run shows: this computer's station, or the gateway's and as whom.</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; }

    /// <summary>Whether the status is a problem (a refused key), for its colour.</summary>
    [ObservableProperty]
    public partial bool HasProblem { get; private set; }

    /// <summary>Whether the choices differ from what's saved, so Apply is worth pressing.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool Changed { get; private set; }

    /// <summary>What the last test found (#419); null before one, or after an edit.</summary>
    [ObservableProperty]
    public partial string? TestText { get; private set; }

    /// <summary>Whether the last test found something that stops Apply.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestNeedsAttention))]
    public partial bool TestFailed { get; private set; }

    /// <summary>Whether the last test passed with a warning (versions differ).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestNeedsAttention))]
    public partial bool TestWarned { get; private set; }

    /// <summary>A failure or a warning: the result in the warning colour.</summary>
    public bool TestNeedsAttention => TestFailed || TestWarned;

    /// <summary>Whether a test is on its way.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    public partial bool Testing { get; private set; }

    /// <summary>Whether Apply may save: a change, and for a gateway a test passed for exactly these fields (#419).</summary>
    private bool CanApply => Changed && (!UseGateway || (Wanted() is { } wanted && wanted == _passed));

    private bool CanTest() => UseGateway && !Testing;

    /// <summary>Said after Apply when the app can't restart itself.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    partial void OnUseGatewayChanged(bool value)
    {
        Edited();
        TestCommand.NotifyCanExecuteChanged();
    }

    partial void OnAddressChanged(string value) => Edited();

    partial void OnApiKeyChanged(string value) => Edited();

    private void Edited()
    {
        if (!_showing)
        {
            var wanted = Wanted();
            Changed = wanted is not null && wanted != _host.Settings;
            if (wanted != _passed)
            {
                TestText = null; // an edit asks for a new test
                TestFailed = TestWarned = false;
            }
            ApplyCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>The settings as chosen; null when the address isn't one.</summary>
    private GatewayClientSettings? Wanted()
    {
        AddressError = null;
        if (!UseGateway)
        {
            return new GatewayClientSettings(null, _host.Settings.ApiKey);
        }
        if (
            !Uri.TryCreate(Address.Trim(), UriKind.Absolute, out var address)
            || address.Scheme is not ("http" or "https")
        )
        {
            AddressError = _text["Connection.AddressInvalid"];
            return null;
        }
        var key = ApiKey.Trim();
        return new GatewayClientSettings(address, key.Length > 0 ? key : null);
    }

    /// <summary>
    /// Tests the address and key without saving them (#419): whether a gateway answers, whether this app can work with
    /// it, what it makes of the key, and whether their versions differ.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        if (Wanted() is not { Gateway: { } gateway } wanted)
        {
            return;
        }
        Testing = true;
        TestText = _text["Connection.Test.Testing"];
        TestFailed = TestWarned = false;
        try
        {
            var outcome = await _host.TestAsync(gateway, wanted.ApiKey);
            var (text, failed, warned) = Judge(gateway, outcome);
            (TestText, TestFailed, TestWarned) = (text, failed, warned);
            _passed = failed ? null : wanted;
        }
        finally
        {
            Testing = false;
            ApplyCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>The verdict (#419): what the test found, whether it stops Apply, and whether it warns.</summary>
    private (string Text, bool Failed, bool Warned) Judge(Uri gateway, ConnectionTestOutcome outcome)
    {
        var args = new Dictionary<string, object?>
        {
            ["address"] = gateway.ToString(),
            ["detail"] = outcome.Detail ?? string.Empty,
            ["version"] = outcome.Gateway?.Version ?? string.Empty,
            ["app"] = _appVersion,
        };
        if (outcome.Unreachable)
        {
            return (_text.Format("Connection.Test.Unreachable", args), true, false);
        }
        if (outcome.Gateway is not { } answer || answer.Product != Brand.Slug)
        {
            return (_text.Format("Connection.Test.NotAGateway", args), true, false);
        }
        if (answer.ContractVersion != EngineHub.ContractVersion)
        {
            return (_text.Format("Connection.Test.Incompatible", args), true, false);
        }
        switch (answer.Key)
        {
            case ApiKeyCheck.Refused:
                return (_text.Format("Connection.Test.KeyRefused", args), true, false);
            case ApiKeyCheck.NeedsHttps:
                return (_text.Format("Connection.Test.KeyNeedsHttps", args), true, false);
            case ApiKeyCheck.NotAdminPort:
                return (_text.Format("Connection.Test.NotAdminPort", args), true, false);
        }
        var reached = _text.Format(
            answer.Key == ApiKeyCheck.Accepted ? "Connection.Test.Operator" : "Connection.Test.WatchOnly",
            args
        );
        return MajorMinor(answer.Version) == MajorMinor(_appVersion)
            ? (reached, false, false)
            : (reached + " " + _text.Format("Connection.Test.VersionsDiffer", args), false, true);
    }

    /// <summary>"0.8" of "0.8.0-alpha.2".</summary>
    private static string MajorMinor(string version) => string.Join('.', version.Split('-')[0].Split('.').Take(2));

    /// <summary>Saves the choice and restarts the app into it (behavior 1).</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (Wanted() is not { } wanted)
        {
            return;
        }
        await _host.SaveAsync(wanted);
        Changed = false;
        if (!_host.Restart())
        {
            Message = _text["Connection.RestartToSwitch"];
        }
    }

    /// <summary>Who this app is on the gateway it shows, for the status line (behaviors 3, 4).</summary>
    internal void ShowAuth(AuthInfo? auth)
    {
        _auth = auth;
        ShowStatus();
    }

    /// <summary>Shows the saved choice and what this run shows.</summary>
    internal void Show()
    {
        _showing = true;
        try
        {
            var settings = _host.Settings;
            UseGateway = settings.Gateway is not null;
            Address = (settings.Gateway ?? GatewayClientSettings.DefaultGateway).ToString();
            ApiKey = settings.ApiKey ?? string.Empty;
            AddressError = null;
            Changed = false;
        }
        finally
        {
            _showing = false;
        }
        ShowStatus();
    }

    private void ShowStatus()
    {
        var address = new Dictionary<string, object?> { ["address"] = _host.Connected?.ToString() ?? string.Empty };
        (StatusText, HasProblem) = (_host.Connected, _auth) switch
        {
            (null, _) => (_text["Connection.Status.ThisComputer"], false),
            (_, { Operator: true }) => (_text.Format("Connection.Status.Operator", address), false),
            (_, { ApiKey: true, NeedsHttps: true }) => (_text.Format("Connection.Status.KeyNeedsHttps", address), true),
            (_, { ApiKey: true }) => (_text.Format("Connection.Status.KeyRefused", address), true),
            _ => (_text.Format("Connection.Status.Viewing", address), false),
        };
    }

    /// <summary>The texts again, in the new language.</summary>
    internal void Retranslate() => ShowStatus();
}
