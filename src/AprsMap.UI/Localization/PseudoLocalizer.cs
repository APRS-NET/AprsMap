// SPDX-License-Identifier: AGPL-3.0-only
using System.Text;

namespace AprsMap.UI.Localization;

/// <summary>
/// Makes the pseudo-language (<see cref="Localizer.PseudoLanguage"/>) from English: letters get accents, the text
/// grows by about 40 % and is wrapped in brackets. English left on screen is a string that isn't localized; a missing
/// closing bracket is text that doesn't fit. Placeholders and plural syntax are left as they are.
/// </summary>
internal static class PseudoLocalizer
{
    private const string Plain = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Accented = "åƀçðéƒĝĥîĵķļɱñöþǫŕšţûṽŵẋýžÅƁÇÐÉƑĜĤÎĴĶĻṀÑÖÞǪŔŠŢÛṼŴẊÝŽ";

    /// <summary>The pseudo-language form of an ICU MessageFormat pattern.</summary>
    public static string Transform(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var text = new StringBuilder(pattern.Length * 2);
        var letters = 0;
        var depth = 0;
        text.Append('[');
        foreach (var c in pattern)
        {
            switch (c)
            {
                case '{':
                    depth++;
                    text.Append(c);
                    break;
                case '}':
                    depth--;
                    text.Append(c);
                    break;
                default:
                    // Even depth is message text (top level, or a plural or select branch); odd depth is a
                    // placeholder's own syntax (its name, "plural", "one"…), which must stay as written.
                    var at = depth % 2 == 0 && c != '#' ? Plain.IndexOf(c, StringComparison.Ordinal) : -1;
                    if (at >= 0)
                    {
                        letters++;
                        text.Append(Accented[at]);
                    }
                    else
                    {
                        text.Append(c);
                    }
                    break;
            }
        }
        var padding = (letters * 2 + 4) / 5; // 40 %
        if (padding > 0)
        {
            text.Append(' ').Append('·', padding);
        }
        return text.Append(']').ToString();
    }
}
