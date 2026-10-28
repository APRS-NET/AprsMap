// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>
/// How much packet history is kept (docs/features/F1.41-persistent-history.md, behavior 2).
/// </summary>
/// <param name="Days">How many days are kept, 1–30; 0 is off: memory only, and what was kept is deleted.</param>
/// <param name="MaxMegabytes">The most the history's files may take, in MB (decision 2: 1 GB).</param>
public sealed record HistorySettings(
    int Days = HistorySettings.DefaultDays,
    int MaxMegabytes = HistorySettings.DefaultMegabytes
)
{
    /// <summary>The default length: 7 days (decision 2).</summary>
    public const int DefaultDays = 7;

    /// <summary>The longest history: 30 days.</summary>
    public const int MaxDays = 30;

    /// <summary>The default size limit: 1 GB (decision 2).</summary>
    public const int DefaultMegabytes = 1024;

    /// <summary>The smallest size limit: 100 MB.</summary>
    public const int MinMegabytes = 100;

    /// <summary>The largest size limit: 100 GB.</summary>
    public const int MaxMegabytesLimit = 102_400;

    /// <summary>The defaults: 7 days within 1 GB.</summary>
    public static readonly HistorySettings Default = new();

    /// <summary>Whether history is kept at all.</summary>
    public bool On => Days > 0;

    /// <summary>Whether the values are in range: 0–30 days, 100 MB–100 GB.</summary>
    public bool IsValid => Days is >= 0 and <= MaxDays && MaxMegabytes is >= MinMegabytes and <= MaxMegabytesLimit;
}

/// <summary>What stands in the history's way, if anything (F1.41 behaviors 5, 13).</summary>
public enum HistoryProblem
{
    /// <summary>Nothing.</summary>
    None,

    /// <summary>The size limit keeps fewer days than asked for (behavior 5); see <see cref="HistoryStatus.DaysFit"/>.</summary>
    SizeLimitShortens,

    /// <summary>Today's file alone reached the size limit: packets aren't kept for the rest of the day (behavior 5).</summary>
    TodayFull,

    /// <summary>Another process has the history open (two engines on one data folder): off for this run (behavior 13).</summary>
    InUseElsewhere,

    /// <summary>The history's folder can't be written: off for this run.</summary>
    CantWrite,
}

/// <summary>What the history holds now (F1.41 behavior 3), for Settings › Data.</summary>
/// <param name="Available">
/// This engine keeps history at all: false for an engine without a data folder, where the settings change nothing.
/// </param>
/// <param name="Settings">The settings in force.</param>
/// <param name="Configured">
/// The settings come from the gateway's configuration (<c>[engine.history]</c>), which wins on every start: shown, not
/// changed here.
/// </param>
/// <param name="Bytes">What the history's files take now.</param>
/// <param name="KeptSince">When the oldest packet kept was received; null with none.</param>
/// <param name="DaysKept">How many days have a file.</param>
/// <param name="Problem">What stands in its way.</param>
/// <param name="DaysFit">
/// With <see cref="HistoryProblem.SizeLimitShortens"/>, about how many days fit in the size limit at the rate seen.
/// </param>
/// <param name="SetAside">Days whose file couldn't be read and was set aside (behavior 13), oldest first.</param>
public sealed record HistoryStatus(
    bool Available,
    HistorySettings Settings,
    bool Configured = false,
    long Bytes = 0,
    DateTimeOffset? KeptSince = null,
    int DaysKept = 0,
    HistoryProblem Problem = HistoryProblem.None,
    int? DaysFit = null,
    IReadOnlyList<DateOnly>? SetAside = null
)
{
    /// <summary>An engine that keeps no history.</summary>
    public static readonly HistoryStatus NotAvailable = new(false, HistorySettings.Default);
}
