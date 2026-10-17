// SPDX-License-Identifier: AGPL-3.0-only
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AprsMap.UI.Views;

/// <summary>The Messages panel (docs/features/F2.4-messaging.md).</summary>
public sealed partial class MessagesPanel : UserControl
{
    /// <summary>Creates the panel.</summary>
    public MessagesPanel()
    {
        InitializeComponent();
        // Enter in the compose box sends, as in any chat.
        ComposeBox.AddHandler(KeyDownEvent, OnComposeKey, RoutingStrategies.Tunnel);
        NewCallsignBox.AddHandler(KeyDownEvent, OnNewCallsignKey, RoutingStrategies.Tunnel);
        ThreadList.SizeChanged += (_, _) => ThreadScroll.ScrollToEnd(); // the newest message in view
    }

    private void OnComposeKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && SendButton.Command is { } send)
        {
            if (send.CanExecute(null))
            {
                send.Execute(null);
            }
            e.Handled = true;
        }
    }

    private void OnNewCallsignKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && StartButton.Command is { } start)
        {
            if (start.CanExecute(null))
            {
                start.Execute(null);
                ComposeBox.Focus();
            }
            e.Handled = true;
        }
    }
}
