// SPDX-License-Identifier: AGPL-3.0-only
using System.ComponentModel;
using AprsMap.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace AprsMap.UI.Views;

/// <summary>
/// The first-run wizard. Code-behind only does what a view model can't: moving focus to each step's first field
/// (behavior 31) and reaching the clipboard for the passcode calculator's Copy button.
/// </summary>
public sealed partial class SetupWizardView : UserControl, IDisposable
{
    private SetupWizardViewModel? _viewModel;

    /// <summary>Creates the view.</summary>
    public SetupWizardView()
    {
        AvaloniaXamlLoader.Load(this);
        // Tunnel, so Enter means Next even where a radio button or check box has focus (behavior 31).
        AddHandler(KeyDownEvent, OnEnter, RoutingStrategies.Tunnel);
        if (this.FindControl<TextBox>("CallsignBox") is { } callsign)
        {
            callsign.TextChanged += (_, _) => TidyCallsign(callsign);
        }
        if (this.FindControl<Control>("MapPicker") is { } picker)
        {
            picker.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty && picker.IsVisible)
                {
                    ShowPicker();
                }
            };
        }
        if (this.FindControl<Button>("UseMapPointButton") is { } use)
        {
            use.Click += async (_, _) =>
            {
                if (_picker?.View is var (lat, lon, _) && _viewModel is { } vm)
                {
                    await vm.UseMapPointAsync(lat, lon);
                }
            };
        }
        DetachedFromVisualTree += (_, _) => Dispose();
    }

    /// <summary>Releases the picker's map and its tiles.</summary>
    public void Dispose()
    {
        _picker?.Dispose();
        _picker = null;
    }

    private Map.MapView? _picker;

    /// <summary>
    /// The picker's map, made the first time it shows (behavior 9.5): with the map's tiles, at the location confirmed
    /// so far, or the whole world.
    /// </summary>
    private void ShowPicker()
    {
        if (_picker is not null || DataContext is not SetupWizardViewModel { Map: { } map } vm)
        {
            return;
        }
        _picker = new Map.MapView();
        if (map is { Tiles: { } tiles, Provider: { } provider })
        {
            _picker.ShowTiles(tiles, provider);
        }
        if (this.FindControl<Border>("PickerHost") is { } host)
        {
            host.Child = _picker;
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (vm is { Latitude: { } lat, Longitude: { } lon })
            {
                _picker.ShowAround(lat, lon, 20);
            }
            else if (map.Home is var (homeLat, homeLon))
            {
                _picker.ShowAround(homeLat, homeLon, 200);
            }
            else
            {
                _picker.ShowWorld();
            }
        });
    }

    /// <summary>
    /// Upper-cases the callsign and drops spaces as it is typed (behavior 5). Done here as well as in the view model,
    /// because a text box ignores a value its binding sends back while it is still updating.
    /// </summary>
    private static void TidyCallsign(TextBox box)
    {
        var text = box.Text ?? string.Empty;
        var tidy = string.Concat(text.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
        if (tidy != text)
        {
            var caret = Math.Min(box.CaretIndex, tidy.Length);
            box.Text = tidy;
            box.CaretIndex = caret;
        }
    }

    private void OnEnter(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || _viewModel is not { } wizard)
        {
            return;
        }
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        // Buttons and links keep their own Enter, and so does the address field (Enter looks it up).
        if (focused is Button and not ToggleButton || focused is TextBox { Name: "AddressBox" })
        {
            return;
        }
        if (wizard.IsCalculatorOpen || wizard.IsConfirmingClose)
        {
            return;
        }
        var command = wizard.IsLastStep ? wizard.FinishCommand : wizard.NextCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }
        _viewModel = DataContext as SetupWizardViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
            ConnectClipboard();
            FocusStep();
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ConnectClipboard();
        FocusStep();
    }

    private void ConnectClipboard()
    {
        if (_viewModel is not null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            _viewModel.CopyToClipboard = text => clipboard.SetTextAsync(text);
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName == nameof(SetupWizardViewModel.IsLicensedOperator)
            && _viewModel is { IsLicensedOperator: true, Step: SetupStep.Callsign }
        )
        {
            Focus("CallsignBox"); // ticked: the callsign is next to type
        }
        if (e.PropertyName is nameof(SetupWizardViewModel.Step) or nameof(SetupWizardViewModel.Method))
        {
            FocusStep();
        }
        else if (
            e.PropertyName == nameof(SetupWizardViewModel.IsCalculatorOpen)
            && _viewModel?.IsCalculatorOpen == true
        )
        {
            Focus("LicensedBox");
        }
        else if (
            e.PropertyName == nameof(SetupWizardViewModel.IsConfirmingClose)
            && _viewModel?.IsConfirmingClose == true
        )
        {
            Focus("KeepEditingButton");
        }
    }

    /// <summary>Puts focus in the current step's first field, once it is shown.</summary>
    private void FocusStep()
    {
        if (_viewModel is null)
        {
            return;
        }
        Focus(
            _viewModel.Step switch
            {
                SetupStep.Callsign => _viewModel.IsLicensedOperator ? "CallsignBox" : "LicensedOperatorBox",
                SetupStep.Location => _viewModel.Method switch
                {
                    LocationMethod.Address => "AddressBox",
                    LocationMethod.Coordinates => "CoordinatesBox",
                    LocationMethod.Device => "FindMeButton",
                    _ => "LocatorBox",
                },
                SetupStep.Server => "RegionChoice",
                _ => "ReceiveOnlyChoice",
            }
        );
    }

    private void Focus(string name) =>
        Dispatcher.UIThread.Post(() => this.FindControl<Control>(name)?.Focus(), DispatcherPriority.Loaded);
}
