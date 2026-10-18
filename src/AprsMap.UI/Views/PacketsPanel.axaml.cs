// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Localization;
using AprsMap.UI.Packets;
using AprsMap.UI.Packets.Export;
using AprsMap.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AprsMap.UI.Views;

/// <summary>
/// The Packets panel (docs/features/F1.8-packet-list.md). The view model does the work; this wires what needs the
/// visual tree: keys, the clipboard, the scroll position, the map selection and the save dialog.
/// </summary>
public sealed partial class PacketsPanel : UserControl
{
    private ScrollViewer? _scroller;

    /// <summary>The viewer is scrolling: the wheel, a touch, a key or a scroll bar held. Only this pauses the list.</summary>
    private bool _viewerScrolling;

    private bool _scrollBarHeld;

    /// <summary>Creates the panel.</summary>
    public PacketsPanel()
    {
        InitializeComponent();
        PacketList.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        PacketList.AddHandler(PointerWheelChangedEvent, (_, _) => ViewerScrolls(), RoutingStrategies.Tunnel, true);
        PacketList.AddHandler(ScrollGestureEvent, (_, _) => ViewerScrolls(), RoutingStrategies.Bubble, true);
        PacketList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel, true);
        PacketList.AddHandler(PointerReleasedEvent, (_, _) => _scrollBarHeld = false, RoutingStrategies.Tunnel, true);
        PacketList.AddHandler(
            PointerCaptureLostEvent,
            (_, _) => _scrollBarHeld = false,
            RoutingStrategies.Tunnel,
            true
        );
        PacketList.SelectionChanged += OnSelectionChanged;
        PacketList.TemplateApplied += (_, _) => WatchScrolling();
        CopyItem.Click += (_, _) => _ = CopyAsync();
        OnlyStationItem.Click += (_, _) => OnlyStation();
        if (SaveButton.Flyout is MenuFlyout formats)
        {
            // Kept filled, never filled as it opens: on the desktop the menu is a window of its own, created as it
            // opens, and one refilled then closed at once. It follows the language instead.
            FillSaveMenu(formats);
            Localizer.Instance.LanguageChanged += (_, _) => FillSaveMenu(formats);
        }
    }

    private PacketListViewModel? _watched;

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_watched is not null)
        {
            _watched.Resumed -= OnResumed;
        }
        _watched = List;
        if (_watched is not null)
        {
            _watched.Resumed += OnResumed;
        }
    }

    /// <summary>Resuming shows the newest packets: the list goes back to its top (F1.8 behavior 6).</summary>
    private void OnResumed(object? sender, EventArgs e) => _scroller?.ScrollToHome();

    private MainViewModel? Main => DataContext as MainViewModel;

    private PacketListViewModel? List => Main?.PacketList;

    private IEnumerable<PacketRow> SelectedRows => PacketList.SelectedItems?.OfType<PacketRow>() ?? [];

    private void WatchScrolling()
    {
        if (_scroller is not null)
        {
            _scroller.ScrollChanged -= OnScrollChanged;
        }
        _scroller = PacketList.Scroll as ScrollViewer;
        if (_scroller is not null)
        {
            _scroller.ScrollChanged += OnScrollChanged;
        }
    }

    /// <summary>
    /// The viewer scrolling down from the newest packets pauses the list; returning to the top resumes it (behavior 6).
    /// The list moving by itself (rows going in above a focused row, the window losing focus) doesn't pause it: while
    /// it's live, it's put back at the top.
    /// </summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scroller is not { } scroller || e.OffsetDelta.Y == 0 || List is not { } list)
        {
            return;
        }
        var atTop = scroller.Offset.Y <= 1;
        if (_viewerScrolling || _scrollBarHeld)
        {
            list.Scrolled(atTop);
        }
        else if (!list.IsPaused && !atTop)
        {
            scroller.ScrollToHome();
        }
    }

    /// <summary>Marks the scrolling the viewer starts, until the list has moved for it.</summary>
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
        if (List is not { } list)
        {
            return;
        }
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            ViewerScrolls();
        }
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None)
        {
            list.TogglePause(); // Space pauses and resumes (behavior 6)
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && PacketList.SelectedItem is PacketRow row)
        {
            row.IsExpanded = !row.IsExpanded;
            e.Handled = true;
        }
        else if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = CopyAsync();
            e.Handled = true;
        }
    }

    /// <summary>A row clicked pauses the list (behavior 6) and selects its station on the map (behavior 8).</summary>
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0)
        {
            List?.RowSelected();
        }
        if (Main is { } main && e.AddedItems.OfType<PacketRow>().LastOrDefault() is { Source.Length: > 0 } row)
        {
            main.SelectPacket(row.Packet);
        }
    }

    private async Task CopyAsync()
    {
        var rows = SelectedRows.ToList();
        if (rows.Count > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(PacketListViewModel.CopyText(rows));
        }
    }

    private void OnlyStation()
    {
        if (List is { } list && PacketList.SelectedItem is PacketRow { Source.Length: > 0 } row)
        {
            list.OnlyStation(row.Source);
        }
    }

    /// <summary>The Save menu: a format a line, named in the viewer's language.</summary>
    private void FillSaveMenu(MenuFlyout menu)
    {
        menu.Items.Clear();
        var text = Localizer.Instance;
        foreach (var exporter in PacketExporter.All)
        {
            var item = new MenuItem { Header = text[exporter.NameKey] };
            item.Click += (_, _) =>
                _ =
                    List is { } list && TopLevel.GetTopLevel(this) is { } topLevel
                        ? SaveAsync(topLevel, list, exporter)
                        : Task.CompletedTask;
            menu.Items.Add(item);
        }
    }

    /// <summary>
    /// Saves the rows shown in <paramref name="exporter"/>'s format where the viewer picks: a save dialog, or a download
    /// (behavior 10).
    /// </summary>
    internal static async Task SaveAsync(TopLevel topLevel, PacketListViewModel list, PacketExporter exporter)
    {
        if (topLevel.StorageProvider is not { CanSave: true } storage)
        {
            return;
        }
        var text = Localizer.Instance;
        list.Notice = null;
        try
        {
            var file = await storage.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = text["Packets.SaveTitle"],
                    SuggestedFileName = list.SaveName(exporter),
                    DefaultExtension = exporter.Extension,
                    FileTypeChoices =
                    [
                        new FilePickerFileType(text[exporter.NameKey])
                        {
                            Patterns = ["*." + exporter.Extension],
                            MimeTypes = [exporter.MediaType],
                        },
                    ],
                }
            );
            if (file is null)
            {
                return;
            }
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(list.SaveText(exporter));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            list.Notice = text.Format(
                "Packets.SaveFailed",
                new Dictionary<string, object?> { ["reason"] = ex.Message }
            );
        }
    }
}
