// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsNet.Packets;
using Mapsui.Projections;
using SkiaSharp;

namespace AprsMap.UI.Map;

/// <summary>A station as the map draws it: its place in Web Mercator metres and its symbol.</summary>
internal sealed record StationMark(
    string Name,
    double X,
    double Y,
    AprsSymbol? Symbol,
    bool IsStale,
    DateTimeOffset LastHeard,
    double? DistanceKm = null,
    int? BearingDegrees = null
)
{
    /// <summary>The mark for a station from the station stream.</summary>
    public static StationMark From(StationState station)
    {
        var (x, y) = SphericalMercator.FromLonLat(station.Longitude, station.Latitude);
        AprsSymbol? symbol =
            station.Symbol is { Length: 2 } text && AprsSymbol.TryCreate(text[0], text[1], out var parsed)
                ? parsed
                : null;
        return new StationMark(
            station.Name,
            x,
            y,
            symbol,
            station.IsStale,
            station.LastHeard,
            station.DistanceKm,
            station.BearingDegrees
        );
    }
}

/// <summary>
/// Draws the stations' symbols on the render thread (docs/features/F1.4-map.md, behaviors 14, 15, 19): one
/// <c>DrawAtlas</c> call for the fresh ones and one at half opacity for the stale ones, and a neutral dot for a symbol
/// the atlas has no picture of. Labels and the selection ring are drawn by <see cref="MapView"/> with Avalonia's text,
/// which has fonts in the browser too.
/// </summary>
internal sealed class StationLayer : IDisposable
{
    private SymbolAtlas? _atlas;

    /// <summary>Draws <paramref name="marks"/> at <paramref name="screen"/> positions, symbols <paramref name="symbolDip"/> across.</summary>
    /// <param name="canvas">The map's canvas, in device-independent pixels.</param>
    /// <param name="marks">The stations, with their screen positions in the same order.</param>
    /// <param name="screen">Each mark's centre on screen.</param>
    /// <param name="symbolDip">Symbol size in device-independent pixels (24 × the map symbol size).</param>
    /// <param name="deviceScale">Device pixels per device-independent pixel, for a crisp atlas.</param>
    /// <param name="dot">The neutral dot's colour.</param>
    public void Draw(
        SKCanvas canvas,
        IReadOnlyList<StationMark> marks,
        IReadOnlyList<SKPoint> screen,
        float symbolDip,
        float deviceScale,
        SKColor dot
    )
    {
        var cell = Math.Max(8, (int)MathF.Round(symbolDip * deviceScale));
        if (_atlas?.CellPixels != cell)
        {
            _atlas?.Dispose();
            _atlas = new SymbolAtlas(cell); // rebuilt when the symbol size or the screen scale changes
        }

        var fresh = new List<(SKRect, SKRotationScaleMatrix)>(marks.Count);
        var stale = new List<(SKRect, SKRotationScaleMatrix)>();
        using var dotPaint = new SKPaint { Color = dot, IsAntialias = true };
        var scale = symbolDip / cell;
        for (var i = 0; i < marks.Count; i++)
        {
            var at = screen[i];
            if (marks[i].Symbol is { } symbol && _atlas.Sprite(symbol) is { } sprite)
            {
                var transform = SKRotationScaleMatrix.Create(scale, 0, at.X, at.Y, cell / 2f, cell / 2f);
                (marks[i].IsStale ? stale : fresh).Add((sprite, transform));
            }
            else
            {
                dotPaint.Color = marks[i].IsStale ? dot.WithAlpha(128) : dot;
                canvas.DrawCircle(at, symbolDip / 4, dotPaint);
            }
        }
        var image = _atlas.Image;
        using var sampling = new SKPaint { IsAntialias = true };
        if (stale.Count > 0)
        {
            using var faded = new SKPaint { Color = SKColors.White.WithAlpha(128) };
            canvas.DrawAtlas(
                image,
                [.. stale.Select(s => s.Item1)],
                [.. stale.Select(s => s.Item2)],
                new SKSamplingOptions(SKFilterMode.Linear),
                faded
            );
        }
        if (fresh.Count > 0)
        {
            canvas.DrawAtlas(
                image,
                [.. fresh.Select(s => s.Item1)],
                [.. fresh.Select(s => s.Item2)],
                new SKSamplingOptions(SKFilterMode.Linear),
                sampling
            );
        }
    }

    public void Dispose() => _atlas?.Dispose();
}
