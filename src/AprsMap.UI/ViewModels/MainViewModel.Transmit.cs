// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.ViewModels;

/// <summary>Transmitting (docs/features/F2.1-own-station.md): the Transmit switch, its marker and Send position now.</summary>
public sealed partial class MainViewModel
{
    /// <summary>The Transmit switch (behavior 4).</summary>
    [ObservableProperty]
    public partial bool TransmitOn { get; private set; }

    /// <summary>
    /// Whether this app may turn Transmit on or off: it may change the toggles, it isn't a gateway's browser (behavior
    /// 9), and the engine wasn't started receive only.
    /// </summary>
    [ObservableProperty]
    public partial bool CanTransmit { get; private set; }

    /// <summary>"Transmitting", or "Transmit held: APRS-IS isn't connected"; null while Transmit is off.</summary>
    [ObservableProperty]
    public partial string? TransmitText { get; private set; }

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

    /// <summary>Whether the APRS-IS pill's menu has Send position on APRS-IS: it's TX enabled.</summary>
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

    /// <summary>Shows <see cref="EngineStatus.Transmit"/>, from the status and the toggles.</summary>
    private void ShowTransmit()
    {
        var transmit = _status?.Transmit;
        TransmitOn = _status?.Toggles?.Transmit ?? false;
        CanTransmit = CanToggle && !SharedStation && transmit is not { ReceiveOnly: true };
        var held = TransmitOn && transmit?.Blocked is not null;
        IsTransmitHeld = held;
        TransmitText =
            !TransmitOn ? null
            : held
                ? _text.Format(
                    "Transmit.Held",
                    new Dictionary<string, object?> { ["reason"] = _text.Format(transmit!.Blocked!) }
                )
            : _text["Transmit.On"];
        CanSendPosition = TransmitOn && !held && !_engineUnreachable && CanTransmit;
        var aprsIs = _status?.Sources?.FirstOrDefault(s => s.Kind == SourceKind.AprsIs);
        IsAprsIsTransmitEnabled = CanTransmit && aprsIs is { TransmitEnabled: true };
        CanSendPositionOnAprsIs = CanSendPosition && aprsIs is { CanTransmit: true };
    }
}
