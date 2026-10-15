// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.Text.Json.Nodes;
using AprsMap.Contracts;
using AprsMap.UI.Platform;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AprsMap.UI.Map;

/// <summary>A remembered map view: its centre in degrees and resolution in metres per pixel.</summary>
public sealed record MapViewState(double Latitude, double Longitude, double Resolution);

/// <summary>
/// The map's state that isn't drawing (docs/features/F1.4-map.md): the tile providers and the one shown, the station's
/// location for Home, and the view this viewer left, kept with their preferences (behavior 2).
/// </summary>
public sealed partial class MapViewModel : ObservableObject, IDisposable
{
    /// <summary>The preference key the last view is stored under.</summary>
    public const string ViewKey = "map";

    /// <summary>How long after the map stops moving its view is saved.</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);

    /// <summary>How much Home and a first start show around the station (behavior 2): about the stand-in filter's 150 km.</summary>
    public const double HomeSpanKm = 150;

    private readonly IPreferenceStore? _store;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private ITimer? _saveTimer;
    private MapViewState? _pending;

    /// <summary>Creates the map's state; <paramref name="tiles"/> null means a map without tiles (ground colour only).</summary>
    public MapViewModel(ITileSource? tiles, IPreferenceStore? store, TimeProvider time)
    {
        Tiles = tiles;
        _store = store;
        _time = time;
    }

    /// <summary>Where tiles come from, or null.</summary>
    public ITileSource? Tiles { get; }

    /// <summary>The providers on offer, once loaded.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TileProviderInfo> Providers { get; private set; } = [];

    /// <summary>The provider shown: the first one until Settings › Map lets the viewer choose (behavior 21).</summary>
    [ObservableProperty]
    public partial TileProviderInfo? Provider { get; set; }

    /// <summary>The active profile's location, for Home (behavior 3); null hides Home.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHome))]
    public partial (double Latitude, double Longitude)? Home { get; set; }

    /// <summary>Whether Home is offered.</summary>
    public bool HasHome => Home is not null;

    /// <summary>The view this viewer left last time, once loaded; null on a first start.</summary>
    public MapViewState? SavedView { get; private set; }

    /// <summary>Whether <see cref="LoadAsync"/> has finished, so the map can take its opening position.</summary>
    [ObservableProperty]
    public partial bool IsLoaded { get; private set; }

    /// <summary>Shows the provider with <paramref name="key"/>, or the first one when it isn't offered (behavior 11).</summary>
    public void UseProvider(string? key) =>
        Provider = Providers.FirstOrDefault(p => p.Key == key) ?? (Providers.Count > 0 ? Providers[0] : null);

    /// <summary>Loads the providers and the remembered view. Never throws: without them the map still works.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (Tiles is not null)
        {
            try
            {
                Providers = await Tiles.GetProvidersAsync(cancellationToken);
                Provider ??= Providers.Count > 0 ? Providers[0] : null;
            }
            catch (Exception ex)
                when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
            {
                Providers = []; // no tiles: the ground colour and the stations still show
            }
        }
        if (_store is not null)
        {
            try
            {
                SavedView = Read(await _store.LoadAsync(ViewKey, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SavedView = null;
            }
        }
        IsLoaded = true;
    }

    /// <summary>Remembers <paramref name="view"/>, written once the map has been still for <see cref="SaveDelay"/>.</summary>
    public void Remember(MapViewState view)
    {
        if (_store is null)
        {
            return;
        }
        lock (_gate)
        {
            _pending = view;
            _saveTimer ??= _time.CreateTimer(
                _ => _ = FlushAsync(),
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan
            );
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Writes a view waiting to be saved now (on exit, and for tests).</summary>
    public async Task FlushAsync()
    {
        MapViewState? view;
        lock (_gate)
        {
            view = _pending;
            _pending = null;
        }
        if (view is null || _store is null)
        {
            return;
        }
        var text = new JsonObject
        {
            ["latitude"] = Math.Round(view.Latitude, 6),
            ["longitude"] = Math.Round(view.Longitude, 6),
            ["resolution"] = view.Resolution,
        }.ToJsonString();
        try
        {
            await _store.SaveAsync(ViewKey, text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nowhere to save: the next start opens at the station instead.
        }
    }

    /// <inheritdoc />
    public void Dispose() => _saveTimer?.Dispose();

    internal static MapViewState? Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        try
        {
            if (JsonNode.Parse(text) is not JsonObject view)
            {
                return null;
            }
            var lat = view["latitude"]?.GetValue<double>();
            var lon = view["longitude"]?.GetValue<double>();
            var resolution = view["resolution"]?.GetValue<double>();
            return lat is >= -85 and <= 85 && lon is >= -180 and <= 180 && resolution is > 0 and < 1e6
                ? new MapViewState(lat.Value, lon.Value, resolution.Value)
                : null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>For logs.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"map {Provider?.Key ?? "no tiles"}, home {HasHome}");
}
