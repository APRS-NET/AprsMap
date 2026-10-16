// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Map;
using Avalonia.Headless.XUnit;
using SkiaSharp;

namespace AprsMap.UI.Tests;

/// <summary>Area objects on the map (docs/features/F1.12-area-objects.md, behaviors 1–7).</summary>
public sealed class AreaDrawingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly SKColor[] Colours = [.. Enumerable.Range(0, 8).Select(i => new SKColor((byte)i, 0, 0))];

    private static StationMark Area(AreaKind kind, bool filled = false, double? corridorKm = null) =>
        StationMark.From(
            new StationState(
                "AREA",
                T0,
                40,
                -105,
                "\\l",
                StationKind.Object,
                Area: new AreaInfo(kind, filled, 3, 0.1, 0.1, corridorKm)
            )
        );

    private static SKPoint Screen(double x, double y) => new((float)(x / 100), (float)(-y / 100));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8, 0)]
    [InlineData(3, 3)]
    [InlineData(11, 3)]
    [InlineData(15, 7)]
    public void SixteenColoursShareEightTokens(int color, int token) => Assert.Equal(token, AreaFrame.TokenOf(color));

    [Theory]
    [InlineData(AreaKind.Circle)]
    [InlineData(AreaKind.Ellipse)]
    [InlineData(AreaKind.Box)]
    [InlineData(AreaKind.Triangle)]
    [InlineData(AreaKind.LineLeft)]
    [InlineData(AreaKind.LineRight)]
    public void EachShapeIsDrawn(AreaKind kind)
    {
        using var frame = AreaFrame.Build([Area(kind)], Screen, 100, Colours);

        Assert.Equal(1, frame.Count);
    }

    [Fact]
    public void OrdinaryStationsDrawNoShape()
    {
        var car = StationMark.From(new StationState("N0CAR", T0, 40, -105, "/>"));

        using var frame = AreaFrame.Build([car], Screen, 100, Colours);

        Assert.Equal(0, frame.Count);
    }

    [AvaloniaFact]
    public void TheSettingTurnsShapesOff()
    {
        using var view = new MapView();
        var viewport = new Mapsui.Viewport(0, 0, 100, 0, 800, 600);

        using (var shown = view.BuildAreas(viewport, [Area(AreaKind.Box, filled: true)]))
        {
            Assert.Equal(1, shown!.Count);
        }
        view.ShowAreas = false;
        Assert.Null(view.BuildAreas(viewport, [Area(AreaKind.Box)]));
    }
}
