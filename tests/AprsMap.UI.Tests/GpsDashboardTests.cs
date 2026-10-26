// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Appearance;
using AprsMap.UI.Localization;
using AprsMap.UI.Statistics;
using AprsMap.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>The dashboard's GPS tiles (docs/features/F3.14-gps-dashboard.md, behaviors 7–9; acceptance tests 6, 8).</summary>
public sealed class GpsDashboardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static double?[] Points(double? value) => [.. Enumerable.Repeat(value, 59), value];

    internal static Dashboard Sample() =>
        FakeEngine.EmptyDashboard() with
        {
            At = T0,
            Start = T0.AddMinutes(-59),
            Gps = new DashboardGps(
                "gps",
                OtherInUse: false,
                Fix3D: TimeSpan.FromMinutes(54),
                Fix2D: TimeSpan.FromMinutes(1),
                NoFix: TimeSpan.FromMinutes(4),
                Silent: TimeSpan.FromMinutes(1),
                Losses: 3,
                LongestOutage: TimeSpan.FromMinutes(2),
                LastOutage: new DashboardOutage(T0.AddMinutes(-10), TimeSpan.FromSeconds(16), false),
                SatellitesUsed: new DashboardStat(14, 12.4, 5, 14),
                SatellitesInView: new DashboardStat(22, 21, 18, 23),
                Constellations: [new(GnssConstellation.Gps, 10, 12, 9.6), new(GnssConstellation.BeiDou, 4, 10, 2.8)],
                Dilutions:
                [
                    new("pdop", new DashboardStat(1.6, 1.8, 1.4, 4.2)),
                    new("hdop", new DashboardStat(0.8, 0.9, 0.7, 2.5)),
                    new("vdop", new DashboardStat(1.4, 1.5, 1.2, 3.6)),
                ],
                Errors:
                [
                    new("horizontal", new DashboardStat(2.1, 2.5, 1.8, 9.4)),
                    new("vertical", new DashboardStat(3.6, 4, 3, 14)),
                ],
                Signal: new DashboardStat(38, 37.5, 30, 42),
                WanderMeters: 3.2,
                WanderFixes: 3300,
                FixShare: Points(92),
                SatellitesSeries: Points(12.4),
                HdopSeries: Points(0.9),
                ErrorSeries: Points(2.5)
            ),
        };

    internal static DashboardViewModel Create(UnitSettings? units = null, FakeEngine? engine = null) =>
        new(
            engine ?? new FakeEngine(),
            new FakeTimeProvider(T0),
            new Localizer(CultureInfo.GetCultureInfo("en-US")),
            () => null,
            new SourceNames(() =>
                AppSettings.Empty with
                {
                    Sources = [SourceSettings.GpsNetworkFor(GpsLink.Tcp, "nuc") with { Id = "gps", Name = "NUC gpsd" }],
                }
            ),
            _ => null,
            _ => { },
            () => { },
            units is null ? null : () => units
        );

    [Fact]
    public void TheGpsTilesWriteTheFixSatellitesDilutionsAndAccuracyOut()
    {
        var vm = Create();

        vm.Show(Sample());

        Assert.True(vm.HasGps);
        Assert.Equal("NUC gpsd", vm.GpsSourceText);
        Assert.Equal("92 %", vm.GpsFix.Number); // 55 of 60 minutes with a fix
        Assert.Equal("3 fixes lost", vm.GpsFix.Detail);
        Assert.Equal(["3D fix", "2D fix", "No fix", "Silent"], vm.GpsFixRows.Select(r => r.Name));
        Assert.Equal("90 % · 54 min", vm.GpsFixRows[0].Value);
        Assert.Equal("Longest outage 2 min · Last lost at 11:50 AM for 16 s", vm.GpsOutageText!.Replace('\u202f', ' '));
        Assert.Equal("14 of 22", vm.GpsSatellites.Number);
        Assert.Equal("Average 12.4 · lowest 5 · highest 14", vm.GpsSatellites.Detail);
        Assert.Equal(["GPS", "BeiDou"], vm.GpsSystemRows.Select(r => r.Name));
        Assert.Equal("4 of 10 now · 2.8 average", vm.GpsSystemRows[1].Value);
        Assert.Equal("HDOP 0.8", vm.GpsDilution.Number);
        Assert.Equal("1.6 now · 1.8 average · 4.2 worst", vm.GpsDilutionRows[0].Value);
        Assert.Equal("±2.1 m", vm.GpsAccuracy.Number);
        Assert.Equal("±3.6 m now · ±4.0 m average · ±14.0 m worst", vm.GpsErrorRows[1].Value);
        Assert.Equal("Average signal of the satellites used: 38 dB-Hz", vm.GpsSignalText);
        Assert.Equal("While still, 95 % of fixes within 3.2 m (3,300 fixes)", vm.GpsWanderText);
        Assert.Equal("0.9 at 12:00 PM", vm.GpsDilution.PointLabels[^1].Replace('\u202f', ' '));
        Assert.StartsWith("GPS fix, Last hour: 92 %.", vm.GpsFix.Spoken, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorsAndWanderAreInFeetForImperialViewers()
    {
        var vm = Create(UnitSettings.Metric with { Altitude = AltitudeUnit.Feet });

        vm.Show(Sample());

        Assert.Equal("±6.9 ft", vm.GpsAccuracy.Number);
        Assert.Equal(8.2, vm.GpsAccuracy.Series[^1]!.Value, 1);
        Assert.Equal("While still, 95 % of fixes within 10.5 ft (3,300 fixes)", vm.GpsWanderText);
    }

    [Fact]
    public void WithoutAGpsTheTilesStepAside()
    {
        var vm = Create();

        vm.Show(FakeEngine.EmptyDashboard());

        Assert.False(vm.HasGps);
    }
}

/// <summary>The GPS tiles at 200 % text in every theme (F3.14 acceptance test 8); changes the app's appearance.</summary>
[Collection(nameof(LanguageViewTests))]
public sealed class GpsDashboardRenderTests : IDisposable
{
    public void Dispose() => App.Appearance.Apply(new AppearanceSettings());

    private static App App => Assert.IsType<App>(Application.Current);

    [AvaloniaTheory]
    [MemberData(nameof(AccessibilityRenderTests.Combinations), MemberType = typeof(AccessibilityRenderTests))]
    public void TheGpsTilesFitAtDoubleTextSize(ThemeMode theme, Palette palette)
    {
        App.Appearance.Apply(
            new AppearanceSettings
            {
                Theme = theme,
                Palette = palette,
                TextScale = 2.0,
            }
        );
        // The panel asks its engine as soon as it's shown.
        var vm = GpsDashboardTests.Create(engine: new FakeEngine { Dashboard = GpsDashboardTests.Sample() });
        var window = new Window
        {
            Content = new DashboardPanel { DataContext = vm },
            Width = 1280,
            Height = 2400,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var failures = new List<string>();
        var gpsTexts = window
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text) && t.Bounds.Width > 0)
            .ToList();
        Assert.True(
            gpsTexts.Any(t => t.Text == "Dilution of precision"),
            string.Join(" | ", gpsTexts.Select(t => t.Text).Take(80))
        );
        foreach (var text in gpsTexts)
        {
            var room = text.Bounds.Width - text.Padding.Left - text.Padding.Right;
            if (text.TextTrimming == TextTrimming.None && text.TextLayout.Width > room + 1)
            {
                failures.Add($"“{text.Text}” is cut short ({text.TextLayout.Width:0} > {room:0})");
            }
        }
        window.Close();
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
