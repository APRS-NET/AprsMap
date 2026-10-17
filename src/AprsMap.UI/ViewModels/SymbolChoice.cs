// SPDX-License-Identifier: AGPL-3.0-only
using AprsNet.Symbols;

namespace AprsMap.UI.ViewModels;

/// <summary>
/// A symbol the station can send (docs/features/F2.1-own-station.md, behavior 1): its two APRS characters, for the
/// icon, and its name from APRS.NET's catalog.
/// </summary>
/// <param name="Symbol">The table or overlay character and the code, such as <c>/-</c>.</param>
/// <param name="Name">What it shows, such as "House".</param>
public sealed record SymbolChoice(string Symbol, string Name)
{
    /// <summary>
    /// Every symbol with a current meaning, by name; unassigned and reserved codes aren't offered, and symbols that
    /// moved to an overlay are offered as their overlay.
    /// </summary>
    public static IReadOnlyList<SymbolChoice> All { get; } =
    [
        .. AprsSymbolCatalog
            .All.Where(i => i.Status == AprsSymbolStatus.Assigned)
            .Select(i => new SymbolChoice(i.Symbol.ToString(), i.Name))
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
    ];

    /// <summary>The choice for <paramref name="symbol"/>; one named by its characters when the catalog doesn't list it.</summary>
    public static SymbolChoice For(string symbol) =>
        All.FirstOrDefault(c => c.Symbol == symbol) ?? new SymbolChoice(symbol, symbol);

    /// <inheritdoc />
    public override string ToString() => Name; // what the list's text search matches
}
