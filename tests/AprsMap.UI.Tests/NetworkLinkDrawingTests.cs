// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Appearance;
using AprsMap.UI.Map;
using Avalonia.Headless.XUnit;

namespace AprsMap.UI.Tests;

/// <summary>The RF network on the map (docs/features/F1.21-rf-paths.md, behaviors 5, 6).</summary>
public sealed class NetworkLinkDrawingTests
{
    private static readonly NetworkLink Link = new("N0CAR-9", 40, -105, "N0GATE-1", 40.1, -105, 4);

    [Theory]
    [InlineData(1, 1.5f)]
    [InlineData(4, 3f)]
    [InlineData(10_000, 6f)]
    public void MorePacketsDrawAThickerLine(int packets, float width) =>
        Assert.Equal(width, LinkFrame.WidthOf(packets), 2);

    [AvaloniaFact]
    public void TheNetworkShowsOnlyInItsModeAndTheRouteOnlyInSelectedStation()
    {
        using var view = new MapView
        {
            Links = [Link],
            Route = new PacketRoute(
                "N0CAR-9",
                ArrivalKind.Direct,
                40,
                -105,
                [new RouteHop("N0GATE-1", true, 40.1, -105, 11)]
            ),
        };
        var viewport = new Mapsui.Viewport(-11_688_546, 4_865_942, 50, 0, 800, 600);

        Assert.Null(view.BuildLinks(viewport));
        using (var route = view.BuildRoute(viewport))
        {
            Assert.NotNull(route);
        }

        view.Paths = MapPaths.Network;
        using var links = view.BuildLinks(viewport)!;
        Assert.Equal(1, links.Count);
        Assert.Null(view.BuildRoute(viewport));
    }
}
