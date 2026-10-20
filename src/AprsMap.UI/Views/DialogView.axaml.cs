// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Settings;
using AprsMap.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace AprsMap.UI.Views;

/// <summary>
/// A dialog over the main view; everything is in its view model and templates. Code-behind only hands About the
/// clipboard, for Copy (docs/features/F1.31-files.md, behavior 3).
/// </summary>
public partial class DialogView : UserControl
{
    public DialogView() => InitializeComponent();

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        GiveClipboard();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        GiveClipboard();
    }

    private void GiveClipboard()
    {
        if (
            DataContext is DialogViewModel { Content: AboutViewModel about }
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard
        )
        {
            about.CopyToClipboard = text => clipboard.SetTextAsync(text);
        }
    }
}
