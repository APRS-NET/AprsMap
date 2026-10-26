// SPDX-License-Identifier: MIT
using System.Text.RegularExpressions;

namespace AprsMap.Contracts;

/// <summary>
/// A named list of callsigns and objects in a profile (docs/features/F1.33-station-lists.md, behaviors 1–3), kept as
/// the text the user typed or pasted, one entry a line, so headings and blank lines survive a copy out and back in.
/// </summary>
/// <param name="Id">Its id, which the View filter refers to.</param>
/// <param name="Name">What it's called ("Skywarn spotters").</param>
/// <param name="Text">Its entries, one a line, a note after a space; <c>#</c> lines and blank lines are kept and ignored.</param>
public sealed record StationList(string Id, string Name, string Text)
{
    /// <summary>The most lists a profile has (decision 6).</summary>
    public const int MaxLists = 20;

    /// <summary>The most entries a list has (decision 6).</summary>
    public const int MaxEntries = 500;

    /// <summary>
    /// The most entries of the lists shown that go into the APRS-IS filter (behavior 9): a filter can't be endless, so
    /// the rest are left out, and Settings says so.
    /// </summary>
    public const int MaxFilterEntries = 100;

    /// <summary>Its entries, read from <see cref="Text"/>; lines that can't be read are left out.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<StationListEntry> Entries => StationListText.Parse(Text).Entries;
}

/// <summary>
/// One entry of a list (behavior 2): a callsign with its SSID, one without (every SSID), a prefix and <c>*</c>, or an
/// object or item name. Case doesn't matter.
/// </summary>
/// <param name="Pattern">As written, upper-cased.</param>
/// <param name="Note">The note after it, or null.</param>
public sealed partial record StationListEntry(string Pattern, string? Note = null)
{
    /// <summary>Whether it's a prefix and <c>*</c>, matching anything starting so.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsPrefix => Pattern.EndsWith('*');

    /// <summary>Whether it's written as a callsign (with or without an SSID) or a prefix, for the buddy list.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsCallsign => IsPrefix || CallsignPattern().IsMatch(Pattern);

    /// <summary>Whether it's a callsign without an SSID, which matches every SSID of it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEverySsid => IsCallsign && !IsPrefix && !Pattern.Contains('-', StringComparison.Ordinal);

    /// <summary>Whether <paramref name="name"/>, a station's callsign or an object's or item's name, is this entry.</summary>
    public bool Matches(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (IsPrefix)
        {
            return name.StartsWith(Pattern[..^1], StringComparison.OrdinalIgnoreCase);
        }
        if (name.Equals(Pattern, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return IsEverySsid
            && name.Length > Pattern.Length + 1
            && name[Pattern.Length] == '-'
            && name.StartsWith(Pattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A callsign: letters and digits with at least one digit, then an optional SSID.</summary>
    [GeneratedRegex(@"^(?=[A-Z0-9]*[0-9])[A-Z0-9]{3,7}(-[A-Z0-9]{1,2})?$")]
    private static partial Regex CallsignPattern();
}

/// <summary>A line of a list that can't be an entry.</summary>
/// <param name="Line">Its number, from 1.</param>
/// <param name="Text">The line as written.</param>
public sealed record StationListLineError(int Line, string Text);

/// <summary>A list's text read: its entries, and the lines that can't be entries.</summary>
public sealed record StationListParse(
    IReadOnlyList<StationListEntry> Entries,
    IReadOnlyList<StationListLineError> Errors
);

/// <summary>
/// A list's plain text (behavior 3), read and written the same way by the desktop, the browser and the engine: one
/// entry a line, the note after a space; blank lines and lines starting with <c>#</c> are ignored.
/// </summary>
public static partial class StationListText
{
    /// <summary>Reads <paramref name="text"/>: the entries, and each line that can't be one, by number.</summary>
    public static StationListParse Parse(string? text)
    {
        var entries = new List<StationListEntry>();
        var errors = new List<StationListLineError>();
        var lines = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            var space = line.IndexOfAny([' ', '\t']);
            var pattern = (space < 0 ? line : line[..space]).ToUpperInvariant();
            var note = space < 0 ? null : line[(space + 1)..].Trim();
            if (!EntryPattern().IsMatch(pattern))
            {
                errors.Add(new StationListLineError(i + 1, lines[i]));
                continue;
            }
            entries.Add(new StationListEntry(pattern, string.IsNullOrEmpty(note) ? null : note));
        }
        return new StationListParse(entries, errors);
    }

    /// <summary><paramref name="text"/> with <paramref name="pattern"/> added on a line of its own (behavior 5).</summary>
    public static string Append(string? text, string pattern, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var line = string.IsNullOrWhiteSpace(note) ? pattern : $"{pattern} {note.Trim()}";
        var body = (text ?? string.Empty).TrimEnd('\r', '\n');
        return body.Length == 0 ? line : $"{body}\n{line}";
    }

    /// <summary>
    /// An entry: up to nine printable characters without spaces (a callsign, an object or item name), or up to nine
    /// letters, digits and dashes before a final <c>*</c>.
    /// </summary>
    [GeneratedRegex(@"^([!-)+-~]{1,9}|[A-Z0-9/-]{1,9}\*)$")]
    private static partial Regex EntryPattern();
}
