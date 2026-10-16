// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsNet.Packets;

namespace AprsMap.UI.Localization;

/// <summary>
/// Distance and bearing from my location in words (docs/features/F1.13-my-location.md, behavior 4): the distance in the
/// viewer's unit, then the compass point and whole degrees, for example <c>100 km NE 045°</c>. The engine measures; this
/// only writes it.
/// </summary>
internal static class RangeText
{
    private static readonly string[] Points =
    [
        "N",
        "NNE",
        "NE",
        "ENE",
        "E",
        "ESE",
        "SE",
        "SSE",
        "S",
        "SSW",
        "SW",
        "WSW",
        "W",
        "WNW",
        "NW",
        "NNW",
    ];

    /// <summary>The 16-point compass key of a direction in degrees, for example <c>NNE</c>.</summary>
    public static string Point(double degrees) =>
        Points[(int)Math.Round(((degrees % 360) + 360) % 360 / 22.5, MidpointRounding.AwayFromZero) % 16];

    /// <summary>A distance in the viewer's unit: tenths close by, whole units further out ("4.2 km", "1,250 mi").</summary>
    public static string Distance(double km, DistanceUnit unit, Localizer text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var (to, key) = unit switch
        {
            DistanceUnit.Miles => (LengthUnit.Miles, "Range.Miles"),
            DistanceUnit.NauticalMiles => (LengthUnit.NauticalMiles, "Range.NauticalMiles"),
            _ => (LengthUnit.Kilometers, "Range.Kilometres"),
        };
        var distance = AprsUnits.Convert(km, LengthUnit.Kilometers, to);
        var number = distance.ToString(distance < 10 ? "0.#" : "#,0", text.Culture);
        return text.Format(key, new Dictionary<string, object?> { ["value"] = number });
    }

    /// <summary>The text for <paramref name="km"/> at <paramref name="bearing"/> degrees, or null when either is unknown.</summary>
    public static string? Format(double? km, int? bearing, DistanceUnit unit, Localizer text)
    {
        if (km is not { } kilometres || bearing is not { } degrees)
        {
            return null;
        }
        var point = Point(degrees);
        return text.Format(
            "Range.Text",
            new Dictionary<string, object?>
            {
                ["distance"] = Distance(kilometres, unit, text),
                ["point"] = text[$"Compass.{point}"],
                ["bearing"] = degrees.ToString("000", text.Culture),
            }
        );
    }
}
