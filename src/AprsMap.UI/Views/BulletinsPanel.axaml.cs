// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AprsMap.UI.Views;

/// <summary>
/// The Bulletins panel (docs/features/F2.5-bulletins.md). Seeing it clears the new marks (behavior 2): when it shows, and
/// when the pointer or focus comes to it.
/// </summary>
public sealed partial class BulletinsPanel : UserControl
{
    /// <summary>Creates the panel.</summary>
    public BulletinsPanel()
    {
        InitializeComponent();
        PointerEntered += (_, _) => Seen();
        GotFocus += (_, _) => Seen();
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Seen();
    }

    private void Seen() => (DataContext as MainViewModel)?.Bulletins.MarkSeen();
}
