// SPDX-License-Identifier: AGPL-3.0-only
using System.Runtime.InteropServices.JavaScript;
using AprsMap.Contracts;
using AprsMap.UI.Platform;

namespace AprsMap.Browser;

/// <summary>
/// The viewer's preferences in this browser's <c>localStorage</c> (F0.6 behavior 16), under keys that start with the
/// product slug. Private browsing that refuses storage just means nothing is kept (main.js swallows the error).
/// </summary>
internal sealed partial class BrowserPreferenceStore : IPreferenceStore
{
    private static string Prefixed(string key) => $"{Brand.Slug}.{key}";

    /// <inheritdoc />
    public Task<string?> LoadAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(Get(Prefixed(key)));

    /// <inheritdoc />
    public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        Set(Prefixed(key), value);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetAsideAsync(string key, CancellationToken cancellationToken = default)
    {
        if (Get(Prefixed(key)) is { } value)
        {
            Set(Prefixed(key) + ".invalid", value);
            Remove(Prefixed(key));
        }
        return Task.CompletedTask;
    }

    /// <summary>Whether the browser asks for reduced motion (<c>prefers-reduced-motion: reduce</c>).</summary>
    internal static bool PrefersReducedMotion() => ReducedMotion();

    [JSImport("prefGet", "main.js")]
    private static partial string? Get(string key);

    [JSImport("prefSet", "main.js")]
    private static partial void Set(string key, string value);

    [JSImport("prefRemove", "main.js")]
    private static partial void Remove(string key);

    [JSImport("prefersReducedMotion", "main.js")]
    private static partial bool ReducedMotion();

    /// <summary>Whether the primary pointer is coarse: a touch screen (<c>pointer: coarse</c>).</summary>
    internal static bool IsCoarsePointer() => CoarsePointer();

    [JSImport("coarsePointer", "main.js")]
    private static partial bool CoarsePointer();
}
