// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Packets;

namespace AprsMap.Core;

/// <summary>
/// The logging categories of each area (docs/features/F1.32-logging.md, behavior 4). A level rule on a category covers
/// every category under it (<c>AprsMap.Tiles</c> covers <c>AprsMap.Tiles.TileService</c>), so a new part of the app joins
/// an area by logging under one of these.
/// </summary>
public static class LogAreas
{
    /// <summary>The engine's connections: APRS-IS, KISS TNCs, serial ports and TNC setup.</summary>
    public const string Connections = "AprsMap.Connections";

    /// <summary>Packets received and sent, and what's wrong with them.</summary>
    public const string Packets = "AprsMap.Packets";

    /// <summary>Messages and bulletins.</summary>
    public const string Messages = "AprsMap.Messages";

    /// <summary>Transmit and beacons.</summary>
    public const string Transmit = "AprsMap.Transmit";

    /// <summary>Updates and the device list.</summary>
    public const string Updates = "AprsMap.Updates";

    /// <summary>The categories that make up <paramref name="area"/>.</summary>
    public static IReadOnlyList<string> Categories(LogArea area) =>
        area switch
        {
            LogArea.Connections => [Connections],
            LogArea.Packets => [Packets],
            LogArea.Messages => [Messages],
            LogArea.Transmit => [Transmit],
            // The tile service, and the HTTP client's own lines for each tile (four at Information).
            LogArea.Tiles => ["AprsMap.Tiles", "System.Net.Http.HttpClient.AprsMap.Tiles"],
            LogArea.Updates =>
            [
                Updates,
                "System.Net.Http.HttpClient." + Engine.UpdatesHttpClient,
                "System.Net.Http.HttpClient." + Engine.DeviceListHttpClient,
            ],
            LogArea.Web => ["AprsMap.Gateway", "Microsoft.AspNetCore"],
            LogArea.AprsNet => ["AprsNet"],
            _ => [],
        };

    /// <summary>The .NET level for <paramref name="level"/>.</summary>
    public static Microsoft.Extensions.Logging.LogLevel ToLogLevel(LogLevelSetting level) =>
        level switch
        {
            LogLevelSetting.Errors => Microsoft.Extensions.Logging.LogLevel.Error,
            LogLevelSetting.Warnings => Microsoft.Extensions.Logging.LogLevel.Warning,
            LogLevelSetting.Debug => Microsoft.Extensions.Logging.LogLevel.Debug,
            LogLevelSetting.Trace => Microsoft.Extensions.Logging.LogLevel.Trace,
            _ => Microsoft.Extensions.Logging.LogLevel.Information,
        };

    /// <summary>
    /// <paramref name="packet"/> as a log shows it: its raw text, with a message's or bulletin's text left out unless
    /// <paramref name="messageTexts"/> (behavior 5.2). The addressee and message ID stay, so a line can still be matched
    /// with its ack.
    /// </summary>
    public static string Packet(AprsPacket packet, bool messageTexts)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var text = packet.ToString();
        if (
            messageTexts
            || packet.Payload
                is not MessagePayload
                {
                    Kind: MessageKind.Message or MessageKind.Bulletin or MessageKind.Announcement,
                    Text.Length: > 0,
                }
        )
        {
            return text;
        }
        // TNC2 message format: header, "::", the addressee padded to 9, ":", then the text and any "{id".
        var colons = text.IndexOf("::", StringComparison.Ordinal);
        var start = colons < 0 ? -1 : colons + 2 + 9 + 1;
        if (start < 0 || start > text.Length)
        {
            return text;
        }
        var brace = text.LastIndexOf('{');
        return text[..start] + "(text not logged)" + (brace >= start ? text[brace..] : string.Empty);
    }
}

/// <summary>
/// The logging settings in effect, shared by the engine, which keeps them with the station's settings, and the host,
/// which applies them to the levels, the log file and the OTLP targets (docs/features/F1.32-logging.md). The host sets
/// what it alone knows (the default folder, what the environment sets) when it starts.
/// </summary>
public sealed class LoggingState
{
    private LoggingSettings _current = LoggingSettings.Default;

    /// <summary>The settings in effect.</summary>
    public LoggingSettings Current => Volatile.Read(ref _current);

    /// <summary>Raised after <see cref="Current"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Where the log file goes when no folder is chosen (behavior 8).</summary>
    public string DefaultFolder { get; set; } = string.Empty;

    /// <summary>Whether the log file is on unless turned off: the desktop, not a gateway (behavior 7).</summary>
    public bool FileOnByDefault { get; set; } = true;

    /// <summary>Whether the environment sets the overall level (behavior 1).</summary>
    public bool EnvironmentLevel { get; set; }

    /// <summary>Areas whose level the environment sets.</summary>
    public IReadOnlyList<LogArea> EnvironmentAreas { get; set; } = [];

    /// <summary>The OTLP endpoint the environment adds as a target (behavior 14), or null.</summary>
    public string? EnvironmentEndpoint { get; set; }

    /// <summary>The OTLP targets the app started with: a change to them applies after a restart (behavior 2).</summary>
    public IReadOnlyList<OtlpTarget> StartupTargets { get; set; } = [];

    /// <summary>Whether the log file is written, from the settings and the default for this host.</summary>
    public bool FileOn => Current.EffectiveFile.On ?? FileOnByDefault;

    /// <summary>The log file's folder in effect.</summary>
    public string Folder =>
        string.IsNullOrWhiteSpace(Current.EffectiveFile.Folder) ? DefaultFolder : Current.EffectiveFile.Folder;

    /// <summary>Uses <paramref name="settings"/> from now on, telling the host when they changed.</summary>
    public void Update(LoggingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (Same(Interlocked.Exchange(ref _current, settings), settings))
        {
            return;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What the settings can't show by themselves, for Settings › Logging.</summary>
    public LoggingStatus Status() =>
        new(
            DefaultFolder,
            FileOnByDefault,
            EnvironmentLevel,
            EnvironmentAreas,
            EnvironmentEndpoint,
            RestartPending: !SameTargets(StartupTargets, Current.TargetList)
        );

    /// <summary>Whether two settings say the same, comparing their lists by content (records compare them by reference).</summary>
    public static bool Same(LoggingSettings a, LoggingSettings b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Level == b.Level
            && a.Packets == b.Packets
            && a.MessageTexts == b.MessageTexts
            && a.EffectiveFile == b.EffectiveFile
            && Enum.GetValues<LogArea>().All(area => a.OwnLevel(area) == b.OwnLevel(area))
            && SameTargets(a.TargetList, b.TargetList);
    }

    /// <summary>Whether two target lists are the same, headers and their values included.</summary>
    public static bool SameTargets(IReadOnlyList<OtlpTarget> a, IReadOnlyList<OtlpTarget> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Count == b.Count
            && a.Zip(b)
                .All(p =>
                    p.First with { Headers = null } == p.Second with { Headers = null }
                    && p.First.HeaderList.SequenceEqual(p.Second.HeaderList)
                );
    }
}
