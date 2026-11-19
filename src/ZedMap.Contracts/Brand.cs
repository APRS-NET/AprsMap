// SPDX-License-Identifier: MIT
using System.Reflection;

namespace ZedMap.Contracts;

/// <summary>
/// Product identity, read from the assembly metadata that <c>build/Branding.props</c> stamps into every project.
/// User-visible text uses these values instead of literal names so the product can be renamed (docs/branding.md).
/// </summary>
public static class Brand
{
    private static readonly Dictionary<string, string> Metadata = typeof(Brand)
        .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Where(a => a.Key.StartsWith("Brand.", StringComparison.Ordinal))
        .ToDictionary(a => a.Key, a => a.Value ?? string.Empty, StringComparer.Ordinal);

    /// <summary>The name users see in titles, About and installers.</summary>
    public static string DisplayName => Get("Brand.DisplayName");

    /// <summary>Lower-case slug for folders, the container image and URLs.</summary>
    public static string Slug => Get("Brand.Slug");

    /// <summary>Slugs used by earlier names, oldest first, so user data can be migrated after a rename.</summary>
    public static IReadOnlyList<string> PreviousSlugs =>
        Get("Brand.PreviousSlugs").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// The slug a rename under way moves to (docs/branding.md, "Renaming in two steps"), so updates are found when the
    /// releases start using it; empty otherwise.
    /// </summary>
    public static string NextSlug => Get("Brand.NextSlug");

    /// <summary>The slugs a release's files may be named by, this one first: <see cref="Slug"/>, then <see cref="NextSlug"/>.</summary>
    public static IReadOnlyList<string> ReleaseSlugs =>
        NextSlug.Length == 0 || NextSlug == Slug ? [Slug] : [Slug, NextSlug];

    /// <summary>The transmitted device identifier (TOCALL).</summary>
    public static string Tocall => Get("Brand.Tocall");

    /// <summary>Where the source code is published (the AGPL network-use offer points here).</summary>
    public static string SourceUrl => Get("Brand.SourceUrl");

    /// <summary>The informational version of this build, as stamped by MinVer.</summary>
    public static string Version =>
        typeof(Brand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    internal static string Get(string key) => Metadata.TryGetValue(key, out var value) ? value : string.Empty;
}
