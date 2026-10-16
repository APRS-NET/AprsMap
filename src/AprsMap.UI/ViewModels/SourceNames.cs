// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;

namespace AprsMap.UI.ViewModels;

/// <summary>
/// Sources by name, for lists and details (docs/features/F1.25-sources.md, behaviors 8–10): with one source in use
/// nothing about sources is shown, so the common single-APRS-IS station looks as it always has.
/// </summary>
public sealed class SourceNames(Func<AppSettings?> settings)
{
    /// <summary>Whether the active profile uses more than one source, so sources are worth naming.</summary>
    public bool AreShown => settings()?.GetActiveProfile()?.SourceIds.Count > 1;

    /// <summary>The source's name, or its id when it isn't in the list any more.</summary>
    public string NameOf(string id) => settings()?.SourceList.FirstOrDefault(s => s.Id == id)?.Name ?? id;

    /// <summary>"Direwolf", or "Direwolf · also rotate.aprs2.net"; null when sources aren't shown.</summary>
    internal string? Describe(string? sourceId, IReadOnlyList<string>? seenOn, Localization.Localizer text) =>
        !AreShown || sourceId is null ? null
        : seenOn is { Count: > 0 }
            ? text.Format(
                "Sources.SeenAlso",
                new Dictionary<string, object?>
                {
                    ["source"] = NameOf(sourceId),
                    ["others"] = string.Join(", ", seenOn.Select(NameOf)),
                }
            )
        : NameOf(sourceId);
}
