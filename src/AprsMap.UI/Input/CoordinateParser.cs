// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.Text.RegularExpressions;

namespace AprsMap.UI.Input;

/// <summary>Why typed coordinates couldn't be read.</summary>
public enum CoordinateParseError
{
    /// <summary>Read fine.</summary>
    None,

    /// <summary>Not latitude then longitude in a known form.</summary>
    Unreadable,

    /// <summary>Latitude outside -90 to 90, longitude outside -180 to 180, or minutes or seconds of 60 or more.</summary>
    OutOfRange,
}

/// <summary>
/// Reads a latitude and longitude typed by a person (docs/features/F0.5-first-run-wizard.md, behavior 9.3), latitude
/// first: decimal degrees with signs (<c>40.0150, -105.2705</c>) or hemisphere letters (<c>40.0150 N 105.2705 W</c>,
/// <c>N40.0150 W105.2705</c>), degrees and decimal minutes (<c>40 00.90 N 105 16.23 W</c>), or degrees, minutes and
/// seconds (<c>40°00′54″N 105°16′14″W</c>). The decimal separator is a point; a comma separates the two halves.
/// </summary>
public static partial class CoordinateParser
{
    /// <summary>Reads <paramref name="text"/>, or says why it can't.</summary>
    public static CoordinateParseError TryParse(string? text, out double latitude, out double longitude)
    {
        latitude = longitude = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return CoordinateParseError.Unreadable;
        }

        // Degree, minute and second marks are only separators; hemisphere letters are tokens of their own.
        var normal = Marks().Replace(text.ToUpperInvariant(), " ");
        normal = Hemisphere().Replace(normal, " $1 ");
        if (!Split(normal, out var first, out var second))
        {
            return CoordinateParseError.Unreadable;
        }

        var a = ReadHalf(first);
        var b = ReadHalf(second);
        if (a is null || b is null)
        {
            return CoordinateParseError.Unreadable;
        }

        // Latitude first, unless the hemisphere letters say otherwise (E/W first, N/S second).
        var (lat, lon) = a.Value.Axis == 'L' || b.Value.Axis == 'A' ? (b.Value, a.Value) : (a.Value, b.Value);
        if (lat.Axis == 'L' || lon.Axis == 'A')
        {
            return CoordinateParseError.Unreadable; // two latitudes or two longitudes
        }
        if (double.IsNaN(lat.Degrees) || double.IsNaN(lon.Degrees))
        {
            return CoordinateParseError.OutOfRange;
        }
        if (lat.Degrees is < -90 or > 90 || lon.Degrees is < -180 or > 180)
        {
            return CoordinateParseError.OutOfRange;
        }

        latitude = lat.Degrees;
        longitude = lon.Degrees;
        return CoordinateParseError.None;
    }

    /// <summary>Splits into the latitude and longitude halves: at a comma, after a hemisphere letter, or in the middle.</summary>
    private static bool Split(string text, out string first, out string second)
    {
        first = second = string.Empty;
        var commas = text.Split(',');
        if (commas.Length == 2)
        {
            (first, second) = (commas[0], commas[1]);
            return true;
        }
        if (commas.Length > 2)
        {
            return false;
        }

        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var letters = tokens.Select((t, i) => (t, i)).Where(x => IsHemisphere(x.t)).Select(x => x.i).ToList();
        int cut;
        if (letters.Count == 2)
        {
            // Letters after the numbers (40 N 105 W) end a half; letters before them (N 40 W 105) start one.
            cut = letters[0] == 0 ? letters[1] : letters[0] + 1;
        }
        else if (letters.Count == 0 && tokens.Length is 2 or 4 or 6)
        {
            cut = tokens.Length / 2;
        }
        else
        {
            return false;
        }
        first = string.Join(' ', tokens[..cut]);
        second = string.Join(' ', tokens[cut..]);
        return true;
    }

    /// <summary>One half: 1 to 3 numbers (degrees, minutes, seconds) and at most one hemisphere letter.</summary>
    private static (double Degrees, char Axis)? ReadHalf(string half)
    {
        var tokens = half.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hemisphere = tokens.Where(IsHemisphere).ToList();
        var numbers = tokens.Where(t => !IsHemisphere(t)).ToList();
        if (hemisphere.Count > 1 || numbers.Count is < 1 or > 3)
        {
            return null;
        }

        var parts = new double[numbers.Count];
        for (var i = 0; i < numbers.Count; i++)
        {
            if (
                !double.TryParse(
                    numbers[i],
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out parts[i]
                )
            )
            {
                return null;
            }
        }
        // Only the degrees may carry a sign, and only the last number may have a fraction.
        if (
            parts.Skip(1).Any(p => p < 0 || numbers[0].StartsWith('+')) || numbers.SkipLast(1).Any(n => n.Contains('.'))
        )
        {
            return null;
        }

        var negative = numbers[0].StartsWith('-');
        var minutes = parts.Length > 1 ? parts[1] : 0;
        var seconds = parts.Length > 2 ? parts[2] : 0;
        if (minutes >= 60 || seconds >= 60)
        {
            return (double.NaN, ' ');
        }
        var degrees = Math.Abs(parts[0]) + minutes / 60 + seconds / 3600;

        var axis = ' ';
        if (hemisphere.Count == 1)
        {
            if (negative)
            {
                return null; // "-40 S" says the same thing twice and may mean either
            }
            var letter = hemisphere[0][0];
            axis = letter is 'N' or 'S' ? 'A' : 'L';
            negative = letter is 'S' or 'W';
        }
        return (negative ? -degrees : degrees, axis);
    }

    private static bool IsHemisphere(string token) => token is "N" or "S" or "E" or "W";

    [GeneratedRegex("[°º′″'\"]")]
    private static partial Regex Marks();

    [GeneratedRegex("([NSEW])")]
    private static partial Regex Hemisphere();
}
