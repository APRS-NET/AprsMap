// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.Xml.Linq;
using AprsMap.UI.Appearance;

namespace AprsMap.UI.Tests;

/// <summary>
/// Checks the palettes against the targets in docs/features/F0.6-appearance.md (behavior 7, acceptance tests 3 and 4):
/// WCAG 2.2 contrast ratios for every text colour on every surface it is drawn on, and, for the colour-vision-safe
/// palette, that the state colours stay apart under simulated colour-vision deficiency.
/// </summary>
public class PaletteTests
{
    private static readonly string[] TextColours = ["Text", "TextMuted", "Accent", "Ok", "Warn", "Transmit"];
    private static readonly string[] Surfaces = ["Ground", "Surface"];

    public static TheoryData<Palette, string> Combinations()
    {
        var data = new TheoryData<Palette, string>();
        foreach (var palette in Enum.GetValues<Palette>())
        {
            data.Add(palette, "Light");
            data.Add(palette, "Dark");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void EveryTextColourMeetsItsContrastTarget(Palette palette, string variant)
    {
        var colours = Load(palette, variant);
        var target = palette == Palette.HighContrast ? 7.0 : 4.5;
        var failures = new List<string>();

        foreach (var text in TextColours)
        {
            foreach (var surface in Surfaces)
            {
                Check(text, surface);
            }
        }
        Check("Text", "AccentSoft"); // the wizard's confirmation line
        Check("Text", "OkSoft"); // the connected status pill

        Assert.Empty(failures);

        void Check(string text, string surface)
        {
            var ratio = Contrast(colours[text], colours[surface]);
            if (ratio < target)
            {
                failures.Add($"{text} on {surface}: {ratio:0.00} < {target}");
            }
        }
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void HighContrastBordersAreVisible(string variant)
    {
        var colours = Load(Palette.HighContrast, variant);

        Assert.True(Contrast(colours["Border"], colours["Surface"]) >= 3.0);
        Assert.True(Contrast(colours["Border"], colours["Ground"]) >= 3.0);
    }

    // Machado, Oliveira and Fernandes (2009), severity 1.0, applied to linear RGB.
    private static readonly double[][][] Deficiencies =
    [
        [
            [0.152286, 1.052583, -0.204868],
            [0.114503, 0.786281, 0.099216],
            [-0.003882, -0.048116, 1.051998],
        ],
        [
            [0.367322, 0.860646, -0.227968],
            [0.280085, 0.672501, 0.047413],
            [-0.011820, 0.042940, 0.968881],
        ],
        [
            [1.255528, -0.076749, -0.178779],
            [-0.078411, 0.930809, 0.147602],
            [0.004733, 0.691367, 0.303900],
        ],
    ];

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void ColourVisionSafeStatesStayApartUnderEachDeficiency(string variant)
    {
        var colours = Load(Palette.ColourVisionSafe, variant);
        string[] states = ["Ok", "Warn", "Transmit"];

        foreach (var matrix in Deficiencies)
        {
            for (var i = 0; i < states.Length; i++)
            {
                for (var j = i + 1; j < states.Length; j++)
                {
                    var distance = DeltaE(Simulate(colours[states[i]], matrix), Simulate(colours[states[j]], matrix));
                    Assert.True(distance >= 15, $"{states[i]}/{states[j]} ΔE {distance:0.0} under a deficiency");
                }
            }
        }
        // And no state is green: Ok is blue, so nothing pairs red with green.
        var ok = Lab(Linear(colours["Ok"]));
        Assert.True(ok.B < 0, "Ok should be on the blue side");
    }

    [Fact]
    public void EveryPaletteDefinesTheSameTokensForBothVariants()
    {
        var keys = Load(Palette.Standard, "Light").Keys.Order().ToList();
        foreach (var palette in Enum.GetValues<Palette>())
        {
            Assert.Equal(keys, Load(palette, "Light").Keys.Order());
            Assert.Equal(keys, Load(palette, "Dark").Keys.Order());
        }
    }

    // ── Colour maths (WCAG 2.2 definitions; CIE 1976) ──

    private static double Contrast(double[] a, double[] b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(double[] rgb)
    {
        var l = Linear(rgb);
        return 0.2126 * l[0] + 0.7152 * l[1] + 0.0722 * l[2];
    }

    private static double[] Linear(double[] rgb) =>
        [.. rgb.Select(c => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4))];

    private static double[] Simulate(double[] rgb, double[][] m)
    {
        var l = Linear(rgb);
        return [.. m.Select(row => Math.Clamp(row[0] * l[0] + row[1] * l[1] + row[2] * l[2], 0, 1))];
    }

    private static double DeltaE(double[] linearA, double[] linearB)
    {
        var (a, b) = (Lab(linearA), Lab(linearB));
        return Math.Sqrt(Math.Pow(a.L - b.L, 2) + Math.Pow(a.A - b.A, 2) + Math.Pow(a.B - b.B, 2));
    }

    private static (double L, double A, double B) Lab(double[] l)
    {
        double x = (0.4124 * l[0] + 0.3576 * l[1] + 0.1805 * l[2]) / 0.95047;
        double y = 0.2126 * l[0] + 0.7152 * l[1] + 0.0722 * l[2];
        double z = (0.0193 * l[0] + 0.1192 * l[1] + 0.9505 * l[2]) / 1.08883;
        static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
        return (116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
    }

    // ── Reading the palette files ──

    private static Dictionary<string, double[]> Load(Palette palette, string variant)
    {
        var file = Path.Combine(RepoRoot(), "src", "AprsMap.UI", "Themes", "Palettes", palette + ".axaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var dictionary = XDocument
            .Load(file)
            .Descendants()
            .Single(e => e.Name.LocalName == "ResourceDictionary" && (string?)e.Attribute(x + "Key") == variant);
        return dictionary
            .Elements()
            .Where(e => e.Name.LocalName == "Color")
            .ToDictionary(e => ((string)e.Attribute(x + "Key")!)["Color.".Length..], e => Rgb(e.Value));
    }

    // #RRGGBB or #AARRGGBB (alpha is ignored: the translucent scrim isn't a text background).
    private static double[] Rgb(string hex)
    {
        var digits = hex.TrimStart('#');
        digits = digits[^6..];
        return
        [
            .. Enumerable
                .Range(0, 3)
                .Select(i =>
                    int.Parse(digits.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0
                ),
        ];
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AprsMap.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("Can't find the repository root.");
    }
}
