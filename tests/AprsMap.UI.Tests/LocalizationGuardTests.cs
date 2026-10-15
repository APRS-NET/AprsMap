// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsNet.Packets;

namespace AprsMap.UI.Tests;

/// <summary>
/// Keeps the UI translatable (docs/features/F0.9-localization.md, behavior 19): no literal text in views, every key in
/// the English strings, translations with the same placeholders, and a string for every engine error code.
/// </summary>
public partial class LocalizationGuardTests
{
    private static readonly string Ui = Path.Combine(RepoRoot(), "src", "AprsMap.UI");

    // Attributes that show text to the user; a value must be a binding or markup extension.
    private static readonly string[] TextAttributes =
    [
        "Text",
        "Content",
        "Header",
        "Watermark",
        "PlaceholderText",
        "ToolTip.Tip",
        "Title",
    ];

    [Fact]
    public void ViewsHaveNoLiteralText()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Ui, "*.axaml", SearchOption.AllDirectories))
        {
            foreach (var element in XDocument.Load(file).Descendants())
            {
                foreach (var attribute in element.Attributes().Where(a => TextAttributes.Contains(a.Name.LocalName)))
                {
                    if (IsLiteralText(attribute.Value))
                    {
                        offenders.Add(
                            $"{Path.GetFileName(file)}: <{element.Name.LocalName} {attribute.Name.LocalName}=\"{attribute.Value}\">"
                        );
                    }
                }
                if (
                    element.Name.LocalName is "Run" or "TextBlock"
                    && !element.HasElements
                    && IsLiteralText(element.Value)
                )
                {
                    offenders.Add($"{Path.GetFileName(file)}: <{element.Name.LocalName}>{element.Value}</…>");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("packets", true)]
    [InlineData("Station profile", true)]
    [InlineData("{Binding Name}", false)]
    [InlineData("{l:T Main.PacketCount}", false)]
    [InlineData("·", false)]
    [InlineData(" – ", false)]
    [InlineData("", false)]
    public void LiteralTextIsToldApartFromBindingsAndSeparators(string value, bool literal) =>
        Assert.Equal(literal, IsLiteralText(value));

    [Fact]
    public void EveryKeyUsedInTheUiIsInTheEnglishStrings()
    {
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Ui, "*.axaml", SearchOption.AllDirectories))
        {
            used.UnionWith(XamlKey().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value));
        }
        foreach (var file in Directory.EnumerateFiles(Ui, "*.cs", SearchOption.AllDirectories))
        {
            if (
                !file.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal
                )
            )
            {
                used.UnionWith(CodeKey().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value));
            }
        }

        Assert.NotEmpty(used);
        var missing = used.Where(key => !Localizer.Exists(key)).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void EveryEngineErrorCodeHasText()
    {
        var codes = typeof(EngineErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name != nameof(EngineErrorCodes.AprsPrefix))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Concat(Enum.GetNames<AprsValidationErrorCode>().Select(n => EngineErrorCodes.AprsPrefix + Kebab(n)));

        var missing = codes.Where(code => !Localizer.Exists("Error." + code)).ToList();
        Assert.Empty(missing);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData(Localizer.PseudoLanguage)]
    public void EveryStringFormatsWithItsPlaceholdersFilled(string language)
    {
        var text = new Localizer(CultureInfo.GetCultureInfo("en-US"));
        text.SetLanguage(language);
        foreach (var (key, pattern) in EnglishStrings())
        {
            // Every placeholder gets a value: a number for plurals, text for the rest (select falls to "other").
            var args = Placeholder()
                .Matches(pattern)
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(
                    name => name,
                    name => pattern.Contains("{" + name + ", plural", StringComparison.Ordinal) ? 2 : (object?)"x",
                    StringComparer.Ordinal
                );
            var value = text.Format(key, args);
            Assert.DoesNotContain("⟦", value, StringComparison.Ordinal);
            Assert.DoesNotContain("{", value, StringComparison.Ordinal);
            if (text.IsPseudo)
            {
                Assert.True(value.StartsWith('[') && value.EndsWith(']'), $"{key}: {value}");
            }
        }
    }

    [Fact]
    public void TranslationsUseTheSamePlaceholdersAndAreOffered()
    {
        var english = EnglishStrings();
        var files = Directory.EnumerateFiles(Path.Combine(Ui, "Localization"), "Strings.*.resx");
        foreach (var file in files)
        {
            var culture = Path.GetFileNameWithoutExtension(file)["Strings.".Length..];
            Assert.Contains(culture, Localizer.Translations);
            foreach (var (key, value) in Read(file))
            {
                Assert.True(english.TryGetValue(key, out var source), $"{culture}: {key} is not in Strings.resx");
                Assert.Equal(Placeholders(source), Placeholders(value));
            }
        }
        Assert.Equal(Localizer.Translations.Count, files.Count());
    }

    [Fact]
    public void EveryEnglishStringHasAKeyInTheResourceManager()
    {
        var set = new ResourceManager("AprsMap.UI.Localization.Strings", typeof(Localizer).Assembly).GetResourceSet(
            CultureInfo.InvariantCulture,
            createIfNotExists: true,
            tryParents: false
        );
        Assert.NotNull(set);
        Assert.Equal(EnglishStrings().Count, set.Cast<System.Collections.DictionaryEntry>().Count());
    }

    internal static bool IsLiteralText(string value) => !value.TrimStart().StartsWith('{') && value.Any(char.IsLetter);

    private static Dictionary<string, string> EnglishStrings() =>
        Read(Path.Combine(Ui, "Localization", "Strings.resx"));

    private static Dictionary<string, string> Read(string file) =>
        XDocument
            .Load(file)
            .Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!, StringComparer.Ordinal);

    private static SortedSet<string> Placeholders(string pattern) =>
        new(Placeholder().Matches(pattern).Select(m => m.Groups[1].Value), StringComparer.Ordinal);

    private static string Kebab(string name) =>
        string.Concat(
            name.Select(
                (c, i) =>
                    char.IsUpper(c) && i > 0 ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()
            )
        );

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

    [GeneratedRegex(@"\{l:T\s+([^}\s]+)\s*\}")]
    private static partial Regex XamlKey();

    // _text["Key.Name"], _text.Format("Key.Name", …), Localizer.Instance["Key.Name"]
    [GeneratedRegex(@"(?:\[|Format\()\s*""([A-Z][A-Za-z]*\.[A-Za-z0-9.\-]+)""")]
    private static partial Regex CodeKey();

    // {name} or {name, plural|select, …}
    [GeneratedRegex(@"\{\s*([A-Za-z]\w*)\s*[,}]")]
    private static partial Regex Placeholder();
}
