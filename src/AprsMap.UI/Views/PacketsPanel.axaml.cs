// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Localization;
using AprsMap.UI.Packets;
using AprsMap.UI.Packets.Export;
using AprsMap.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace AprsMap.UI.Views;

/// <summary>
/// The Packets panel (docs/features/F1.8-packet-list.md). The view model does the work; this wires what needs the
/// visual tree: keys, the clipboard, the scroll position, the map selection and the save dialog.
/// </summary>
public sealed partial class PacketsPanel : UserControl
{
    private ScrollViewer? _scroller;

    /// <summary>Creates the panel.</summary>
    public PacketsPanel()
    {
        InitializeComponent();
        PacketList.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
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

    /// <summary>Scrolling down from the newest packets pauses the list; returning to the top resumes it (behavior 6).</summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scroller is { } scroller && e.OffsetDelta.Y != 0)
        {
            List?.Scrolled(atTop: scroller.Offset.Y <= 1);
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (List is not { } list)
        {
            return;
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

    /// <summary>A row clicked selects its station on the map (behavior 8).</summary>
    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
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
            item.Click += (_, _) => _ = SaveAsync(exporter);
            menu.Items.Add(item);
        }
    }

    /// <summary>
    /// Saves the rows shown in <paramref name="exporter"/>'s format where the viewer picks: a save dialog, or a download
    /// (behavior 10).
    /// </summary>
    private async Task SaveAsync(PacketExporter exporter)
    {
        if (List is not { } list || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
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
