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
    private AuthInfo? _auth;
    private bool _showing;

    internal ConnectionSectionViewModel(IGatewayClientHost host, Localizer text)
    {
        _host = host;
        _text = text;
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

    /// <summary>Said after Apply when the app can't restart itself.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    partial void OnUseGatewayChanged(bool value) => Edited();

    partial void OnAddressChanged(string value) => Edited();

    partial void OnApiKeyChanged(string value) => Edited();

    private void Edited()
    {
        if (!_showing)
        {
            Changed = Wanted() is { } wanted && wanted != _host.Settings;
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

    /// <summary>Saves the choice and restarts the app into it (behavior 1).</summary>
    [RelayCommand(CanExecute = nameof(Changed))]
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
            (_, { ApiKey: true }) => (_text.Format("Connection.Status.KeyRefused", address), true),
            _ => (_text.Format("Connection.Status.Viewing", address), false),
        };
    }

    /// <summary>The texts again, in the new language.</summary>
    internal void Retranslate() => ShowStatus();
}
