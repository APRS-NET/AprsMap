// SPDX-License-Identifier: AGPL-3.0-only
using CommunityToolkit.Mvvm.ComponentModel;

namespace AprsMap.UI.ViewModels;

/// <summary>
/// An on/off switch for a source (F1.15 quick toggles): one instance shared by every place that shows it (the
/// connection pill and the dashboard's Connection tile), so they move together. Flipping it asks the engine; the engine's
/// answer, through <see cref="Show"/>, is what it settles on, so a refused change springs back.
/// </summary>
public sealed partial class SourceSwitch(Func<bool, Task> change) : ObservableObject
{
    private bool _isOn = true;

    /// <summary>Whether the source is on; setting it asks the engine to turn it that way.</summary>
    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (SetProperty(ref _isOn, value))
            {
                _ = change(value);
            }
        }
    }

    /// <summary>Whether this viewer may flip it (a gateway's viewers can't, and Internet off holds APRS-IS off).</summary>
    [ObservableProperty]
    public partial bool CanChange { get; private set; }

    /// <summary>Shows the engine's state, without asking it for anything.</summary>
    internal void Show(bool on, bool canChange)
    {
        CanChange = canChange;
        if (_isOn != on)
        {
            _isOn = on;
            OnPropertyChanged(nameof(IsOn));
        }
    }
}
