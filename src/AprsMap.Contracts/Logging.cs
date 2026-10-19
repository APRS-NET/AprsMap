// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>How much is logged (docs/features/F1.32-logging.md, behavior 3), from least to most.</summary>
public enum LogLevelSetting
{
    /// <summary>Errors only.</summary>
    Errors,

    /// <summary>Warnings and errors.</summary>
    Warnings,

    /// <summary>What the app does, as well as warnings and errors: the default.</summary>
    Information,

    /// <summary>Details for chasing a problem.</summary>
    Debug,

    /// <summary>Everything.</summary>
    Trace,
}

/// <summary>A part of the app whose log level can be set on its own (behavior 4).</summary>
public enum LogArea
{
    /// <summary>APRS-IS, KISS TNCs, serial ports and TNC setup.</summary>
    Connections,

    /// <summary>Parsing, duplicates and what's unexpected; each packet, with Log every packet.</summary>
    Packets,

    /// <summary>Messages and bulletins.</summary>
    Messages,

    /// <summary>Transmit and beacons.</summary>
    Transmit,

    /// <summary>Map tiles: Warnings unless set (behavior 4.5).</summary>
    Tiles,

    /// <summary>Updates and the device list.</summary>
    Updates,

    /// <summary>The gateway's web side and sign-in.</summary>
    Web,

    /// <summary>APRS.NET's own log.</summary>
    AprsNet,
}

/// <summary>An area's level: <see cref="Level"/> null means the overall level.</summary>
/// <param name="Area">The area.</param>
/// <param name="Level">Its own level, or null for "Same as overall".</param>
public sealed record LogAreaLevel(LogArea Area, LogLevelSetting? Level);

/// <summary>The log file (behaviors 7–12).</summary>
/// <param name="On">Write a log file; null for the default, on for the desktop and off for a gateway (behavior 7).</param>
/// <param name="Folder">Where; null for the default, a <c>logs</c> folder in the Data folder (behavior 8).</param>
/// <param name="MaxFileMegabytes">A day's file goes on in a new one past this size (behavior 9).</param>
/// <param name="KeepDays">Files older than this are deleted (behavior 10).</param>
/// <param name="TotalMegabytes">The oldest files are deleted past this total (behavior 11).</param>
public sealed record LogFileSettings(
    bool? On = null,
    string? Folder = null,
    int MaxFileMegabytes = LogFileSettings.DefaultMaxFileMegabytes,
    int KeepDays = LogFileSettings.DefaultKeepDays,
    int TotalMegabytes = LogFileSettings.DefaultTotalMegabytes
)
{
    /// <summary>10 MB a file (decision 9).</summary>
    public const int DefaultMaxFileMegabytes = 10;

    /// <summary>Two weeks.</summary>
    public const int DefaultKeepDays = 14;

    /// <summary>100 MB in all.</summary>
    public const int DefaultTotalMegabytes = 100;

    /// <summary>The smallest and largest a file may be set to, in MB.</summary>
    public static (int Min, int Max) FileMegabytesRange => (1, 100);

    /// <summary>The fewest and most days files may be kept.</summary>
    public static (int Min, int Max) KeepDaysRange => (1, 365);

    /// <summary>The smallest and largest total, in MB.</summary>
    public static (int Min, int Max) TotalMegabytesRange => (10, 10_000);

    /// <summary>The defaults.</summary>
    public static LogFileSettings Default { get; } = new();
}

/// <summary>The OTLP protocol a target speaks (behavior 13.3).</summary>
public enum OtlpProtocol
{
    /// <summary>gRPC, usually on port 4317.</summary>
    Grpc,

    /// <summary>HTTP with protobuf, usually on port 4318.</summary>
    HttpProtobuf,
}

/// <summary>Which signals a target receives (behavior 13.4).</summary>
[Flags]
public enum OtlpSignals
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary>Logs.</summary>
    Logs = 1,

    /// <summary>Metrics.</summary>
    Metrics = 2,

    /// <summary>Traces.</summary>
    Traces = 4,

    /// <summary>Logs, metrics and traces.</summary>
    All = Logs | Metrics | Traces,
}

/// <summary>
/// A header sent to an OTLP target, such as an API key (behavior 13.5). The engine never hands a value back:
/// <see cref="Value"/> is null in what it returns, with <see cref="HasValue"/> saying one is stored; a save with a null
/// value keeps the stored one.
/// </summary>
/// <param name="Name">The header's name.</param>
/// <param name="Value">Its value when setting it; null when keeping the stored one, and always null from the engine.</param>
/// <param name="HasValue">Set by the engine: a value is stored.</param>
public sealed record OtlpHeader(string Name, string? Value = null, bool HasValue = false);

/// <summary>An OpenTelemetry endpoint the station's logs, metrics or traces go to (behavior 13).</summary>
/// <param name="Id">Stable identifier; never shown.</param>
/// <param name="Name">What the user calls it.</param>
/// <param name="On">Whether it's used.</param>
/// <param name="Endpoint">Its address, such as <c>http://localhost:4317</c>.</param>
/// <param name="Protocol">gRPC or HTTP/protobuf.</param>
/// <param name="Signals">What it receives.</param>
/// <param name="Headers">Headers sent with each export, such as an API key.</param>
public sealed record OtlpTarget(
    string Id,
    string Name,
    bool On,
    string Endpoint,
    OtlpProtocol Protocol = OtlpProtocol.Grpc,
    OtlpSignals Signals = OtlpSignals.All,
    IReadOnlyList<OtlpHeader>? Headers = null
)
{
    /// <summary>The headers, none when there are none.</summary>
    public IReadOnlyList<OtlpHeader> HeaderList => Headers ?? [];
}

/// <summary>
/// Logging and telemetry (docs/features/F1.32-logging.md): the overall level, each area's, the privacy switches, the
/// log file and the OTLP targets. Kept with the app's settings; the environment wins over each of them (behavior 1).
/// </summary>
/// <param name="Level">The overall level (behavior 3).</param>
/// <param name="Areas">Areas with their own level; an area not listed follows its default (behavior 4).</param>
/// <param name="Packets">Log every packet received or sent, as its raw text (behavior 5.1).</param>
/// <param name="MessageTexts">Log the text of messages and bulletins (behavior 5.2).</param>
/// <param name="File">The log file; null for the defaults.</param>
/// <param name="Targets">The OTLP targets; null for none.</param>
public sealed record LoggingSettings(
    LogLevelSetting Level = LogLevelSetting.Information,
    IReadOnlyList<LogAreaLevel>? Areas = null,
    bool Packets = false,
    bool MessageTexts = false,
    LogFileSettings? File = null,
    IReadOnlyList<OtlpTarget>? Targets = null
)
{
    /// <summary>The defaults: Information, Map tiles at Warnings, nothing private, the default file, no targets.</summary>
    public static LoggingSettings Default { get; } = new();

    /// <summary>The log file's settings in effect.</summary>
    public LogFileSettings EffectiveFile => File ?? LogFileSettings.Default;

    /// <summary>The OTLP targets, none when there are none.</summary>
    public IReadOnlyList<OtlpTarget> TargetList => Targets ?? [];

    /// <summary>
    /// <paramref name="area"/>'s own level, or null when it follows the overall level: as set, or by default Warnings for
    /// Map tiles (decision 7) and the overall level for the rest.
    /// </summary>
    public LogLevelSetting? OwnLevel(LogArea area) =>
        Areas?.FirstOrDefault(a => a.Area == area) is { } set ? set.Level
        : area == LogArea.Tiles ? LogLevelSetting.Warnings
        : null;

    /// <summary>The level <paramref name="area"/> logs at: its own, or the overall level.</summary>
    public LogLevelSetting LevelOf(LogArea area) => OwnLevel(area) ?? Level;
}

/// <summary>
/// What the logging settings can't show by themselves (behaviors 1, 2, 8, 14): what the environment sets, where the file
/// goes by default, and whether a change waits for a restart.
/// </summary>
/// <param name="DefaultFolder">Where the log file goes when no folder is chosen.</param>
/// <param name="FileOnByDefault">Whether the log file is on unless turned off: the desktop, not a gateway.</param>
/// <param name="EnvironmentLevel">The environment sets the overall level, so the setting doesn't apply.</param>
/// <param name="EnvironmentAreas">Areas whose level the environment sets.</param>
/// <param name="EnvironmentEndpoint">The OTLP endpoint the environment adds as a target, read only; null for none.</param>
/// <param name="RestartPending">An OTLP target changed since the app started: it applies after a restart.</param>
public sealed record LoggingStatus(
    string DefaultFolder,
    bool FileOnByDefault,
    bool EnvironmentLevel = false,
    IReadOnlyList<LogArea>? EnvironmentAreas = null,
    string? EnvironmentEndpoint = null,
    bool RestartPending = false
);
