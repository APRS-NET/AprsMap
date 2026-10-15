// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;

namespace AprsMap.UI.Tests;

public class LocalizationTests
{
    private static Localizer English() => new(CultureInfo.GetCultureInfo("en-US"));

    private static Dictionary<string, object?> Count(long count) => new() { ["count"] = count };

    [Theory]
    [InlineData(0, "0 packets")]
    [InlineData(1, "1 packet")]
    [InlineData(2, "2 packets")]
    [InlineData(1234, "1,234 packets")]
    public void PluralsFollowTheCount(long count, string expected) =>
        Assert.Equal(expected, English().Format("Main.PacketCount", Count(count)));

    [Fact]
    public void WithNoLocaleSetPluralsFollowEnglish() =>
        Assert.Equal(
            "1 packet",
            new Localizer(System.Globalization.CultureInfo.InvariantCulture).Format("Main.PacketCount", Count(1))
        );

    [Fact]
    public void ApostrophesAndPlaceholdersComeThrough()
    {
        var text = English();

        Assert.Equal(
            "Couldn't reach the address lookup server. Check the connection, or use another way.",
            text["Error.geocoding.unreachable"]
        );
        Assert.Equal(
            "APRS-IS connected · T2TEST",
            text.Format(
                "Status.Connected",
                new Dictionary<string, object?> { ["server"] = "T2TEST", ["verified"] = "no" }
            )
        );
    }

    [Fact]
    public void EngineErrorsAreTranslatedWithTheCharacterPositionCountedFromOne()
    {
        var error = new EngineError(
            "aprs.callsign-invalid-character",
            "English",
            new Dictionary<string, string>
            {
                [EngineErrorArgs.Value] = "N0 CALL",
                [EngineErrorArgs.Index] = "2",
                [EngineErrorArgs.Character] = " ",
            }
        );

        Assert.Equal("“N0 CALL” isn't a callsign: character 3 (“ ”) isn't allowed here.", English().Format(error));
    }

    [Theory]
    [InlineData("square", "Character 3 of “DNX0” (“X”) must be a digit.")]
    [InlineData("field", "Character 3 of “DNX0” (“X”) must be a letter from A to R.")]
    public void LocatorErrorsNameWhatBelongsThere(string part, string expected)
    {
        var error = new EngineError(
            EngineErrorCodes.LocatorInvalidCharacter,
            "English",
            new Dictionary<string, string>
            {
                [EngineErrorArgs.Value] = "DNX0",
                [EngineErrorArgs.Index] = "2",
                [EngineErrorArgs.Character] = "X",
                [EngineErrorArgs.Part] = part,
            }
        );

        Assert.Equal(expected, English().Format(error));
    }

    [Fact]
    public void AnErrorThisVersionCantWordShowsTheEnginesEnglish()
    {
        var text = English();

        Assert.Equal("Something new.", text.Format(new EngineError("future.code", "Something new.")));
        // The text needs a profile name the error doesn't carry.
        Assert.Equal(
            "English fallback",
            text.Format(new EngineError(EngineErrorCodes.ProfileNameDuplicate, "English fallback"))
        );
        Assert.Equal("plain", text.Describe(new InvalidOperationException("plain")));
        Assert.Equal(
            text["Error.geocoding.disabled"],
            text.Describe(EngineErrors.Create(EngineErrorCodes.GeocodingDisabled, "English"))
        );
    }

    [Theory]
    [InlineData(
        "{n, plural, one {# item} other {# items}}",
        "{n, plural, one {{n__formatted} item} other {{n__formatted} items}}"
    )]
    [InlineData("{n} # {k, select, a {#} other {x}}", "{n} # {k, select, a {#} other {x}}")]
    [InlineData(
        "{a, plural, other {{b, select, x {#} other {y}} and #}}",
        "{a, plural, other {{b, select, x {#} other {y}} and {a__formatted}}}"
    )]
    public void NumberSignsInPluralsPointAtTheFormattedNumber(string pattern, string expected) =>
        Assert.Equal(expected, Localizer.BindNumberSigns(pattern));

    [Fact]
    public void AMissingKeyStandsOut() => Assert.Equal("⟦No.Such.Key⟧", English()["No.Such.Key"]);

    [Fact]
    public void ThePseudoLanguageAccentsLengthensAndBracketsButKeepsPlaceholders()
    {
        Assert.Equal(
            "[ÅÞŔŠ-ÎŠ çöññéçţéð · {server} ······]",
            PseudoLocalizer.Transform("APRS-IS connected · {server}")
        );
        Assert.Equal(
            "[{count, plural, one {# þåçķéţ} other {# þåçķéţš}} ······]",
            PseudoLocalizer.Transform("{count, plural, one {# packet} other {# packets}}")
        );
        Assert.Equal("[]", PseudoLocalizer.Transform(string.Empty));

        var text = English();
        text.SetLanguage(Localizer.PseudoLanguage);
        Assert.True(text.IsPseudo);
        Assert.Equal("[2 þåçķéţš ······]", text.Format("Main.PacketCount", Count(2)));
    }

    [Fact]
    public void ChoosingALanguageFallsBackSensiblyAndOnlyAnnouncesRealChanges()
    {
        var text = English();
        var changes = 0;
        text.LanguageChanged += (_, _) => changes++;

        text.SetLanguage(null);
        Assert.Equal(0, changes); // already the system language

        text.SetLanguage("de-DE"); // no German yet: German formatting, English text
        Assert.Equal("de-DE", text.Culture.Name);
        Assert.Equal("1.234 packets", text.Format("Main.PacketCount", Count(1234)));
        Assert.Equal(1, changes);

        text.SetLanguage("not a language!");
        Assert.Equal("en-US", text.Culture.Name);
        text.SetLanguage(Localizer.PseudoLanguage);
        text.SetLanguage(Localizer.PseudoLanguage);
        Assert.Equal(3, changes);
        text.SetLanguage("");
        Assert.False(text.IsPseudo);
        Assert.Equal(4, changes);
    }

    [Fact]
    public void ObservedTextFollowsTheLanguageUntilDisposed()
    {
        var text = English();
        var seen = new List<string>();
        var subscription = text.Observe("Status.Disconnected").Subscribe(new Collect(seen));

        text.SetLanguage(Localizer.PseudoLanguage);
        subscription.Dispose();
        text.SetLanguage(null);

        Assert.Equal(["APRS-IS disconnected", PseudoLocalizer.Transform("APRS-IS disconnected")], seen);
    }

    [Fact]
    public void TheLanguagesOnOfferIncludeThePseudoLanguageOnlyWhenAsked()
    {
        Assert.Equal(["en"], Localizer.Languages(includePseudo: false));
        Assert.Equal(["en", Localizer.PseudoLanguage], Localizer.Languages(includePseudo: true));
    }

    [Fact]
    public void GuardsAgainstNullArguments()
    {
        var text = English();
        Assert.Throws<ArgumentNullException>(() => text.Format((EngineError)null!));
        Assert.Throws<ArgumentNullException>(() => text.Describe(null!));
        Assert.Throws<ArgumentNullException>(() => PseudoLocalizer.Transform(null!));
        Assert.Throws<ArgumentNullException>(() => text.Observe("Status.Disconnected").Subscribe(null!));
    }

    private sealed class Collect(List<string> seen) : IObserver<string>
    {
        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(string value) => seen.Add(value);
    }
}
