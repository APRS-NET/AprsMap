// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.ViewModels;

/// <summary>Transmitting (docs/features/F2.1-own-station.md): the Transmit switch, its pill and Send position now.</summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// The Transmit switch in the status bar (behavior 6), the one place it's turned on or off; Station › Transmit
    /// mirrors it. A refused turn springs back, and the refusal shows in the error line.
    /// </summary>
    public SourceSwitch TransmitSwitch { get; }

    /// <summary>"Transmit" while it's off, otherwise <see cref="TransmitText"/>: the pill's words.</summary>
    [ObservableProperty]
    public partial string TransmitPillText { get; private set; } = string.Empty;

    /// <summary>Transmit is on and nothing holds it: the pill is outlined in the transmit colour.</summary>
    [ObservableProperty]
    public partial bool IsTransmitting { get; private set; }

    /// <summary>Turns Transmit <paramref name="on"/> or off from the switch, then shows what the engine settled on.</summary>
    private async Task SwitchTransmitAsync(bool on)
    {
        if (on != TransmitOn)
        {
            await ToggleAsync(Toggle.Transmit);
        }
        ShowTransmit();
    }

    /// <summary>The Transmit switch (behavior 4).</summary>
    [ObservableProperty]
    public partial bool TransmitOn { get; private set; }

    /// <summary>
    /// Whether this app may turn Transmit on or off: it may change the toggles, it isn't a gateway's browser (behavior
    /// 9), and the engine wasn't started receive only.
    /// </summary>
    [ObservableProperty]
    public partial bool CanTransmit { get; private set; }

    /// <summary>"TX enabled", or "Transmit held: APRS-IS isn't connected"; null while Transmit is off.</summary>
    [ObservableProperty]
    public partial string? TransmitText { get; private set; }

    /// <summary>
    /// The marker's tooltip: what it is, and when the next beacon is due and why (docs/features/F2.2-beacons.md,
    /// behavior 10).
    /// </summary>
    [ObservableProperty]
    public partial string? TransmitTip { get; private set; }

    /// <summary>Transmit is on but held (behavior 8): the marker takes the warning colour, and says so in words.</summary>
    [ObservableProperty]
    public partial bool IsTransmitHeld { get; private set; }

    /// <summary>Whether Send position now is offered: Transmit is on and nothing holds it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendPositionCommand))]
    public partial bool CanSendPosition { get; private set; }

    /// <summary>Said to screen readers when a position was sent.</summary>
    [ObservableProperty]
    public partial string? TransmitAnnouncement { get; private set; }

    /// <summary>Sends the station's position once on every source that can (behaviors 10–13).</summary>
    [RelayCommand(CanExecute = nameof(CanSendPosition))]
    private Task SendPositionAsync() => SendPositionOnAsync(null);

    /// <summary>Sends the position on APRS-IS alone, from its pill's menu (behavior 10).</summary>
    [RelayCommand(CanExecute = nameof(CanSendPositionOnAprsIs))]
    private Task SendPositionOnAprsIsAsync() =>
        _status?.Sources?.FirstOrDefault(s => s.Kind == SourceKind.AprsIs) is { } aprsIs
            ? SendPositionOnAsync(aprsIs.Id)
            : Task.CompletedTask;

    /// <summary>Whether the APRS-IS pill's menu has Send position on APRS-IS: it isn't RX only.</summary>
    [ObservableProperty]
    public partial bool IsAprsIsTransmitEnabled { get; private set; }

    /// <summary>Whether Send position on APRS-IS can be used: Transmit is on and APRS-IS can send now.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendPositionOnAprsIsCommand))]
    public partial bool CanSendPositionOnAprsIs { get; private set; }

    /// <summary>
    /// Sends the position on <paramref name="sourceId"/>, or on every source that can when null; says where it went,
    /// and shows a refusal in the error line.
    /// </summary>
    private async Task SendPositionOnAsync(string? sourceId)
    {
        try
        {
            var sent = await engine.SendPositionAsync(sourceId);
            TransmitAnnouncement = _text.Format(
                "Transmit.SentOn",
                new Dictionary<string, object?>
                {
                    ["sources"] = string.Join(
                        ", ",
                        sent.Select(p =>
                            _status?.Sources?.FirstOrDefault(s => s.Id == p.SourceId)?.Name ?? p.SourceId ?? "?"
                        )
                    ),
                }
            );
            ShowError(null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError(ex);
        }
    }

    /// <summary>"TX enabled · next beacon in 12 min", or "TX enabled" with no beacon on.</summary>
    private string BeaconText(TransmitStatus? transmit)
    {
        if (transmit?.BeaconHeld is not null)
        {
            return _text["Transmit.BeaconsWaiting"];
        }
        if (transmit?.NextBeacon is not { } next)
        {
            return _text["Transmit.On"];
        }
        var minutes = (int)Math.Ceiling((next.At - time.GetUtcNow()).TotalMinutes);
        return minutes <= 1
            ? _text["Transmit.NextBeaconSoon"]
            : _text.Format("Transmit.NextBeacon", new Dictionary<string, object?> { ["minutes"] = minutes });
    }

    /// <summary>Why beacons wait, or which beacon is next and why then (behavior 10).</summary>
    private string BeaconTip(TransmitStatus? transmit)
    {
        if (transmit?.BeaconHeld is { } held)
        {
            return _text.Format(
                "Transmit.BeaconsWaitingTip",
                new Dictionary<string, object?> { ["reason"] = _text.Format(held) }
            );
        }
        if (transmit?.NextBeacon is not { } next)
        {
            return _text["Transmit.Marker.Help"];
        }
        var source = _status?.Sources?.FirstOrDefault(s => s.Id == next.SourceId)?.Name ?? next.SourceId;
        return _text.Format(
            next.Kind == BeaconKind.Position ? "Transmit.NextPositionTip" : "Transmit.NextStatusTip",
            new Dictionary<string, object?>
            {
                ["source"] = source,
                ["reason"] = _text[$"Transmit.BeaconReason.{next.Reason}"],
            }
        );
    }

    /// <summary>Shows <see cref="EngineStatus.Transmit"/>, from the status and the toggles.</summary>
    private void ShowTransmit()
    {
        var transmit = _status?.Transmit;
        TransmitOn = _status?.Toggles?.Transmit ?? false;
        CanTransmit = CanToggle && IsOperator && transmit is not { ReceiveOnly: true };
        Bulletins.CanPost = CanTransmit; // posting and groups (F2.5 behavior 7)
        var held = TransmitOn && transmit?.Blocked is not null;
        IsTransmitHeld = held;
        TransmitText =
            !TransmitOn ? null
            : held
                ? _text.Format(
                    "Transmit.Held",
                    new Dictionary<string, object?> { ["reason"] = _text.Format(transmit!.Blocked!) }
                )
            : BeaconText(transmit);
        TransmitTip = TransmitOn && !held ? BeaconTip(transmit) : _text["Transmit.Marker.Help"];
        if (TransmitOn && HeardBackText.Tip(transmit?.LastPosition, time.GetUtcNow(), _text) is { } heardBack)
        {
            TransmitTip += Environment.NewLine + heardBack; // F3.3 behavior 4
        }
        IsTransmitting = TransmitOn && !held;
        TransmitPillText = TransmitText ?? _text["Toggles.Transmit"];
        if (!TransmitOn)
        {
            TransmitTip = _text["Transmit.OffTip"];
        }
        TransmitSwitch.Show(TransmitOn, CanTransmit);
        TransmitSwitch.IsShown = IsOperator;
        CanSendPosition = TransmitOn && !held && !_engineUnreachable && CanTransmit;
        var aprsIs = _status?.Sources?.FirstOrDefault(s => s.Kind == SourceKind.AprsIs);
        IsAprsIsTransmitEnabled = CanTransmit && aprsIs is { MaySend: true };
        CanSendPositionOnAprsIs = CanSendPosition && aprsIs is { CanTransmit: true };
    }
}
