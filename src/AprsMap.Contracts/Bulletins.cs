// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>What kind of bulletin (docs/features/F2.5-bulletins.md, behavior 1).</summary>
public enum BulletinKind
{
    /// <summary>A bulletin, <c>BLN0</c>–<c>BLN9</c>, with or without a group.</summary>
    Bulletin,

    /// <summary>An announcement, <c>BLNA</c>–<c>BLNZ</c>, with or without a group.</summary>
    Announcement,

    /// <summary>A National Weather Service bulletin, <c>NWS-…</c>.</summary>
    Nws,
}

/// <summary>A bulletin as the Bulletins panel shows it (F2.5 behaviors 1–3).</summary>
/// <param name="Key">Stable for one bulletin across new versions: its sender, kind, addressee and id.</param>
/// <param name="Kind">Bulletin, announcement or NWS.</param>
/// <param name="From">The sender.</param>
/// <param name="Addressee">The addressee as sent, such as <c>BLN3</c> or <c>BLN4WX</c>.</param>
/// <param name="Id">The id, such as <c>3</c> or <c>Q</c>; null for an NWS bulletin without one.</param>
/// <param name="Group">The group, such as <c>WX</c>; null for none.</param>
/// <param name="Text">The newest text.</param>
/// <param name="FirstHeard">When this version was first heard.</param>
/// <param name="LastHeard">When it was last heard.</param>
/// <param name="SourceId">The source it was last heard on.</param>
/// <param name="Updated">Whether this text replaced an earlier one.</param>
/// <param name="Removed">Whether it went: not heard for the board's expiry.</param>
public sealed record BulletinView(
    string Key,
    BulletinKind Kind,
    string From,
    string Addressee,
    string? Id,
    string? Group,
    string Text,
    DateTimeOffset FirstHeard,
    DateTimeOffset LastHeard,
    string? SourceId,
    bool Updated = false,
    bool Removed = false
);
