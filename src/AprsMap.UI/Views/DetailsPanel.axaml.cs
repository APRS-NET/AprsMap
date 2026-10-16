// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace AprsMap.UI.Views;

/// <summary>The Details panel (docs/features/F1.6-station-details.md).</summary>
public sealed partial class DetailsPanel : UserControl
{
    /// <summary>Creates the panel.</summary>
    public DetailsPanel()
    {
        InitializeComponent();
        CopyPositionButton.Click += OnCopyPosition;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Escape closes the panel and clears the selection (behavior 1).</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainViewModel { SelectedStation: not null } main)
        {
            main.SelectedStation = null;
            e.Handled = true;
        }
    }

    private async void OnCopyPosition(object? sender, RoutedEventArgs e)
    {
        if (
            DataContext is MainViewModel main
            && main.Details.PositionCopy() is { } text
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard
        )
        {
            await clipboard.SetTextAsync(text);
        }
    }
}
