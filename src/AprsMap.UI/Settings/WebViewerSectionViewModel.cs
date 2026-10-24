// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using System.Globalization;
using AprsMap.UI.Localization;
using AprsMap.UI.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Settings;

/// <summary>A location choice for viewers.</summary>
/// <param name="Value">The choice.</param>
/// <param name="Label">How it reads.</param>
public sealed record ViewerLocationOption(ViewerLocation Value, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>An address to open, with Copy.</summary>
/// <param name="Address">The address.</param>
/// <param name="Copy">Puts it on the clipboard.</param>
public sealed record WebViewerAddress(string Address, IAsyncRelayCommand Copy);

/// <summary>
/// Settings › Web viewer (docs/features/F1.35-desktop-web-viewer.md, behaviors 2–7): Share to browsers, who may connect,
/// the port, what viewers see of the station's location, and, while it serves, the addresses to open and how many are
/// watching. The desktop's only.
/// </summary>
public sealed partial class WebViewerSectionViewModel : ObservableObject
{
    private readonly IWebViewerHost _host;
    private readonly Localizer _text;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private bool _showing;

    internal WebViewerSectionViewModel(IWebViewerHost host, Localizer text)
    {
        _host = host;
        _text = text;
        LocationOptions =
        [
            new(ViewerLocation.Exact, text["WebViewer.Location.Exact"]),
            new(ViewerLocation.Grid, text["WebViewer.Location.Grid"]),
            new(ViewerLocation.Hidden, text["WebViewer.Location.Hidden"]),
        ];
        Location = LocationOptions[0];
        host.Changed += (_, _) =>
        {
            if (_context is null)
            {
                Refresh();
            }
            else
            {
                _context.Post(_ => Refresh(), null);
            }
        };
        Show();
    }

    /// <summary>Puts text on the clipboard; set by the view.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>Whether this build carries the browser app (behavior 1).</summary>
    public bool Available => _host.Available;

    /// <summary>Share to browsers; applied as it's switched.</summary>
    [ObservableProperty]
    public partial bool On { get; set; }

    /// <summary>Devices on the local network may connect; otherwise this computer only.</summary>
    [ObservableProperty]
    public partial bool LocalNetwork { get; set; }

    /// <summary>This computer only (the radio's other choice).</summary>
    public bool ThisComputerOnly
    {
        get => !LocalNetwork;
        set => LocalNetwork = !value;
    }

    /// <summary>The port, as typed.</summary>
    [ObservableProperty]
    public partial string Port { get; set; } = string.Empty;

    /// <summary>Exact, grid square, hidden.</summary>
    public IReadOnlyList<ViewerLocationOption> LocationOptions { get; }

    /// <summary>What viewers see of the station's location.</summary>
    [ObservableProperty]
    public partial ViewerLocationOption Location { get; set; }

    /// <summary>"Serving: 2 browsers watching", "Off", or what went wrong.</summary>
    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    /// <summary>Whether something went wrong, for its colour.</summary>
    [ObservableProperty]
    public partial bool HasProblem { get; private set; }

    /// <summary>The addresses to open while it serves.</summary>
    public ObservableCollection<WebViewerAddress> Addresses { get; } = [];

    /// <summary>Why the port can't be used, as typed; null otherwise.</summary>
    [ObservableProperty]
    public partial string? PortError { get; private set; }

    /// <summary>Whether the choices differ from what's in force, so Apply is worth pressing.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool Changed { get; private set; }

    partial void OnLocalNetworkChanged(bool value)
    {
        OnPropertyChanged(nameof(ThisComputerOnly));
        Edited();
    }

    partial void OnPortChanged(string value) => Edited();

    partial void OnLocationChanged(ViewerLocationOption value) => Edited();

    partial void OnOnChanged(bool value)
    {
        if (!_showing && value != _host.Settings.On)
        {
            _ = ApplyAsync();
        }
    }

    private void Edited()
    {
        if (!_showing)
        {
            Changed = Wanted() is { } wanted && wanted != _host.Settings with { On = wanted.On };
        }
    }

    /// <summary>The settings as chosen; null when the port isn't a usable number.</summary>
    private WebViewerSettings? Wanted()
    {
        PortError =
            int.TryParse(Port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && port is >= 1024 and <= 65535
                ? null
                : _text["WebViewer.PortInvalid"];
        return PortError is null ? new WebViewerSettings(On, port, LocalNetwork, Location.Value) : null;
    }

    /// <summary>Applies the choices: restarts the viewer on them when it's on.</summary>
    [RelayCommand(CanExecute = nameof(Changed))]
    private async Task ApplyAsync()
    {
        if (Wanted() is not { } wanted)
        {
            _showing = true;
            On = _host.Settings.On; // a bad port can't be served
            _showing = false;
            return;
        }
        await _host.ApplyAsync(wanted);
        Show();
    }

    /// <summary>Shows the settings in force and where it stands.</summary>
    internal void Show()
    {
        _showing = true;
        try
        {
            var settings = _host.Settings;
            On = settings.On;
            LocalNetwork = settings.LocalNetwork;
            Port = settings.Port.ToString(CultureInfo.InvariantCulture);
            Location = LocationOptions.First(o => o.Value == settings.Location);
            PortError = null;
            Changed = false;
        }
        finally
        {
            _showing = false;
        }
        Refresh();
    }

    /// <summary>Where it stands now: the status line and the addresses; the viewer count is read fresh.</summary>
    internal void Refresh()
    {
        var state = _host.State;
        HasProblem = state.Problem != WebViewerProblem.None;
        StatusText = state switch
        {
            { Problem: WebViewerProblem.PortInUse } => _text.Format(
                "WebViewer.PortInUse",
                new Dictionary<string, object?> { ["port"] = _host.Settings.Port }
            ),
            { Problem: WebViewerProblem.CantListen } => _text.Format(
                "WebViewer.CantListen",
                new Dictionary<string, object?> { ["reason"] = state.Detail ?? string.Empty }
            ),
            { Running: true } => _text.Format(
                "WebViewer.Serving",
                new Dictionary<string, object?> { ["viewers"] = state.Viewers }
            ),
            _ => _host.Available ? _text["WebViewer.Off"] : _text["WebViewer.NotInBuild"],
        };
        var addresses = state.Running ? state.Addresses.Select(a => a.ToString()).ToList() : [];
        if (!addresses.SequenceEqual(Addresses.Select(a => a.Address)))
        {
            Addresses.Clear();
            foreach (var address in addresses)
            {
                Addresses.Add(
                    new WebViewerAddress(
                        address,
                        new AsyncRelayCommand(() => CopyToClipboard?.Invoke(address) ?? Task.CompletedTask)
                    )
                );
            }
        }
    }
}
