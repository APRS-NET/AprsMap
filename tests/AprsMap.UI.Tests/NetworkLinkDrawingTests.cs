// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Appearance;
using AprsMap.UI.Map;
using Avalonia.Headless.XUnit;

namespace AprsMap.UI.Tests;

/// <summary>The RF network on the map (docs/features/F1.21-rf-paths.md, behaviors 5, 6).</summary>
public sealed class NetworkLinkDrawingTests
{
    private static readonly NetworkLink[] Links =
    [
        new("N0CAR-9", 40, -105, "N0GATE-1", 40.1, -105, 4),
        new("N0HOUSE", 40.05, -105.1, "N0GATE-1", 40.1, -105, 2),
        new("N0BIKE-7", 39.9, -105, "N0DIGI-1", 39.95, -105.05, 1),
    ];

    private static readonly Mapsui.Viewport Viewport = new(-11_688_546, 4_865_942, 50, 0, 800, 600);

    [Theory]
    [InlineData(1, 1.5f)]
    [InlineData(4, 3f)]
    [InlineData(10_000, 6f)]
    public void MorePacketsDrawAThickerLine(int packets, float width) =>
        Assert.Equal(width, LinkFrame.WidthOf(packets), 2);

    [AvaloniaFact]
    public void NoStationSelectedDrawsNoLinks()
    {
        using var view = new MapView { Links = Links };

        Assert.Null(view.BuildLinks(Viewport));
    }

    [AvaloniaFact]
    public void AnIGateSelectedDrawsTheStationsItHeard()
    {
        using var view = new MapView { Links = Links };
        view.Select("N0GATE-1");

        using var links = view.BuildLinks(Viewport)!;

        Assert.Equal(2, links.Count);
    }

    [AvaloniaFact]
    public void AMobileSelectedDrawsWhatHeardItAndClearingTheSelectionHidesIt()
    {
        using var view = new MapView { Links = Links };
        view.Select("N0BIKE-7");
        using (var links = view.BuildLinks(Viewport)!)
        {
            Assert.Equal(1, links.Count);
        }

        view.Select(null);

        Assert.Null(view.BuildLinks(Viewport));
    }

    [AvaloniaFact]
    public void ShowAllConnectionsDrawsEveryLinkWithoutASelection()
    {
        using var view = new MapView { Links = Links, ShowAllLinks = true };

        using var links = view.BuildLinks(Viewport)!;

        Assert.Equal(3, links.Count);
    }

    [AvaloniaFact]
    public void PathsOffDrawsNoLinksOrRouteEvenWithShowAll()
    {
        using var view = new MapView
        {
            Links = Links,
            ShowAllLinks = true,
            Paths = MapPaths.Off,
            Route = new PacketRoute(
                "N0CAR-9",
                ArrivalKind.Direct,
                40,
                -105,
                [new RouteHop("N0GATE-1", true, 40.1, -105, 11)]
            ),
        };
        view.Select("N0GATE-1");

        Assert.Null(view.BuildLinks(Viewport));
        Assert.Null(view.BuildRoute(Viewport));
    }
}
