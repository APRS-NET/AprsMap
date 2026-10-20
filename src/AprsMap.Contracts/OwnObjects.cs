// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>An object or an item (docs/features/F2.7-objects-and-items.md, behavior 2.1; APRS101 chapter 11).</summary>
public enum OwnObjectKind
{
    /// <summary>An object (<c>;</c>): sent with the time.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming",
        "CA1720:Identifier contains type name",
        Justification = "APRS's own name for it (APRS101 chapter 11)."
    )]
    Object,

    /// <summary>An item (<c>)</c>): without a time, for things that don't change.</summary>
    Item,
}

/// <summary>
/// An object or item this station places on the map (docs/features/F2.7-objects-and-items.md), kept in its profile and
/// re-sent on a decaying schedule until it's killed.
/// </summary>
/// <param name="Id">Its id in the profile, which a rename keeps.</param>
/// <param name="Name">1 to 9 characters for an object, 3 to 9 for an item (behavior 2.2).</param>
/// <param name="Kind">Object or item.</param>
/// <param name="Latitude">Where it is.</param>
/// <param name="Longitude">Where it is.</param>
/// <param name="Symbol">Its symbol: the table or overlay and the code; red X by default.</param>
/// <param name="Comment">Up to 43 characters.</param>
/// <param name="IntervalMinutes">The longest wait between sends: 10 minutes to 24 hours (behavior 5).</param>
/// <param name="SourceIds">Send via: the sources it goes out on; null for all that may send.</param>
/// <param name="Live">False once killed: it's sent killed three times, then removed (behavior 8).</param>
public sealed record OwnObject(
    string Id,
    string Name,
    OwnObjectKind Kind,
    double Latitude,
    double Longitude,
    string Symbol = OwnObject.DefaultSymbol,
    string Comment = "",
    int IntervalMinutes = OwnObject.DefaultIntervalMinutes,
    IReadOnlyList<string>? SourceIds = null,
    bool Live = true
)
{
    /// <summary>Red X (proposed).</summary>
    public const string DefaultSymbol = "/.";

    /// <summary>30 minutes.</summary>
    public const int DefaultIntervalMinutes = 30;

    /// <summary>The most characters a comment may have (APRS101 chapter 11).</summary>
    public const int MaxCommentLength = 43;

    /// <summary>How many times a killed object is sent before it's removed (behavior 8).</summary>
    public const int KillSends = 3;

    /// <summary>The most objects a profile may have.</summary>
    public const int MaxPerProfile = 50;

    /// <summary>Whether it goes out on <paramref name="sourceId"/>.</summary>
    public bool SendsVia(string sourceId) => SourceIds is null || SourceIds.Contains(sourceId);

    /// <summary>Equal when every field is, comparing the source ids in order.</summary>
    public bool Equals(OwnObject? other) =>
        other is not null
        && Id == other.Id
        && Name == other.Name
        && Kind == other.Kind
        && Latitude.Equals(other.Latitude)
        && Longitude.Equals(other.Longitude)
        && Symbol == other.Symbol
        && Comment == other.Comment
        && IntervalMinutes == other.IntervalMinutes
        && BeaconSettings.SameSources(SourceIds, other.SourceIds)
        && Live == other.Live;

    /// <inheritdoc />
    public override int GetHashCode() =>
        HashCode.Combine(
            HashCode.Combine(Id, Name, Kind, Latitude, Longitude, Symbol, Comment),
            IntervalMinutes,
            SourceIds is null ? null : string.Join(',', SourceIds),
            Live
        );
}
