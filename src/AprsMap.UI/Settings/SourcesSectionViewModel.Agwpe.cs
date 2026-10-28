// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using System.Globalization;
using AprsMap.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Settings;

/// <summary>
/// An AGWPE source in Settings › Sources (docs/features/F3.8-agwpe.md, behavior 1): its host and port as a KISS over TCP
/// TNC's, and its radio port, typed or picked from the server's list, written from 1 as the server numbers them.
/// </summary>
public sealed partial class SourcesSectionViewModel
{
    /// <summary>Whether the form is for an AGWPE source.</summary>
    public bool IsAgwpe => EditKind == SourceKind.Agwpe;

    /// <summary>Whether the form shows a KISS TNC's port, from 0; an AGWPE source has its radio port instead.</summary>
    public bool ShowsTncPort => IsKiss && !IsAgwpe;

    /// <summary>The radio port as typed or picked, from 1: "1", or "1 · first soundcard mono" from the list.</summary>
    [ObservableProperty]
    public partial string EditAgwpePort { get; set; } = string.Empty;

    /// <summary>The server's radio ports, as "1 · first soundcard mono", once asked.</summary>
    public ObservableCollection<string> AgwpePorts { get; } = [];

    /// <summary>"Dire Wolf's server, version 2005.127", or that it didn't answer; null before asking.</summary>
    [ObservableProperty]
    public partial string? AgwpeNote { get; private set; }

    /// <summary>Asks the server at the host and port for its radio ports (behavior 1).</summary>
    [RelayCommand]
    private async Task RefreshAgwpePortsAsync()
    {
        var host = EditHost.Trim();
        if (host.Length == 0)
        {
            AgwpeNote = _text["Settings.Sources.Agwpe.NoHost"];
            return;
        }
        var port =
            EditPort.Trim().Length == 0 ? SourceSettings.DefaultAgwpePort
            : int.TryParse(EditPort.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var typed) ? typed
            : -1;
        AgwpeNote = _text["Settings.Sources.Agwpe.Asking"];
        AgwpeServer server;
        try
        {
            server = await _engine.GetAgwpePortsAsync(host, port);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            AgwpeNote = _text.Describe(ex);
            return;
        }
        AgwpePorts.Clear();
        foreach (var radio in server.Ports)
        {
            AgwpePorts.Add(Label(radio));
        }
        AgwpeNote = !server.Reached
            ? _text.Format(
                "Settings.Sources.Agwpe.Unreached",
                new Dictionary<string, object?> { ["address"] = $"{host}:{port}" }
            )
            : _text.Format(
                "Settings.Sources.Agwpe.Answered",
                new Dictionary<string, object?> { ["version"] = server.Version ?? "?", ["count"] = server.Ports.Count }
            );
        if (EditAgwpePort.Trim().Length == 0 && AgwpePorts.Count > 0)
        {
            EditAgwpePort = AgwpePorts[0]; // its first, as most have one
        }
    }

    /// <summary>"2 · second soundcard": a radio port as the server numbers it, with its description.</summary>
    private static string Label(AgwpeRadioPort port) =>
        port.Description.Length > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{port.Number + 1} · {port.Description}")
            : (port.Number + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The radio port typed or picked, from 0 as kept; null when nothing's typed (the first), and -1 when it isn't a
    /// number from 1, for the engine's check to word.
    /// </summary>
    private int? AgwpeRadioPort()
    {
        var text = EditAgwpePort.Trim();
        if (text.Length == 0)
        {
            return null;
        }
        var digits = new string([.. text.TakeWhile(char.IsAsciiDigit)]);
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 1
            ? number - 1
            : -1;
    }

    /// <summary>The radio port of the source being edited, from 1; empty for a new source (null).</summary>
    private void ShowAgwpePort(SourceSettings? source)
    {
        AgwpePorts.Clear();
        AgwpeNote = null;
        EditAgwpePort = source is { Kind: SourceKind.Agwpe }
            ? ((source.TncPort ?? 0) + 1).ToString(CultureInfo.InvariantCulture)
            : string.Empty;
    }
}
