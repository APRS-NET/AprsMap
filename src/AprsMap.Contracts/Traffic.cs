// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>Which way a <see cref="TrafficEntry"/> went (docs/features/F1.28-source-console.md, behavior 2).</summary>
public enum TrafficDirection
{
    /// <summary>From the TNC or server.</summary>
    In,

    /// <summary>From this station.</summary>
    Out,
}

/// <summary>What a <see cref="TrafficEntry"/> is (F1.28 behavior 2), from APRS.NET's stable traffic kinds.</summary>
public enum TrafficKind
{
    /// <summary>A packet: a KISS frame that decoded as APRS, or an APRS-IS packet line.</summary>
    Packet,

    /// <summary>A KISS data frame that isn't APRS: another protocol, or not a UI frame.</summary>
    NotAprs,

    /// <summary>A KISS frame for another port of a multi-port TNC.</summary>
    OtherPort,

    /// <summary>A KISS command that isn't data.</summary>
    KissCommand,

    /// <summary>A frame or line dropped as damaged; <see cref="TrafficEntry.Detail"/> says why.</summary>
    DroppedFrame,

    /// <summary>Bytes outside any frame: text from a TNC not in KISS mode, or noise.</summary>
    Unframed,

    /// <summary>A packet this station sent.</summary>
    Sent,

    /// <summary>A command or bytes typed on the console's command line (F3.2 behavior 10).</summary>
    Typed,

    /// <summary>A TNC setup script sent a step.</summary>
    SetupSent,

    /// <summary>The TNC answered a setup step.</summary>
    SetupReceived,

    /// <summary>A setup step failed: what was expected didn't come.</summary>
    SetupFailed,

    /// <summary>The APRS-IS login line, with the passcode masked.</summary>
    LoginSent,

    /// <summary>The APRS-IS server's answer to the login.</summary>
    LoginResponse,

    /// <summary>An APRS-IS server comment or keepalive.</summary>
    ServerComment,

    /// <summary>An APRS-IS <c>#filter</c> command.</summary>
    FilterSent,

    /// <summary>Another APRS-IS server command.</summary>
    CommandSent,

    /// <summary>A line a GPS sent: an NMEA sentence (docs/features/F3.6-gps.md, behavior 6).</summary>
    GpsSentence,
}

/// <summary>
/// One thing a source sent or received, for the Source console (docs/features/F1.28-source-console.md, behavior 2).
/// </summary>
/// <param name="At">When.</param>
/// <param name="SourceId">The source.</param>
/// <param name="Direction">In or out.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Text">The packet as TNC-2 text, the line, or the text that came; null when there's none.</param>
/// <param name="Bytes">The bytes, at most <see cref="MaxBytes"/>; null for an APRS-IS line.</param>
/// <param name="Port">The KISS port, for a frame.</param>
/// <param name="Detail">Why a frame was dropped, or the setup step.</param>
/// <param name="EndsLine">False for text that stops without a line end, such as a TNC's <c>cmd:</c> prompt.</param>
/// <param name="Cut">Whether <paramref name="Bytes"/> was cut at <see cref="MaxBytes"/>.</param>
public sealed record TrafficEntry(
    DateTimeOffset At,
    string SourceId,
    TrafficDirection Direction,
    TrafficKind Kind,
    string? Text,
    byte[]? Bytes = null,
    int? Port = null,
    string? Detail = null,
    bool EndsLine = true,
    bool Cut = false
)
{
    /// <summary>The most bytes an entry keeps.</summary>
    public const int MaxBytes = 1024;

    /// <summary>Whether it's a problem: not APRS, another port, dropped, unframed, or a failed setup step (behavior 5).</summary>
    public bool IsProblem =>
        Kind
            is TrafficKind.NotAprs
                or TrafficKind.OtherPort
                or TrafficKind.DroppedFrame
                or TrafficKind.Unframed
                or TrafficKind.SetupFailed;
}

/// <summary>What to send on a TNC's command line (docs/features/F3.2-built-in-tncs.md, behavior 10).</summary>
public enum TncCommandKind
{
    /// <summary>A line of text, with a carriage return.</summary>
    Line,

    /// <summary>Bytes given as hex.</summary>
    Bytes,

    /// <summary>The TNC type's script into KISS.</summary>
    EnterKiss,

    /// <summary>The TNC type's script out of KISS, or the standard KISS exit frame.</summary>
    LeaveKiss,
}

/// <summary>A command for a TNC (F3.2 behavior 10).</summary>
/// <param name="Kind">What to send.</param>
/// <param name="Text">The line, or the hex bytes such as <c>C0 FF C0</c>; null for the scripts.</param>
public sealed record TncCommand(TncCommandKind Kind, string? Text = null);
