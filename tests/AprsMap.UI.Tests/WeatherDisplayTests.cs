// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Appearance;
using AprsMap.UI.Localization;
using AprsMap.UI.Map;
using AprsMap.UI.ViewModels;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Weather in the details and on the map (docs/features/F1.10-weather.md, behaviors 3–6).</summary>
public sealed class WeatherDisplayTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static Localizer English() => new(new System.Globalization.CultureInfo("en-GB"));

    [Theory]
    [InlineData(5, 0, 0, 1)]
    [InlineData(15, 0, 1, 1)]
    [InlineData(55, 1, 0, 1)]
    [InlineData(65, 1, 1, 1)]
    [InlineData(2, 0, 0, 0)]
    [InlineData(103, 2, 0, 1)]
    public void ABarbHasAFlagPerFiftyKnotsAFeatherPerTenAndOneForFive(double knots, int flags, int longs, int shorts) =>
        Assert.Equal((flags, longs, shorts), WindBarbs.Parts(knots));

    [Fact]
    public void ReadingsAreWrittenInTheViewersUnits()
    {
        var text = English();

        Assert.Equal("14 °C", WeatherText.Temperature(14.2, TemperatureUnit.Celsius, text));
        Assert.Equal("58 °F", WeatherText.Temperature(14.2, TemperatureUnit.Fahrenheit, text));
        Assert.Equal("22 km/h", WeatherText.Speed(6.1, SpeedUnit.KilometresPerHour, text));
        Assert.Equal("12 kn", WeatherText.Speed(6.1, SpeedUnit.Knots, text));
        Assert.Equal("2.5 mm", WeatherText.Rain(2.54, RainUnit.Millimetres, text));
        Assert.Equal("0.10 in", WeatherText.Rain(2.54, RainUnit.Inches, text));
        Assert.Equal("1013.2 hPa", WeatherText.Pressure(1013.2, PressureUnit.Hectopascals, text));
        Assert.Equal("29.92 inHg", WeatherText.Pressure(1013.2, PressureUnit.InchesOfMercury, text));
        Assert.Equal("NW", WeatherText.Compass(315, text));
    }

    [Fact]
    public void TheMapLineIsTheTemperatureAndOptionallyTheWind()
    {
        var text = English();
        var weather = new WeatherSummary(14, 315, 6.1);

        Assert.Equal("14 °C", WeatherText.MapLine(weather, false, UnitSettings.Metric, text));
        Assert.Equal("14 °C · NW 22 km/h", WeatherText.MapLine(weather, true, UnitSettings.Metric, text));
        Assert.Null(WeatherText.MapLine(null, true, UnitSettings.Metric, text));
    }

    [AvaloniaFact]
    public void OnlyWeatherStationsGetASecondLabelLineAndBarbs()
    {
        using var view = new MapView { Width = 800, Height = 600 };
        var wx = StationMark.From(new StationState("N0WX", T0, 40, -105, "/_", Weather: new(14, 315, 6.1)));
        var car = StationMark.From(new StationState("N0CAR", T0, 40, -105, "/>"));

        Assert.Equal("N0WX\n14 °C", view.LabelText(wx));
        Assert.Equal("N0CAR", view.LabelText(car));
        view.WeatherLabels = MapWeatherLabels.Off;
        Assert.Equal("N0WX", view.LabelText(wx));

        Assert.Null(view.BuildBarbs([wx, car], [new(100, 100), new(200, 200)]));
        view.Wind = MapWind.Barbs;
        view.Measure(new Avalonia.Size(800, 600));
        view.Arrange(new Avalonia.Rect(0, 0, 800, 600));
        using var barbs = view.BuildBarbs([wx, car], [new(100, 100), new(200, 200)])!;
        Assert.Equal(1, barbs.Count);
    }

    [Fact]
    public async Task DetailsListEachReadingWithItsAgeWhenOlderThanTheLastPacket()
    {
        var time = new FakeTimeProvider(T0);
        var engine = new FakeEngine();
        engine.Details["N0WX"] = new StationDetail(
            "N0WX",
            StationKind.Station,
            T0,
            3,
            false,
            40,
            -105,
            0,
            "/_",
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            Weather: new StationWeather(
                WindDirection: new(315, T0),
                WindSpeed: new(6.1, T0),
                Temperature: new(14.2, T0),
                RainLastHour: new(2.54, T0.AddMinutes(-20))
            )
        );
        var vm = new MainViewModel(engine, time);

        vm.SelectedStation = "N0WX";
        await WaitUntil(() => vm.Details.HasWeather);

        Assert.Equal(
            ["Wind: NW 22 km/h", "Temperature: 14 °C", "Rain, last hour: 2.5 mm · 20 minutes old"],
            vm.Details.Weather.Select(l => $"{l.Label}: {l.Value}")
        );
    }

    [Fact]
    public async Task SettingsUnitsSavesTheStationsUnitsWhereAllowed()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        await vm.OpenSettingsAsync();

        var temperature = (Settings.ChoiceSetting)vm.Settings!.UnitItems.Single(i => i.Id == "units.temperature");
        temperature.Selected = temperature.Options.Single(o => Equals(o.Value, TemperatureUnit.Fahrenheit));
        await WaitUntil(() => engine.Saved.Count > 0);

        Assert.Equal(TemperatureUnit.Fahrenheit, engine.Saved[^1].Units.Temperature);
        Assert.Contains(vm.Settings.Sections, s => s.Id == "units");

        var readOnly = new FakeEngine { Settings = FakeEngine.TwoProfiles() with { ReadOnly = true } };
        var viewer = new MainViewModel(readOnly, new FakeTimeProvider());
        viewer.Show(readOnly.Settings);
        await viewer.OpenSettingsAsync();
        Assert.DoesNotContain(viewer.Settings!.Sections, s => s.Id == "units");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
        }
        Assert.True(condition(), "Condition was not met in time.");
    }
}
