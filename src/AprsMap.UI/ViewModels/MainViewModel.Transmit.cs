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

    /// <summary>Sends the station's position once (behaviors 10–13); a refusal shows in the error line.</summary>
    [RelayCommand(CanExecute = nameof(CanSendPosition))]
    private async Task SendPositionAsync()
    {
        try
        {
            await engine.SendPositionAsync();
            TransmitAnnouncement = _text["Transmit.Sent"];
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
    }
}
