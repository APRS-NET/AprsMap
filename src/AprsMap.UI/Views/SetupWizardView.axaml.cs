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
public sealed partial class SetupWizardView : UserControl
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
                SetupStep.Callsign => "CallsignBox",
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
