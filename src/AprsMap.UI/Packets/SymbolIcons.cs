// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.UI.Map;
using AprsNet.Packets;
using AprsNet.Symbols;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace AprsMap.UI.Packets;

/// <summary>
/// A packet's symbol as a small image for the packet list (F1.8 behavior 1), from its two APRS characters. Each symbol
/// is drawn once, at twice the list's icon size so it stays sharp at 200 %, and kept for the session.
/// </summary>
public sealed class SymbolIcons : IValueConverter
{
    /// <summary>The converter, for bindings.</summary>
    public static SymbolIcons Instance { get; } = new();

    private const int Pixels = 48;

    private readonly Dictionary<string, Bitmap?> _icons = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: 2 } text ? Icon(text) : null;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private Bitmap? Icon(string text)
    {
        if (_icons.TryGetValue(text, out var icon))
        {
            return icon;
        }
        icon = AprsSymbol.TryCreate(text[0], text[1], out var symbol) ? Draw(symbol) : null;
        _icons[text] = icon;
        return icon;
    }

    private static Bitmap? Draw(AprsSymbol symbol)
    {
        using var picture = SymbolAtlas.Picture(symbol, AprsSymbolStyle.Conventional);
        if (picture is null)
        {
            return null;
        }
        using var surface = SKSurface.Create(new SKImageInfo(Pixels, Pixels, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
        {
            return null;
        }
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale(Pixels / Math.Max(1, picture.CullRect.Width));
        surface.Canvas.DrawPicture(picture);
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = png.AsStream();
        return new Bitmap(stream);
    }
}
