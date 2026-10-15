// SPDX-License-Identifier: AGPL-3.0-only
using AprsNet.Packets;
using AprsNet.Symbols;
using SkiaSharp;
using Svg.Skia;

namespace AprsMap.UI.Map;

/// <summary>
/// The APRS symbols as one sprite sheet (docs/features/F1.4-map.md, behavior 15; spike 3), so a frame draws every
/// station with a single <see cref="SKCanvas.DrawAtlas(SKImage, SKRect[], SKRotationScaleMatrix[], SKColor[], SKBlendMode, SKPaint)"/>
/// call. The base symbols are drawn when the sheet is made; an overlay symbol is added the first time it's needed,
/// without redrawing the rest. Used on the render thread only.
/// </summary>
internal sealed class SymbolAtlas : IDisposable
{
    private const int Columns = 32;
    private const int Rows = 40; // room for the base set and about a thousand overlays
    private const int Padding = 1; // keeps neighbours out of filtered samples

    /// <summary>
    /// Svg.Skia looks a typeface up through the system font manager for every paint, even without text: 47 ms per symbol
    /// on Linux (spike 3). The symbols draw their characters as outlines, so one cached answer per family is enough.
    /// </summary>
    private static readonly CachedTypefaceProvider Typefaces = new();

    private readonly Dictionary<AprsSymbol, SKRect> _sprites = [];
    private readonly SKSurface _surface;
    private SKImage? _image;
    private int _next;

    /// <summary>Makes a sheet of <paramref name="cellPixels"/>-pixel symbols with the base set drawn in.</summary>
    public SymbolAtlas(int cellPixels, AprsSymbolStyle style = AprsSymbolStyle.Conventional)
    {
        CellPixels = cellPixels;
        Style = style;
        var stride = cellPixels + 2 * Padding;
        _surface =
            SKSurface.Create(new SKImageInfo(Columns * stride, Rows * stride, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("No surface for the symbol atlas.");
        _surface.Canvas.Clear(SKColors.Transparent);
        foreach (var info in AprsSymbolCatalog.All)
        {
            Add(info.Symbol);
        }
    }

    /// <summary>The size of each symbol, in device pixels.</summary>
    public int CellPixels { get; }

    /// <summary>The drawing style.</summary>
    public AprsSymbolStyle Style { get; }

    /// <summary>The sheet as it is now.</summary>
    public SKImage Image => _image ??= _surface.Snapshot();

    /// <summary>The symbol's place on the sheet, drawing it in first if it's new; null when the sheet is full.</summary>
    public SKRect? Sprite(AprsSymbol symbol) => _sprites.TryGetValue(symbol, out var rect) ? rect : Add(symbol);

    public void Dispose()
    {
        _image?.Dispose();
        _surface.Dispose();
    }

    private SKRect? Add(AprsSymbol symbol)
    {
        if (_next >= Columns * Rows)
        {
            return null;
        }
        using var picture = Picture(symbol, Style);
        if (picture is null)
        {
            return null;
        }
        var stride = CellPixels + 2 * Padding;
        var x = _next % Columns * stride + Padding;
        var y = _next / Columns * stride + Padding;
        var canvas = _surface.Canvas;
        canvas.Save();
        canvas.Translate(x, y);
        canvas.Scale(CellPixels / Math.Max(1, picture.CullRect.Width));
        canvas.DrawPicture(picture);
        canvas.Restore();
        _next++;
        var rect = SKRect.Create(x, y, CellPixels, CellPixels);
        _sprites[symbol] = rect;
        _image?.Dispose();
        _image = null; // snapshot again on next use
        return rect;
    }

    /// <summary>The symbol drawn as a picture, or null when it can't be drawn (behavior 14).</summary>
    internal static SKPicture? Picture(AprsSymbol symbol, AprsSymbolStyle style)
    {
        try
        {
            var converter = new SKSvg();
            if (converter.Settings.TypefaceProviders is { } providers)
            {
                providers.Clear();
                providers.Add(Typefaces);
            }
            return converter.FromSvg(AprsSymbolImages.GetSvg(symbol, style));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            return null; // drawn as a neutral dot instead (behavior 14)
        }
    }

    private sealed class CachedTypefaceProvider : Svg.Skia.TypefaceProviders.ITypefaceProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<
            (string, SKFontStyleWeight, SKFontStyleWidth, SKFontStyleSlant),
            SKTypeface?
        > _cache = new();

        public SKTypeface? FromFamilyName(
            string fontFamily,
            SKFontStyleWeight fontWeight,
            SKFontStyleWidth fontWidth,
            SKFontStyleSlant fontStyle
        ) =>
            _cache.GetOrAdd(
                (fontFamily, fontWeight, fontWidth, fontStyle),
                k => SKTypeface.FromFamilyName(k.Item1, k.Item2, k.Item3, k.Item4)
            );
    }
}
