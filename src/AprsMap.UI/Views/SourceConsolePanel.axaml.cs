// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Specialized;
using AprsMap.UI.Traffic;
using AprsMap.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AprsMap.UI.Views;

/// <summary>
/// The Source console panel (docs/features/F1.28-source-console.md). The view model does the work; this keeps the list
/// at its newest line while live, pauses when the viewer scrolls up (behavior 4), and copies (behavior 3).
/// </summary>
public sealed partial class SourceConsolePanel : UserControl
{
    private ScrollViewer? _scroller;
    private bool _viewerScrolling;
    private bool _scrollBarHeld;
    private SourceConsoleViewModel? _watched;

    /// <summary>Creates the panel.</summary>
    public SourceConsolePanel()
    {
        InitializeComponent();
        LineList.AddHandler(PointerWheelChangedEvent, (_, _) => ViewerScrolls(), RoutingStrategies.Tunnel, true);
        LineList.AddHandler(ScrollGestureEvent, (_, _) => ViewerScrolls(), RoutingStrategies.Bubble, true);
        LineList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel, true);
        LineList.AddHandler(PointerReleasedEvent, (_, _) => _scrollBarHeld = false, RoutingStrategies.Tunnel, true);
        LineList.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        LineList.TemplateApplied += (_, _) => WatchScrolling();
        CopyButton.Click += (_, _) => _ = CopyAsync();
        PausedButton.Click += (_, _) => _scroller?.ScrollToEnd();
    }

    private SourceConsoleViewModel? Console => (DataContext as MainViewModel)?.Console;

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_watched is not null)
        {
            _watched.Lines.CollectionChanged -= OnLinesChanged;
        }
        _watched = Console;
        if (_watched is not null)
        {
            _watched.Lines.CollectionChanged += OnLinesChanged;
        }
    }

    /// <summary>While live, new lines keep the list at its bottom.</summary>
    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            ShowNewest();
        }
    }

    /// <summary>Brings the newest line into view, once the list has laid out, unless the console is paused.</summary>
    private void ShowNewest() =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (_watched is { IsPaused: false, Lines: [.., var newest] })
                {
                    LineList.ScrollIntoView(newest);
                }
            },
            DispatcherPriority.Background
        );

    private void WatchScrolling()
    {
        if (_scroller is not null)
        {
            _scroller.ScrollChanged -= OnScrollChanged;
        }
        _scroller = LineList.Scroll as ScrollViewer;
        if (_scroller is not null)
        {
            _scroller.ScrollChanged += OnScrollChanged;
        }
        ShowNewest(); // the lines kept before the panel opened
    }

    /// <summary>The viewer scrolling up pauses the console; back at the bottom it catches up (behavior 4).</summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scroller is not { } scroller || e.OffsetDelta.Y == 0 || Console is not { } console)
        {
            return;
        }
        var atBottom = scroller.Offset.Y + scroller.Viewport.Height >= scroller.Extent.Height - 1;
        if (_viewerScrolling || _scrollBarHeld || atBottom)
        {
            console.Scrolled(atBottom);
        }
    }

    private void ViewerScrolls()
    {
        if (!_viewerScrolling)
        {
            _viewerScrolling = true;
            Dispatcher.UIThread.Post(() => _viewerScrolling = false, DispatcherPriority.Background);
        }
    }

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null)
        {
            _scrollBarHeld = true;
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            ViewerScrolls();
        }
        else if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = CopyAsync();
            e.Handled = true;
        }
    }

    /// <summary>Copies the chosen lines, or every line shown when none is chosen (behavior 3).</summary>
    private async Task CopyAsync()
    {
        if (Console is not { } console || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }
        var chosen = LineList.SelectedItems?.OfType<ConsoleLine>().ToList() ?? [];
        var lines = chosen.Count > 0 ? console.Lines.Where(chosen.Contains) : console.Lines;
        await clipboard.SetTextAsync(SourceConsoleViewModel.CopyText(lines));
    }
}
