// SPDX-License-Identifier: MIT
using AprsNet.Packets;

namespace AprsMap.Core.Tests;

/// <summary>Weather readings (docs/features/F1.10-weather.md, behaviors 1, 2; acceptance test 1).</summary>
public sealed class WeatherTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static void Apply(StationStore store, string line, DateTimeOffset at) =>
        store.Apply(AprsPacket.Parse(line), at);

    [Fact]
    public void APositionWithWeatherThenARainReportKeepEachReadingsTime()
    {
        var store = new StationStore();
        // APRS101 ch. 12: wind from 220° at 4 mph, gust 5 mph, 77 °F, humidity 50 %, 990.0 hPa.
        Apply(store, "N0WX>APRS,TCPIP*,qAC,T2TEST:!4903.50N/07201.75W_220/004g005t077h50b09900", T0);
        Apply(store, "N0WX>APRS,TCPIP*,qAC,T2TEST:_10090556c...s...g...t...r010p020P030", T0.AddMinutes(5));

        var weather = store.Get("N0WX")!.Weather!;

        Assert.Equal(220, weather.WindDirection!.Value);
        Assert.Equal(1.79, weather.WindSpeed!.Value, 2); // 4 mph in m/s
        Assert.Equal(25, weather.Temperature!.Value, 0); // 77 °F
        Assert.Equal(T0, weather.Temperature.At);
        Assert.Equal(990, weather.Pressure!.Value, 1);
        Assert.Equal(2.54, weather.RainLastHour!.Value, 2); // 0.10 in
        Assert.Equal(T0.AddMinutes(5), weather.RainLastHour.At);
        Assert.Equal(50, weather.Humidity!.Value);
    }

    [Fact]
    public void TheMapGetsTemperatureAndWind()
    {
        var store = new StationStore();
        store.Subscribe(out _);
        Apply(store, "N0WX>APRS,TCPIP*,qAC,T2TEST:!4903.50N/07201.75W_220/004g005t077", T0);

        var watcher = store.Subscribe(out var snapshot);

        var summary = Assert.Single(snapshot).Weather!;
        Assert.Equal(25, summary.TemperatureC!.Value, 0);
        Assert.Equal(220, summary.WindDirection);
        store.Unsubscribe(watcher);
    }

    [Fact]
    public void AWeatherStationsWindIsntItsMotion()
    {
        var store = new StationStore();
        Apply(store, "N0WX>APRS,TCPIP*,qAC,T2TEST:!4903.50N/07201.75W_220/004g005t077", T0);

        var entry = store.Get("N0WX")!;

        Assert.Null(entry.CourseDegrees);
        Assert.Null(entry.SpeedKmh);
    }

    [Fact]
    public void AStationWithoutWeatherHasNone()
    {
        var store = new StationStore();
        Apply(store, "N0CAR>APRS,TCPIP*,qAC,T2TEST:!4903.50N/07201.75W>", T0);

        Assert.Null(store.Get("N0CAR")!.Weather);
    }
}
