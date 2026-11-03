// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Map;
using AprsMap.UI.Platform;
using AprsMap.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>The map's state and panel (docs/features/F1.4-map.md, behaviors 2, 3, 8, 12; acceptance test 3).</summary>
public sealed class MapTests
{
    private static readonly TileProviderInfo Osm = new(
        "osm",
        "OpenStreetMap",
        "© OpenStreetMap contributors",
        "https://www.openstreetmap.org/copyright",
        0,
        19,
        "image/png"
    );

    [Fact]
    public async Task ItLoadsTheProvidersAndTheLastViewAndSavesAMovedOne()
    {
        var store = new MemoryStore();
        await store.SaveAsync(
            MapViewModel.ViewKey,
            "{\"latitude\":40,\"longitude\":-105,\"resolution\":120}",
            TestContext.Current.CancellationToken
        );
        var time = new FakeTimeProvider();
        using var map = new MapViewModel(new FakeTiles(), store, time);

        await map.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(map.IsLoaded);
        Assert.Equal(Osm, map.Provider);
        Assert.Equal(new MapViewState(40, -105, 120), map.SavedView);

        map.Remember(new MapViewState(41, -104, 60));
        map.Remember(new MapViewState(42, -103, 30));
        time.Advance(MapViewModel.SaveDelay);
        await WaitUntil(() => store.Saves.Count == 2); // the first was this test's own setup
        Assert.Equal(new MapViewState(42, -103, 30), MapViewModel.Read(store.Saves[^1]));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"latitude\":95,\"longitude\":0,\"resolution\":10}")]
    [InlineData("{\"latitude\":0,\"longitude\":0,\"resolution\":-1}")]
    [InlineData("")]
    public void AViewThatCantBeUsedIsIgnored(string text) => Assert.Null(MapViewModel.Read(text));

    [Fact]
    public async Task WithoutATileSourceTheMapStillLoads()
    {
        using var map = new MapViewModel(null, null, new FakeTimeProvider());

        await map.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(map.IsLoaded);
        Assert.Null(map.Provider);
    }

    [AvaloniaFact]
    public async Task HomeFollowsTheActiveProfilesLocation()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider())
        {
            Map = new MapViewModel(new FakeTiles(), null, new FakeTimeProvider()),
        };
        var settings = engine.Settings;
        var located = settings with
        {
            Profiles =
            [
                .. settings.Profiles.Select(p =>
                    p.Id == settings.ActiveProfileId ? p with { Latitude = 40, Longitude = -105 } : p
                ),
            ],
        };

        vm.Show(
            settings with
            {
                Profiles = [.. settings.Profiles.Select(p => p with { Latitude = null, Longitude = null })],
            }
        );
        Assert.False(vm.Map!.HasHome);
        vm.Show(located);
        Assert.Equal((40d, -105d), vm.Map.Home);
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task ThePanelShowsTheAttributionAndHidesHomeWithoutALocation()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider())
        {
            Map = new MapViewModel(new FakeTiles(), null, new FakeTimeProvider()),
        };
        await vm.Map!.LoadAsync(TestContext.Current.CancellationToken);
        using var panel = new MapPanel { DataContext = vm };
        var window = new Window
        {
            Content = panel,
            Width = 800,
            Height = 600,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var link = window.GetVisualDescendants().OfType<HyperlinkButton>().Single(b => b.Name == "AttributionLink");
        Assert.Equal("© OpenStreetMap contributors", link.Content);
        Assert.Equal(new Uri("https://www.openstreetmap.org/copyright"), link.NavigateUri);
        Assert.False(window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "HomeButton").IsVisible);
        Assert.NotNull(panel.View.View); // world view placed once it had a size
        window.Close();
    }

    [AvaloniaFact]
    public async Task ALocationThatArrivesAfterTheMapOpensMovesItThereAndTheWorldIsNotRemembered()
    {
        // On the web the settings come after the map has its size: it opens on the world, then the station.
        var engine = new FakeEngine();
        var store = new MemoryStore();
        var time = new FakeTimeProvider();
        var vm = new MainViewModel(engine, time) { Map = new MapViewModel(new FakeTiles(), store, time) };
        await vm.Map!.LoadAsync(TestContext.Current.CancellationToken);
        using var panel = new MapPanel { DataContext = vm };
        var window = new Window
        {
            Content = panel,
            Width = 800,
            Height = 600,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(panel.View.View!.Value.Resolution >= MapPanel.WorldResolution);

        vm.Show(Located(engine.Settings, 40, -105));
        Dispatcher.UIThread.RunJobs();

        var (lat, lon, resolution) = panel.View.View!.Value;
        Assert.Equal(40, lat, 1);
        Assert.Equal(-105, lon, 1);
        Assert.True(resolution < 1000, $"{resolution} m a pixel");
        time.Advance(MapViewModel.SaveDelay);
        await WaitUntil(() => store.Saves.Count == 1);
        Assert.True(MapViewModel.Read(store.Saves[0])!.Resolution < 1000); // the station's view, not the world's
        window.Close();
    }

    [AvaloniaFact]
    public async Task ARememberedWorldViewOpensAtTheStationInstead()
    {
        var engine = new FakeEngine();
        var store = new MemoryStore();
        await store.SaveAsync(
            MapViewModel.ViewKey,
            "{\"latitude\":20,\"longitude\":0,\"resolution\":50000}",
            TestContext.Current.CancellationToken
        );
        var vm = new MainViewModel(engine, new FakeTimeProvider())
        {
            Map = new MapViewModel(new FakeTiles(), store, new FakeTimeProvider()),
        };
        await vm.Map!.LoadAsync(TestContext.Current.CancellationToken);
        vm.Show(Located(engine.Settings, 40, -105));
        using var panel = new MapPanel { DataContext = vm };
        var window = new Window
        {
            Content = panel,
            Width = 800,
            Height = 600,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var (lat, lon, resolution) = panel.View.View!.Value;
        Assert.Equal(40, lat, 1);
        Assert.Equal(-105, lon, 1);
        Assert.True(resolution < 1000, $"{resolution} m a pixel");
        window.Close();
    }

    private static AppSettings Located(AppSettings settings, double lat, double lon) =>
        settings with
        {
            Profiles =
            [
                .. settings.Profiles.Select(p =>
                    p.Id == settings.ActiveProfileId ? p with { Latitude = lat, Longitude = lon } : p
                ),
            ],
        };

    [AvaloniaFact]
    public void TwoFingersZoomAndPanTheMapAndDontSelectAStation()
    {
        using var view = new MapView();
        var window = new Window
        {
            Content = view,
            Width = 800,
            Height = 600,
        };
        window.Show();
        view.SetStations([new StationState("N0CALL-9", DateTimeOffset.UnixEpoch, 40, -105, "/>")]);
        view.GoTo(40, -105, 1000);
        Dispatcher.UIThread.RunJobs();
        var under = view.ScreenPoint(40, -105);
        var one = new Pointer(1, PointerType.Touch, true);
        var two = new Pointer(2, PointerType.Touch, false);

        Touch(view, one, under - new Point(50, 0), InputElement.PointerPressedEvent);
        Touch(view, two, under + new Point(50, 0), InputElement.PointerPressedEvent);
        Touch(view, two, under + new Point(150, 0), InputElement.PointerMovedEvent); // twice as far apart
        Touch(view, two, under + new Point(150, 0), InputElement.PointerReleasedEvent);
        Touch(view, one, under - new Point(50, 0), InputElement.PointerReleasedEvent);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(500, view.View!.Value.Resolution, 1); // zoomed in by the spread
        var moved = view.ScreenPoint(40, -105);
        Assert.Equal(under.X + 50, moved.X, 1); // the place between the fingers stays between them
        Assert.Equal(under.Y, moved.Y, 1);
        Assert.Null(view.Selected); // lifting the fingers isn't a tap
        window.Close();
    }

    private static void Touch(MapView view, Pointer pointer, Point at, RoutedEvent kind)
    {
        var root = TopLevel.GetTopLevel(view)!;
        var position = view.TranslatePoint(at, root)!.Value;
        var properties = new PointerPointProperties(
            RawInputModifiers.None,
            kind == InputElement.PointerReleasedEvent ? PointerUpdateKind.LeftButtonReleased
                : kind == InputElement.PointerPressedEvent ? PointerUpdateKind.LeftButtonPressed
                : PointerUpdateKind.Other
        );
        RoutedEventArgs args =
            kind == InputElement.PointerPressedEvent
                ? new PointerPressedEventArgs(view, pointer, root, position, 0, properties, KeyModifiers.None)
            : kind == InputElement.PointerReleasedEvent
                ? new PointerReleasedEventArgs(
                    view,
                    pointer,
                    root,
                    position,
                    0,
                    properties,
                    KeyModifiers.None,
                    MouseButton.Left
                )
            : new PointerEventArgs(kind, view, pointer, root, position, 0, properties, KeyModifiers.None);
        view.RaiseEvent(args);
    }

    [AvaloniaFact]
    public void TheMapsButtonTogglesAllRfConnectionsForTheViewer()
    {
        var app = Assert.IsType<App>(Application.Current);
        var before = app.Appearance.Current;
        try
        {
            var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider(), appearance: app.Appearance);
            using var panel = new MapPanel { DataContext = vm };
            var window = new Window
            {
                Content = panel,
                Width = 800,
                Height = 600,
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var button = window.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.Name == "AllLinksButton");

            button.IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            Assert.True(app.Appearance.Current.ShowAllLinks);
            Assert.True(panel.View.ShowAllLinks);
            vm.ShowAllLinks = false; // the app menu or Settings turning it off unticks the button
            Dispatcher.UIThread.RunJobs();
            Assert.False(button.IsChecked);
            Assert.False(panel.View.ShowAllLinks);
            window.Close();
        }
        finally
        {
            app.Appearance.Apply(before);
        }
    }

    [AvaloniaFact]
    public async Task FilterCirclesFollowTheSettingAndTheStatus()
    {
        var app = Assert.IsType<App>(Application.Current);
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider(), appearance: app.Appearance)
        {
            Map = new MapViewModel(null, null, new FakeTimeProvider()),
        };
        await vm.Map!.LoadAsync(TestContext.Current.CancellationToken);
        using var panel = new MapPanel { DataContext = vm };
        var window = new Window
        {
            Content = panel,
            Width = 800,
            Height = 600,
        };
        window.Show();
        try
        {
            Assert.True(panel.View.ShowFilterCircles); // on by default: the area received shows (F1.2 behavior 6)
            app.Appearance.Apply(app.Appearance.Current with { ShowFilterCircles = false });
            Dispatcher.UIThread.RunJobs();
            Assert.False(panel.View.ShowFilterCircles);
            app.Appearance.Apply(app.Appearance.Current with { ShowFilterCircles = true });
            Dispatcher.UIThread.RunJobs();
            Assert.True(panel.View.ShowFilterCircles);

            vm.Show(new EngineStatus(ConnectionState.Connected, "T2TEST", 1, "1.0", FilterAreas: [new(40, -105, 150)]));
            Dispatcher.UIThread.RunJobs();
            Assert.Single(panel.View.FilterAreas);
        }
        finally
        {
            app.Appearance.Apply(app.Appearance.Current with { ShowFilterCircles = false });
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task StationsAreDrawnSelectedByClickAndFitted()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider())
        {
            Map = new MapViewModel(new FakeTiles(), null, new FakeTimeProvider()),
        };
        await vm.Map!.LoadAsync(TestContext.Current.CancellationToken);
        using var panel = new MapPanel { DataContext = vm };
        var window = new Window
        {
            Content = panel,
            Width = 800,
            Height = 600,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        vm.Apply(
            new StationBatch(
                [
                    new StationState("N0AAA", DateTimeOffset.UnixEpoch, 40.0, -105.3, "/>"),
                    new StationState("N0BBB", DateTimeOffset.UnixEpoch, 40.1, -105.1, "/-", IsStale: true),
                ],
                []
            )
        );

        Assert.True(panel.View.Fit());
        Dispatcher.UIThread.RunJobs();
        var view = panel.View;
        var at = view.ScreenPoint(40.0, -105.3);
        Assert.Equal("N0AAA", view.HitTest(at)?.Name);
        Assert.Null(view.HitTest(new Avalonia.Point(at.X + 200, at.Y + 200)));

        view.Select("N0AAA");
        Assert.Equal("N0AAA", vm.SelectedStation);
        vm.Apply(new StationBatch([], ["N0AAA"]));
        Assert.Null(view.Selected); // a station that leaves the map is no longer selected
        Assert.Null(vm.SelectedStation);
        window.Close();
    }

    [AvaloniaFact]
    public void FittingNothingDoesNothing()
    {
        using var view = new MapView();

        Assert.False(view.Fit());
    }

    [AvaloniaFact]
    public async Task SettingsMapOffersTheViewersChoicesAndTheStationsAgingWhereAllowed()
    {
        var engine = new FakeEngine();
        var appearance = new Appearance.AppearanceService();
        var vm = new MainViewModel(engine, new FakeTimeProvider(), appearance: appearance)
        {
            Map = new MapViewModel(new FakeTiles(), null, new FakeTimeProvider()),
        };
        await vm.Map!.LoadAsync(TestContext.Current.CancellationToken);
        vm.Show(engine.Settings);

        await vm.OpenSettingsAsync();
        var settings = vm.Settings!;
        Assert.Contains(settings.Sections, s => s.Id == "map");
        Assert.Equal(
            [
                "map.fade",
                "map.greyscale",
                "map.symbol-backing",
                "map.labels",
                "map.tracks",
                "map.paths",
                "map.all-links",
                "map.weather-labels",
                "map.wind",
                "map.symbol-size",
                "map.areas",
                "map.filter-circles",
                "map.dead-reckoning",
                "map.stale-after",
                "map.remove-after",
                "map.track-points",
                "map.track-age",
            ],
            settings.MapItems.Select(i => i.Id)
        );
        var labels = (Settings.ChoiceSetting)settings.MapItems.Single(i => i.Id == "map.labels");
        labels.Selected = labels.Options.Single(o => Equals(o.Value, Appearance.MapLabels.None));
        Assert.Equal(Appearance.MapLabels.None, appearance.Current.MapLabels);

        var stale = (Settings.ChoiceSetting)settings.MapItems.Single(i => i.Id == "map.stale-after");
        Assert.DoesNotContain(stale.Options, o => o.Value is int m && m >= 120); // removal is at 2 hours
        stale.Selected = stale.Options.Single(o => Equals(o.Value, 60));
        await WaitUntil(() => engine.Saved.Count > 0);
        Assert.Equal(new StationAging(60, 120), engine.Saved[^1].Aging);
        var remove = (Settings.ChoiceSetting)settings.MapItems.Single(i => i.Id == "map.remove-after");
        Assert.DoesNotContain(remove.Options, o => o.Value is int m && m <= 60);
        Assert.Contains(remove.Options, o => o.Value is null); // never

        var tracks = (Settings.ChoiceSetting)settings.MapItems.Single(i => i.Id == "map.tracks");
        tracks.Selected = tracks.Options.Single(o => Equals(o.Value, Appearance.MapTracks.SelectedAndFollowed));
        Assert.Equal(Appearance.MapTracks.SelectedAndFollowed, appearance.Current.MapTracks);
        var points = (Settings.ChoiceSetting)settings.MapItems.Single(i => i.Id == "map.track-points");
        points.Selected = points.Options.Single(o => Equals(o.Value, 1000));
        await WaitUntil(() => engine.Saved[^1].Aging?.TrackPoints == 1000);
        Assert.Equal(new StationAging(60, 120, 1000, 120), engine.Saved[^1].Aging);
    }

    [AvaloniaFact]
    public async Task OnAReadOnlyGatewayTheAgingIsntOffered()
    {
        var engine = new FakeEngine { Settings = FakeEngine.TwoProfiles() with { ReadOnly = true } };
        var vm = new MainViewModel(engine, new FakeTimeProvider())
        {
            Map = new MapViewModel(new FakeTiles(), null, new FakeTimeProvider()),
        };
        await vm.Map!.LoadAsync(TestContext.Current.CancellationToken);
        vm.Show(engine.Settings);

        await vm.OpenSettingsAsync();

        Assert.DoesNotContain(vm.Settings!.MapItems, i => i.Id.StartsWith("map.stale", StringComparison.Ordinal));
        Assert.DoesNotContain(vm.Settings!.MapItems, i => i.Id.StartsWith("map.track-", StringComparison.Ordinal));
        Assert.Contains(vm.Settings!.MapItems, i => i.Id == "map.tracks"); // the viewer's own choice
    }

    [Fact]
    public async Task TheChosenProviderIsShownOrTheFirstWhenItIsntOffered()
    {
        using var map = new MapViewModel(new FakeTiles(), null, new FakeTimeProvider());
        await map.LoadAsync(TestContext.Current.CancellationToken);

        map.UseProvider("gone");

        Assert.Equal("osm", map.Provider?.Key);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
        }
        Assert.True(condition(), "Condition was not met in time.");
    }

    /// <summary>One provider whose tiles are blank grey squares.</summary>
    [AvaloniaFact]
    public async Task HomeShowsTheProfilesRangeAndMovingTheMapReportsNothing()
    {
        var time = new FakeTimeProvider();
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, time) { Map = new MapViewModel(null, null, time) };
        await vm.Map!.LoadAsync(TestContext.Current.CancellationToken);
        var active = engine.Settings.GetActiveProfile()!;
        vm.Show(
            engine.Settings with
            {
                Profiles =
                [
                    .. engine.Settings.Profiles.Select(p =>
                        p.Id == active.Id ? p with { Latitude = 40, Longitude = -105, RangeKm = 50 } : p
                    ),
                ],
            }
        );
        using var panel = new MapPanel { DataContext = vm };
        var window = new Window
        {
            Content = panel,
            Width = 1000,
            Height = 600,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The view's corners reach the range, so its circle frames the area received (F1.2 behaviors 1.1, 6).
        var (lat, _, resolution) = panel.View.View!.Value;
        var diagonal = Math.Sqrt(Math.Pow(panel.View.Bounds.Width, 2) + Math.Pow(panel.View.Bounds.Height, 2));
        Assert.InRange(diagonal / 2 * resolution * Math.Cos(lat * Math.PI / 180) / 1000, 45, 55);

        panel.View.ZoomOut();
        time.Advance(TimeSpan.FromSeconds(10));
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(engine.MapViews); // the map doesn't change the filter (F1.2 behavior 1.2)
        window.Close();
    }

    internal sealed class FakeTiles : ITileSource
    {
        public Task<IReadOnlyList<TileProviderInfo>> GetProvidersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TileProviderInfo>>([Osm]);

        public Task<byte[]?> GetTileAsync(
            string provider,
            int z,
            int x,
            int y,
            CancellationToken cancellationToken = default
        )
        {
            using var bitmap = new SkiaSharp.SKBitmap(256, 256);
            bitmap.Erase(new SkiaSharp.SKColor((byte)(160 + (x + y) % 2 * 30), 170, 160));
            using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
            return Task.FromResult<byte[]?>(data.ToArray());
        }
    }

    internal sealed class MemoryStore : IPreferenceStore
    {
        private readonly Dictionary<string, string> _values = [];

        public List<string> Saves { get; } = [];

        public Task<string?> LoadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.GetValueOrDefault(key));

        public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            Saves.Add(value);
            return Task.CompletedTask;
        }

        public Task SetAsideAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
