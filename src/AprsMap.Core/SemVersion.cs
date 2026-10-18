// SPDX-License-Identifier: MIT
using System.Globalization;

namespace AprsMap.Core;

/// <summary>
/// A Semantic Versioning 2.0 version and its precedence (semver.org, section 11), for the update check
/// (docs/features/F4.15-updates.md, behavior 3): build metadata is ignored, a pre-release is older than its release,
/// and pre-release parts compare numerically when both are numbers, so <c>alpha.10</c> follows <c>alpha.9</c>.
/// </summary>
internal sealed record SemVersion(int Major, int Minor, int Patch, IReadOnlyList<string> PreRelease)
    : IComparable<SemVersion>
{
    /// <summary>Whether it has a pre-release part (<c>-alpha.1</c>).</summary>
    public bool IsPreRelease => PreRelease.Count > 0;

    /// <summary>Reads <c>1.2.3</c>, <c>1.2.3-alpha.4</c> or <c>1.2.3-alpha.4+build</c>, with an optional leading <c>v</c>.</summary>
    public static bool TryParse(string? text, out SemVersion version)
    {
        version = new SemVersion(0, 0, 0, []);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var value = text.Trim();
        if (value.StartsWith('v'))
        {
            value = value[1..];
        }
        var plus = value.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            value = value[..plus];
        }
        var dash = value.IndexOf('-', StringComparison.Ordinal);
        var core = (dash >= 0 ? value[..dash] : value).Split('.');
        if (
            core.Length != 3
            || !int.TryParse(core[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(core[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(core[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch)
        )
        {
            return false;
        }
        string[] pre = dash >= 0 ? value[(dash + 1)..].Split('.') : [];
        if (pre.Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            return false;
        }
        version = new SemVersion(major, minor, patch, pre);
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(SemVersion? other)
    {
        if (other is null)
        {
            return 1;
        }
        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0)
        {
            return core;
        }
        if (IsPreRelease != other.IsPreRelease)
        {
            return IsPreRelease ? -1 : 1; // a release follows its pre-releases
        }
        for (var i = 0; i < Math.Min(PreRelease.Count, other.PreRelease.Count); i++)
        {
            var a = PreRelease[i];
            var b = other.PreRelease[i];
            var aNumber = long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var an);
            var bNumber = long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var bn);
            var part =
                aNumber && bNumber ? an.CompareTo(bn)
                : aNumber ? -1
                : bNumber ? 1
                : string.CompareOrdinal(a, b);
            if (part != 0)
            {
                return part;
            }
        }
        return PreRelease.Count.CompareTo(other.PreRelease.Count);
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}" + (IsPreRelease ? "-" + string.Join('.', PreRelease) : string.Empty);
}
