// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Map;

/// <summary>
/// My destination (docs/features/F1.18-destination.md): a point or a station, with its distance and bearing from my
/// location in the map's chip and a great-circle line to it. Per viewer, kept in the preference store; a station
/// destination follows the station.
/// </summary>
public sealed partial class DestinationViewModel : ObservableObject
{
    /// <summary>The preference it's kept under.</summary>
    public const string PreferenceKey = "destination";

    private readonly IEngineService _engine;
    private readonly Localizer _text;
    private readonly Func<UnitSettings> _units;
    private DestinationInfo _info = DestinationInfo.Unknown;

    internal DestinationViewModel(IEngineService engine, Localizer text, Func<UnitSettings> units)
    {
        _engine = engine;
        _text = text;
        _units = units;
    }

    /// <summary>Where preferences are kept; null keeps nothing.</summary>
    public IPreferenceStore? Preferences { get; set; }

    /// <summary>The destination, or null when there's none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSet))]
    public partial GeoPoint? Point { get; private set; }

    /// <summary>The station it follows, or null for a point (behavior 4).</summary>
    public string? Station { get; private set; }

    /// <summary>Whether there's a destination.</summary>
    public bool IsSet => Point is not null;

    /// <summary>"W1AW-9 · 42 km NE 038°", or the point and the location hint (behaviors 2, 5).</summary>
    [ObservableProperty]
    public partial string? Text { get; private set; }

    /// <summary>The great-circle line from my location, for the map (behavior 3); empty without a location.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GeoPoint> Line { get; private set; } = [];

    /// <summary>What's typed in the chip: coordinates or a locator (behavior 1).</summary>
    [ObservableProperty]
    public partial string Typed { get; set; } = string.Empty;

    /// <summary>Why the typed destination wasn't understood; null otherwise.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Loads the destination kept, if any.</summary>
    internal async Task LoadAsync()
    {
        if (Preferences is null || await Preferences.LoadAsync(PreferenceKey) is not { } json)
        {
            return;
        }
        try
        {
            if (JsonSerializer.Deserialize(json, DestinationJson.Default.Kept) is { } kept)
            {
                Station = kept.Station;
                Point = new GeoPoint(kept.Latitude, kept.Longitude);
                await RefreshAsync();
            }
        }
        catch (JsonException)
        {
            await Preferences.SetAsideAsync(PreferenceKey);
        }
    }

    /// <summary>Sets <paramref name="latitude"/>, <paramref name="longitude"/> as the destination; a station's follows it.</summary>
    internal async Task SetAsync(double latitude, double longitude, string? station = null)
    {
        Error = null;
        Station = station;
        Point = new GeoPoint(latitude, longitude);
        await KeepAsync();
        await RefreshAsync();
    }

    /// <summary>Sets the destination typed: "40.015, -105.27" or a Maidenhead locator (behavior 1).</summary>
    [RelayCommand]
    private async Task SetTypedAsync()
    {
        var typed = Typed.Trim();
        var parts = typed.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (
            parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
            && lat is >= -90 and <= 90
            && lon is >= -180 and <= 180
        )
        {
            await SetAsync(lat, lon);
            Typed = string.Empty;
            return;
        }
        try
        {
            var locator = await _engine.ParseLocatorAsync(typed); // the locator's rules are APRS.NET's
            await SetAsync(locator.Latitude, locator.Longitude);
            Typed = string.Empty;
        }
        catch (ArgumentException)
        {
            Error = _text["Destination.TypedInvalid"];
        }
    }

    /// <summary>Clears the destination (behavior 2).</summary>
    [RelayCommand]
    internal async Task ClearAsync()
    {
        Point = null;
        Station = null;
        _info = DestinationInfo.Unknown;
        Line = [];
        Text = null;
        Error = null;
        await KeepAsync();
    }

    /// <summary>A station destination moved: the destination moves with it (behavior 4).</summary>
    internal void StationsChanged(StationBatch batch)
    {
        if (
            Station is { } name
            && batch.Changed.FirstOrDefault(s => s.Name == name) is { } moved
            && (moved.Latitude, moved.Longitude) != (Point?.Latitude, Point?.Longitude)
        )
        {
            Point = new GeoPoint(moved.Latitude, moved.Longitude);
            _ = KeepAsync();
            _ = RefreshAsync();
        }
    }

    /// <summary>Asks the engine again: my location or the units changed.</summary>
    internal async Task RefreshAsync()
    {
        if (Point is not { } point)
        {
            return;
        }
        _info = await _engine.GetDestinationAsync(point.Latitude, point.Longitude);
        Line = _info.Line;
        Retranslate();
    }

    /// <summary>Rewords the chip after a language or unit change.</summary>
    internal void Retranslate()
    {
        if (Point is not { } point)
        {
            Text = null;
            return;
        }
        var name =
            Station
            ?? string.Create(CultureInfo.InvariantCulture, $"{point.Latitude:0.0000}, {point.Longitude:0.0000}");
        var range = RangeText.Format(_info.DistanceKm, _info.Bearing, _units().Distance, _text);
        Text = range is null
            ? _text.Format("Destination.NoLocation", new Dictionary<string, object?> { ["name"] = name })
            : _text.Format("Destination.Text", new Dictionary<string, object?> { ["name"] = name, ["range"] = range });
    }

    private Task KeepAsync() =>
        Preferences is null ? Task.CompletedTask
        : Point is { } point
            ? Preferences.SaveAsync(
                PreferenceKey,
                JsonSerializer.Serialize(
                    new Kept(point.Latitude, point.Longitude, Station),
                    DestinationJson.Default.Kept
                )
            )
        : Preferences.SaveAsync(PreferenceKey, "null");

    /// <summary>The destination as kept.</summary>
    internal sealed record Kept(double Latitude, double Longitude, string? Station);
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(DestinationViewModel.Kept))]
internal sealed partial class DestinationJson : JsonSerializerContext;
