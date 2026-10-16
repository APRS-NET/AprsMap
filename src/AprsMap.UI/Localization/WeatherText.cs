// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsNet.Packets;

namespace AprsMap.UI.Localization;

/// <summary>
/// Weather readings in words and the viewer's units (docs/features/F1.10-weather.md, behavior 6): the engine sends
/// °C, m/s, mm and hPa; this converts with APRS.NET's conversions (G08) and writes them.
/// </summary>
internal static class WeatherText
{
    /// <summary>"14 °C" or "57 °F".</summary>
    public static string Temperature(double celsius, Contracts.TemperatureUnit unit, Localizer text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var (value, key) =
            unit == Contracts.TemperatureUnit.Fahrenheit
                ? (
                    AprsUnits.Convert(
                        celsius,
                        AprsNet.Packets.TemperatureUnit.Celsius,
                        AprsNet.Packets.TemperatureUnit.Fahrenheit
                    ),
                    "Weather.Fahrenheit"
                )
                : (celsius, "Weather.Celsius");
        return text.Format(key, Value(Math.Round(value).ToString("0", text.Culture)));
    }

    /// <summary>"22 km/h", "14 mph", "12 kn" or "6 m/s".</summary>
    public static string Speed(double metresPerSecond, Contracts.SpeedUnit unit, Localizer text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var (to, key) = unit switch
        {
            Contracts.SpeedUnit.MilesPerHour => (AprsNet.Packets.SpeedUnit.MilesPerHour, "Details.Speed.MilesPerHour"),
            Contracts.SpeedUnit.Knots => (AprsNet.Packets.SpeedUnit.Knots, "Details.Speed.Knots"),
            Contracts.SpeedUnit.MetresPerSecond => (
                AprsNet.Packets.SpeedUnit.MetersPerSecond,
                "Details.Speed.MetresPerSecond"
            ),
            _ => (AprsNet.Packets.SpeedUnit.KilometersPerHour, "Details.Speed.KilometresPerHour"),
        };
        var value = AprsUnits.Convert(metresPerSecond, AprsNet.Packets.SpeedUnit.MetersPerSecond, to);
        return text.Format(key, Value(value.ToString("0", text.Culture)));
    }

    /// <summary>"2.5 mm" or "0.10 in".</summary>
    public static string Rain(double millimetres, RainUnit unit, Localizer text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return unit == RainUnit.Inches
            ? text.Format(
                "Weather.Inches",
                Value(
                    AprsUnits
                        .Convert(millimetres, LengthUnit.Millimeters, LengthUnit.Inches)
                        .ToString("0.00", text.Culture)
                )
            )
            : text.Format("Weather.Millimetres", Value(millimetres.ToString("0.#", text.Culture)));
    }

    /// <summary>"1013.2 hPa" or "29.92 inHg".</summary>
    public static string Pressure(double hectopascals, Contracts.PressureUnit unit, Localizer text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return unit == Contracts.PressureUnit.InchesOfMercury
            ? text.Format(
                "Weather.InchesOfMercury",
                Value(
                    AprsUnits
                        .Convert(
                            hectopascals,
                            AprsNet.Packets.PressureUnit.Millibars,
                            AprsNet.Packets.PressureUnit.InchesOfMercury
                        )
                        .ToString("0.00", text.Culture)
                )
            )
            : text.Format("Weather.Hectopascals", Value(hectopascals.ToString("0.0", text.Culture)));
    }

    /// <summary>The 16-point compass name of a direction, for example <c>NW</c>.</summary>
    public static string Compass(double degrees, Localizer text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text[$"Compass.{RangeText.Point(degrees)}"];
    }

    /// <summary>
    /// The second line of a weather station's map label (behavior 4): "14 °C", or "14 °C · NW 22 km/h" with wind;
    /// null when it has nothing to show.
    /// </summary>
    public static string? MapLine(WeatherSummary? weather, bool withWind, UnitSettings units, Localizer text)
    {
        ArgumentNullException.ThrowIfNull(units);
        if (weather is null)
        {
            return null;
        }
        var parts = new List<string>(2);
        if (weather.TemperatureC is { } c)
        {
            parts.Add(Temperature(c, units.Temperature, text));
        }
        if (withWind && weather is { WindDirection: { } from, WindSpeedMs: { } speed })
        {
            parts.Add($"{Compass(from, text)} {Speed(speed, units.Wind, text)}");
        }
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static Dictionary<string, object?> Value(string value) => new() { ["value"] = value };
}
