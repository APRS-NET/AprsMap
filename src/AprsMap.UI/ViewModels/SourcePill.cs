// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.ViewModels;

/// <summary>
/// A source's status pill beside the APRS-IS one (docs/features/F1.25-sources.md behavior 11, F3.1 behaviors 5, 6):
/// its state in words and colour, a switch to turn it off and on, and a menu with Reconnect now. It keeps its identity
/// while the status changes, so its switch doesn't flicker.
/// </summary>
public sealed partial class SourcePill : ObservableObject
{
    private readonly Localizer _text;

    internal SourcePill(
        string id,
        Localizer text,
        Func<bool, Task> turn,
        Func<Task> reconnect,
        Func<Task>? sendPosition = null,
        Func<bool, Task>? openConsole = null
    )
    {
        Id = id;
        _text = text;
        ConsoleCommand = new AsyncRelayCommand(() => openConsole?.Invoke(false) ?? Task.CompletedTask);
        TncCommandsCommand = new AsyncRelayCommand(
            () => openConsole?.Invoke(true) ?? Task.CompletedTask,
            () => CanType
        );
        Switch = new SourceSwitch(turn);
        ReconnectCommand = new AsyncRelayCommand(reconnect, () => CanReconnect);
        SendPositionCommand = new AsyncRelayCommand(sendPosition ?? (() => Task.CompletedTask), () => CanSendPosition);
    }

    /// <summary>Sends the station's position on this source alone (docs/features/F2.1-own-station.md, behavior 10).</summary>
    public IAsyncRelayCommand SendPositionCommand { get; }

    /// <summary>Whether the menu has Send position now: the source isn't RX only and this app may transmit.</summary>
    [ObservableProperty]
    public partial bool IsTransmitEnabled { get; private set; }

    /// <summary>Whether Send position now can be used: Transmit is on and the source can send now.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendPositionCommand))]
    public partial bool CanSendPosition { get; private set; }

    /// <summary>Opens the Source console on this source (docs/features/F1.28-source-console.md, behavior 1).</summary>
    public IAsyncRelayCommand ConsoleCommand { get; }

    /// <summary>
    /// Opens the console with its command line, to type to the TNC (docs/features/F3.2-built-in-tncs.md, behavior 8).
    /// </summary>
    public IAsyncRelayCommand TncCommandsCommand { get; }

    /// <summary>Whether TNC commands… is offered: a KISS over serial source.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TncCommandsCommand))]
    public partial bool CanType { get; private set; }

    /// <summary>The source's id.</summary>
    public string Id { get; }

    /// <summary>The on/off switch inside the pill.</summary>
    public SourceSwitch Switch { get; }

    /// <summary>Drops the source's connection or wait and connects again now (F3.1 behavior 5).</summary>
    public IAsyncRelayCommand ReconnectCommand { get; }

    /// <summary>
    /// "Direwolf connected", "Direwolf: TNC not reachable", "Direwolf: TNC isn't in KISS mode", "Direwolf off".
    /// </summary>
    [ObservableProperty]
    public partial string Text { get; private set; } = string.Empty;

    /// <summary>The switch's accessible name and tooltip: "Direwolf on or off".</summary>
    [ObservableProperty]
    public partial string SwitchName { get; private set; } = string.Empty;

    /// <summary>The source's name, for the menus (F1.29).</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Connected: the OK colours, as the APRS-IS pill (F1.1).</summary>
    [ObservableProperty]
    public partial bool IsConnected { get; private set; }

    /// <summary>
    /// Reconnecting after an error, or connected with a warning: the warning colour, said in words too.
    /// </summary>
    [ObservableProperty]
    public partial bool IsWarning { get; private set; }

    /// <summary>Whether Reconnect now is offered: the source is on.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReconnectCommand))]
    public partial bool CanReconnect { get; private set; }

    /// <summary>
    /// Shows <paramref name="status"/>; <paramref name="canChange"/> says whether this viewer may switch it, and
    /// <paramref name="transmitting"/> whether the station transmits and this app may send (F2.1).
    /// </summary>
    internal void Show(SourceStatus status, bool canChange, bool transmitting = false)
    {
        IsTransmitEnabled = status.MaySend && canChange;
        CanType = status.Kind == SourceKind.KissSerial && canChange;
        CanSendPosition = transmitting && status.CanTransmit;
        Name = status.Name;
        var name = new Dictionary<string, object?> { ["source"] = status.Name };
        Text =
            !status.Enabled ? _text.Format("Status.Source.Off", name)
            : status is { Manual: true, State: ConnectionState.Connected } ? _text.Format("Status.Source.Manual", name)
            : (status.State is ConnectionState.Connected ? status.Warning : status.Error) is { } error
                ? _text.Format(
                    "Status.Source.Error",
                    new Dictionary<string, object?> { ["source"] = status.Name, ["error"] = _text.Format(error) }
                )
            : _text.Format(
                status.State switch
                {
                    ConnectionState.Connected => "Status.Source.Connected",
                    ConnectionState.Reconnecting => "Status.Source.Reconnecting",
                    _ => "Status.Source.Connecting",
                },
                name
            );
        // While transmitting, a source that never sends says so, which is why it isn't sending (F2.1 behavior 5).
        if (transmitting && status.Enabled && !status.MaySend)
        {
            Text = _text.Format("Status.Source.RxOnly", new Dictionary<string, object?> { ["status"] = Text });
        }
        SwitchName = _text.Format("Toggles.Switch.Source", name);
        // Connected with a warning, such as a TNC not in KISS mode, takes the warning colour (F3.1 behavior 13).
        var warned = status.State == ConnectionState.Connected && status.Warning is not null;
        IsConnected = status.Enabled && status.State == ConnectionState.Connected && !warned;
        IsWarning =
            status.Enabled && (warned || status.State == ConnectionState.Reconnecting && status.Error is not null);
        CanReconnect = status.Enabled && canChange;
        Switch.Show(status.Enabled, canChange);
    }
}
