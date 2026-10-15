// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Concurrent;
using System.Globalization;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using AprsMap.Contracts;
using Jeffijoe.MessageFormat;

namespace AprsMap.UI.Localization;

/// <summary>
/// The UI's text in the viewer's language (docs/features/F0.9-localization.md). Strings come from
/// <c>Strings.resx</c> and its translations, formatted with ICU MessageFormat. Changing the language raises
/// <see cref="LanguageChanged"/>, and everything bound through <see cref="Observe(string)"/> or <see cref="TExtension"/>
/// updates without a restart.
/// </summary>
public sealed partial class Localizer
{
    /// <summary>The pseudo-language for testing: accented, about 40 % longer, bracketed.</summary>
    public const string PseudoLanguage = "qps-ploc";

    /// <summary>Languages with a translation, besides English. Add each <c>Strings.&lt;culture&gt;.resx</c> here.</summary>
    internal static readonly IReadOnlyList<string> Translations = [];

    private static readonly ResourceManager Resources = new(
        "AprsMap.UI.Localization.Strings",
        typeof(Localizer).Assembly
    );

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    // Suffix of the extra argument that holds a number formatted for the culture (see BindNumberSigns).
    private const string FormattedSuffix = "__formatted";

    private static readonly ConcurrentDictionary<string, string> BoundPatterns = new(StringComparer.Ordinal);

    private CultureInfo _culture;
    private MessageFormatter _formatter;

    /// <summary>Creates a localizer that follows the OS or browser language.</summary>
    internal Localizer()
        : this(CultureInfo.CurrentUICulture) { }

    internal Localizer(CultureInfo systemCulture)
    {
        SystemCulture = systemCulture;
        _culture = systemCulture;
        _formatter = CreateFormatter(systemCulture);
    }

    /// <summary>The one the app uses; views reach it through <see cref="TExtension"/>.</summary>
    public static Localizer Instance { get; } = new();

    /// <summary>Raised after the language changes.</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>The OS or browser language, used when no language is chosen.</summary>
    public CultureInfo SystemCulture { get; }

    /// <summary>The culture text is shown and formatted in.</summary>
    public CultureInfo Culture => _culture;

    /// <summary>Whether the pseudo-language is on.</summary>
    public bool IsPseudo { get; private set; }

    /// <summary>The languages that can be chosen: English, each translation, and the pseudo-language when shown.</summary>
    public static IReadOnlyList<string> Languages(bool includePseudo) =>
        [English.Name, .. Translations, .. includePseudo ? new[] { PseudoLanguage } : []];

    /// <summary>
    /// Switches to <paramref name="language"/> (an IETF tag such as <c>de</c>), or back to the system language for null
    /// or empty. A language without a translation falls back along its culture chain to English.
    /// </summary>
    public void SetLanguage(string? language)
    {
        var pseudo = string.Equals(language, PseudoLanguage, StringComparison.OrdinalIgnoreCase);
        var culture = pseudo || string.IsNullOrWhiteSpace(language) ? SystemCulture : Find(language) ?? SystemCulture;
        if (pseudo == IsPseudo && Equals(culture, _culture))
        {
            return;
        }

        IsPseudo = pseudo;
        _culture = culture;
        _formatter = CreateFormatter(culture);
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The text for <paramref name="key"/>, with no placeholders filled in.</summary>
    public string this[string key] => Format(key);

    /// <summary>
    /// The text for <paramref name="key"/> with <paramref name="args"/> filled in. A missing key shows as
    /// <c>⟦key⟧</c>, so it stands out on screen and in tests.
    /// </summary>
    public string Format(string key, IReadOnlyDictionary<string, object?>? args = null) =>
        TryFormat(key, args) ?? $"⟦{key}⟧";

    /// <summary>
    /// What to tell the user about <paramref name="error"/>: its translated text, or the engine's English message when
    /// this version has no text for the code or the arguments don't fit it.
    /// </summary>
    public string Format(EngineError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var args = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in error.Args ?? new Dictionary<string, string>())
        {
            args[name] = value;
        }
        if (
            error.Arg(EngineErrorArgs.Index) is { } index
            && int.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out var at)
        )
        {
            args["position"] = at + 1; // people count characters from 1
        }
        return TryFormat("Error." + error.Code, args) ?? error.Message;
    }

    /// <summary>What to tell the user about <paramref name="exception"/>: an engine error's text, or its message.</summary>
    public string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is IHasEngineError coded ? Format(coded.EngineError) : exception.Message;
    }

    /// <summary>The text for <paramref name="key"/> now and after every language change, for bindings.</summary>
    public IObservable<string> Observe(string key) => Observe(() => this[key]);

    /// <summary><paramref name="text"/> now and after every language change, for bindings.</summary>
    public IObservable<string> Observe(Func<string> text) => new LanguageObservable(this, text);

    /// <summary>Whether <paramref name="key"/> is in the English strings.</summary>
    internal static bool Exists(string key) => Resources.GetString(key, CultureInfo.InvariantCulture) is not null;

    private string? TryFormat(string key, IReadOnlyDictionary<string, object?>? args)
    {
        var pattern = Resources.GetString(key, IsPseudo ? CultureInfo.InvariantCulture : _culture);
        if (pattern is null)
        {
            return null;
        }
        if (IsPseudo)
        {
            pattern = PseudoLocalizer.Transform(pattern);
        }
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in args ?? new Dictionary<string, object?>())
        {
            values[name] = value;
            if (value is IFormattable number and (int or long or double or decimal or float or short))
            {
                values[name + FormattedSuffix] = number.ToString("#,##0.###", _culture);
            }
        }
        try
        {
            return _formatter.FormatMessage(BoundPatterns.GetOrAdd(pattern, BindNumberSigns), values);
        }
        catch (Exception ex) when (ex is MessageFormatterException or KeyNotFoundException or FormatException)
        {
            return null; // a placeholder the caller didn't supply, or a broken translation
        }
    }

    /// <summary>
    /// Points each <c>#</c> in a plural branch at the plural's number formatted for the culture
    /// (<c>{count__formatted}</c>), because the formatter writes <c>#</c> as a bare number (<c>1234</c>, not
    /// <c>1,234</c>). The plural form is still chosen from the number itself.
    /// </summary>
    internal static string BindNumberSigns(string pattern)
    {
        var text = new StringBuilder(pattern.Length + 16);
        var arguments = new Stack<(string Name, bool Plural)>();
        var depth = 0;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '{')
            {
                depth++;
                if (depth % 2 == 1)
                {
                    // An argument: {name}, {name, plural, …} or {name, select, …}.
                    var head = ArgumentHead().Match(pattern, i);
                    arguments.Push((head.Groups[1].Value, head.Groups[2].Value is "plural" or "selectordinal"));
                }
            }
            else if (c == '}')
            {
                if (depth % 2 == 1 && arguments.Count > 0)
                {
                    arguments.Pop();
                }
                depth--;
            }
            else if (c == '#' && depth > 0 && depth % 2 == 0 && arguments.TryPeek(out var owner) && owner.Plural)
            {
                text.Append('{').Append(owner.Name).Append(FormattedSuffix).Append('}');
                continue;
            }
            text.Append(c);
        }
        return text.ToString();
    }

    [GeneratedRegex(@"\G\{\s*(\w+)\s*(?:,\s*(\w+))?")]
    private static partial Regex ArgumentHead();

    // The invariant culture (a Linux machine or container with no locale set) has no plural rules, so "one" would never
    // be chosen; its text is English, so English plural rules apply.
    private static MessageFormatter CreateFormatter(CultureInfo culture) =>
        new(true, culture.Name.Length == 0 ? English : culture);

    private static CultureInfo? Find(string language)
    {
        try
        {
            return CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    private sealed class LanguageObservable(Localizer localizer, Func<string> text) : IObservable<string>
    {
        public IDisposable Subscribe(IObserver<string> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            void OnChanged(object? sender, EventArgs e) => observer.OnNext(text());
            observer.OnNext(text());
            localizer.LanguageChanged += OnChanged;
            return new Unsubscriber(() => localizer.LanguageChanged -= OnChanged);
        }
    }

    private sealed class Unsubscriber(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }
}
