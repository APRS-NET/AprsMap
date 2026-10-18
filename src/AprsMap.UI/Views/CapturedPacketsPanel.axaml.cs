// SPDX-License-Identifier: AGPL-3.0-only
using System.Text;
using AprsMap.UI.Capture;
using AprsMap.UI.Localization;
using AprsMap.UI.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace AprsMap.UI.Views;

/// <summary>
/// The Captured packets panel (docs/features/F1.27-packet-capture.md). The view model does the work; this saves the
/// export (a save dialog on the desktop, a download in the browser) and opens the browser for Report on GitHub.
/// </summary>
public sealed partial class CapturedPacketsPanel : UserControl
{
    /// <summary>Creates the panel.</summary>
    public CapturedPacketsPanel()
    {
        InitializeComponent();
        ExportButton.Click += (_, _) => _ = ExportAsync();
        ReportButton.Click += (_, _) => _ = ReportAsync();
    }

    private CapturedPacketsViewModel? Captured => (DataContext as MainViewModel)?.CapturedPackets;

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _ = Captured?.LoadAsync();
    }

    /// <summary>Saves the export as Latin-1, so every byte is the packet's own (behavior 8).</summary>
    private async Task ExportAsync()
    {
        if (
            Captured is not { } captured
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage
        )
        {
            return;
        }
        if (await captured.ExportAsync() is not { } result)
        {
            return;
        }
        var text = Localizer.Instance;
        try
        {
            var file = await storage.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = text["Capture.Export"],
                    SuggestedFileName = result.FileName,
                    DefaultExtension = "log",
                    FileTypeChoices =
                    [
                        new FilePickerFileType(text["Capture.FileType"])
                        {
                            Patterns = ["*.log"],
                            MimeTypes = ["text/plain"],
                        },
                    ],
                }
            );
            if (file is null)
            {
                return;
            }
            await using (var stream = await file.OpenWriteAsync())
            {
                await stream.WriteAsync(Encoding.Latin1.GetBytes(result.Text));
            }
            captured.Exported(result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            captured.Notice = ex.Message;
        }
    }

    /// <summary>Opens a new APRS.NET issue in the browser with the note; nothing is uploaded (behavior 9).</summary>
    private async Task ReportAsync()
    {
        if (Captured is not { } captured || TopLevel.GetTopLevel(this)?.Launcher is not { } launcher)
        {
            return;
        }
        if (await captured.ReportUriAsync() is { } uri)
        {
            await launcher.LaunchUriAsync(uri);
        }
    }
}
