// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;

namespace AprsMap.UI.Localization;

/// <summary>How my last position got out, in words (docs/features/F3.3-rf-paths.md, behaviors 2, 4).</summary>
public static class HeardBackText
{
    /// <summary>
    /// "Sent 14:05 on rotate.aprs2.net and Direwolf. Repeated by K0HQ-1 (RF), gated by N0IGT (to APRS-IS), sent to APRS-IS
    /// directly.", or "Not heard back on RF." once it's no longer listened for.
    /// </summary>
    public static string Describe(LastPositionHeard last, Localizer text, Func<DateTimeOffset, string> clock)
    {
        ArgumentNullException.ThrowIfNull(last);
        ArgumentNullException.ThrowIfNull(text);
        var sent = text.Format(
            "HeardBack.Sent",
            new Dictionary<string, object?> { ["time"] = clock(last.At), ["sources"] = string.Join(", ", last.Sources) }
        );
        var ways = last
            .Heard.Select(h => text.Format($"HeardBack.{h.Kind}", new Dictionary<string, object?> { ["by"] = h.By }))
            .ToList();
        var onRf = last.Heard.Any(h => h.Kind != HeardBackKind.Direct);
        if (last.SentOnRf && !onRf)
        {
            ways.Add(text[last.Following ? "HeardBack.Listening" : "HeardBack.NotOnRf"]);
        }
        if (ways.Count == 0)
        {
            return sent;
        }
        var list = string.Join(", ", ways);
        return sent + " " + text.Culture.TextInfo.ToUpper(list[0]) + list[1..] + ".";
    }

    /// <summary>"Last position heard back via K0HQ-1, 3 min ago" for the transmit tip; null without an RF copy.</summary>
    public static string? Tip(LastPositionHeard? last, DateTimeOffset now, Localizer text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (last?.Heard.FirstOrDefault(h => h.Kind != HeardBackKind.Direct) is not { } first)
        {
            return null;
        }
        return text.Format(
            "HeardBack.Tip",
            new Dictionary<string, object?>
            {
                ["by"] = first.By,
                ["minutes"] = (int)Math.Max(0, (now - first.At).TotalMinutes),
            }
        );
    }
}
