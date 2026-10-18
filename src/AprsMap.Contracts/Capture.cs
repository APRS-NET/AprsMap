// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>Packet capture's settings (docs/features/F1.27-packet-capture.md, behaviors 4, 5).</summary>
/// <param name="On">Whether unexpected packets are recorded; off by default (decision 1).</param>
/// <param name="MaxMegabytes">The most the files may take, in MB (decision 6: 50).</param>
/// <param name="Days">How many days are kept (decision 6: 30).</param>
/// <param name="UserDefined">
/// Whether user-defined packets (<c>{</c>), whose format APRS.NET never decodes, are recorded too (decision 7: no).
/// </param>
public sealed record CaptureSettings(
    bool On = false,
    int MaxMegabytes = CaptureSettings.DefaultMegabytes,
    int Days = CaptureSettings.DefaultDays,
    bool UserDefined = false
)
{
    /// <summary>The default size limit: 50 MB.</summary>
    public const int DefaultMegabytes = 50;

    /// <summary>The default age limit: 30 days.</summary>
    public const int DefaultDays = 30;

    /// <summary>Capture off, with the default limits.</summary>
    public static readonly CaptureSettings Off = new();
}

/// <summary>A packet capture recorded (F1.27 behavior 2).</summary>
/// <param name="At">When it arrived.</param>
/// <param name="SourceId">The source it came on, or null.</param>
/// <param name="Station">Its source callsign, or empty.</param>
/// <param name="Raw">The packet as received, one character per byte (Latin-1).</param>
/// <param name="Reasons">
/// Why: <c>aprs.parse.*</c> issues, <c>capture.unsupported</c>, <c>capture.unknown-type</c>, or
/// <c>aprs.unrecognized.*</c> parts, each with its <see cref="EngineErrorArgs.Offset"/> where there is one.
/// </param>
public sealed record CapturedPacket(
    DateTimeOffset At,
    string? SourceId,
    string Station,
    string Raw,
    IReadOnlyList<EngineError> Reasons
);

/// <summary>One day's file and how many packets it holds (F1.27 behavior 7).</summary>
public sealed record CaptureDay(DateOnly Day, int Count);

/// <summary>Which captured packets to list (F1.27 behavior 7).</summary>
/// <param name="Day">One day, or null for all, newest first.</param>
/// <param name="Reason">One reason's code, or null for any.</param>
/// <param name="Max">At most this many, newest first.</param>
public sealed record CaptureQuery(DateOnly? Day = null, string? Reason = null, int Max = 1000);

/// <summary>Captured packets and what there is to filter by (F1.27 behavior 7).</summary>
/// <param name="Packets">Newest first.</param>
/// <param name="Days">The days kept, newest first.</param>
/// <param name="Reasons">The reasons in the days kept, by code.</param>
/// <param name="RepeatsLeftOut">How many repeats were counted and not written today (behavior 3).</param>
public sealed record CaptureView(
    IReadOnlyList<CapturedPacket> Packets,
    IReadOnlyList<CaptureDay> Days,
    IReadOnlyList<string> Reasons,
    int RepeatsLeftOut = 0
);

/// <summary>What to export for analysis (F1.27 behavior 8).</summary>
/// <param name="Anonymized">Callsigns, names and e-mail addresses replaced, for the corpus (the default).</param>
/// <param name="Day">One day, or null for all.</param>
/// <param name="Reason">One reason's code, or null for any.</param>
public sealed record CaptureExport(bool Anonymized = true, DateOnly? Day = null, string? Reason = null);

/// <summary>An export ready to save (F1.27 behavior 8).</summary>
/// <param name="FileName">A name such as <c>capture-2026-10-18.log</c>.</param>
/// <param name="Text">The corpus lines, one character per byte: save it as Latin-1.</param>
/// <param name="Note">A short Markdown note for an APRS.NET issue.</param>
/// <param name="Lines">How many lines it holds.</param>
/// <param name="LeftOut">Lines left out of an anonymized export (a KISS frame the anonymizer can't rewrite).</param>
/// <param name="Clean">Whether the anonymizer found nothing left that looks like a callsign or an address.</param>
public sealed record CaptureExportResult(
    string FileName,
    string Text,
    string Note,
    int Lines,
    int LeftOut = 0,
    bool Clean = true
);
