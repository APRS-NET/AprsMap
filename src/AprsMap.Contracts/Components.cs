// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AprsMap.Contracts;

/// <summary>Which app a components list is for (docs/features/F1.34-about-components.md, behavior 2).</summary>
public enum ComponentHost
{
    /// <summary>Not one of the apps (tests).</summary>
    Other,

    /// <summary>The desktop app, engine and UI in one.</summary>
    Desktop,

    /// <summary>The gateway: the engine a browser talks to.</summary>
    Gateway,

    /// <summary>The browser app.</summary>
    Browser,
}

/// <summary>A package or shared framework an app is built from (F1.34 behavior 1).</summary>
/// <param name="Name">The package ID, such as <c>AprsNet.Packets</c>, or the framework's name.</param>
/// <param name="Version">Its version.</param>
/// <param name="Licence">
/// What its package states: an SPDX expression (<c>MIT</c>), <c>file: LICENSE.md</c> for a licence file in the package,
/// or an address; null when the package states none.
/// </param>
/// <param name="IsFramework">Whether it's a .NET shared framework rather than a package.</param>
public sealed record ComponentInfo(string Name, string Version, string? Licence, bool IsFramework);

/// <summary>
/// What an app is built from and runs on (docs/features/F1.34-about-components.md): its version, the .NET runtime and
/// OS it's running on, and its components, as the build wrote them (<c>build/Components.targets</c>) from the same
/// restore the release's SBOM is made from (F4.17).
/// </summary>
/// <param name="Host">Which app.</param>
/// <param name="Version">The app's version, as its build stamped it.</param>
/// <param name="Runtime">The .NET runtime it's running on, such as ".NET 10.0.12".</param>
/// <param name="OperatingSystem">The OS and architecture, such as "Ubuntu 24.04.3 LTS (X64)".</param>
/// <param name="Components">The frameworks first, then the packages by name; empty when the build wrote none.</param>
public sealed record ComponentReport(
    ComponentHost Host,
    string Version,
    string Runtime,
    string OperatingSystem,
    IReadOnlyList<ComponentInfo> Components
)
{
    /// <summary>The embedded resource the build writes the list to.</summary>
    public const string ResourceName = "AprsMap.Components.json";

    /// <summary>The report for <paramref name="app"/>, the running app's entry assembly by default.</summary>
    public static ComponentReport Of(Assembly? app = null)
    {
        app ??= Assembly.GetEntryAssembly();
        var version =
            app?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? app?.GetName().Version?.ToString()
            ?? string.Empty;
        using var stream = app?.GetManifestResourceStream(ResourceName);
        var (host, components) = stream is null ? (ComponentHost.Other, []) : Parse(stream);
        return new ComponentReport(
            host,
            version,
            RuntimeInformation.FrameworkDescription,
            $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            components
        );
    }

    /// <summary>
    /// Reads the list the build wrote: which app (<c>host</c>), and its components, each a name, version, licence and
    /// whether it's a framework.
    /// </summary>
    public static (ComponentHost Host, IReadOnlyList<ComponentInfo> Components) Parse(Stream json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var host =
            root.TryGetProperty("host", out var h) && Enum.TryParse<ComponentHost>(h.GetString(), out var parsed)
                ? parsed
                : ComponentHost.Other;
        var list = new List<ComponentInfo>();
        if (!root.TryGetProperty("components", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return (host, list);
        }
        foreach (var row in rows.EnumerateArray())
        {
            if (
                row.TryGetProperty("name", out var name)
                && name.GetString() is { Length: > 0 } n
                && row.TryGetProperty("version", out var version)
                && version.GetString() is { Length: > 0 } v
            )
            {
                list.Add(
                    new ComponentInfo(
                        n,
                        v,
                        row.TryGetProperty("licence", out var licence) && licence.ValueKind == JsonValueKind.String
                            ? licence.GetString()
                            : null,
                        row.TryGetProperty("framework", out var framework) && framework.ValueKind == JsonValueKind.True
                    )
                );
            }
        }
        return (host, list);
    }

    /// <summary>The whole report as plain text, one component a line, for a bug report (behavior 4).</summary>
    public string ToText(string heading)
    {
        var width = Components.Count == 0 ? 0 : Components.Max(c => c.Name.Length);
        var versionWidth = Components.Count == 0 ? 0 : Components.Max(c => c.Version.Length);
        var lines = new List<string> { $"{heading} {Version}", $"{Runtime} · {OperatingSystem}" };
        lines.AddRange(
            Components.Select(c =>
                $"{c.Name.PadRight(width)}  {c.Version.PadRight(versionWidth)}  {c.Licence ?? "?"}".TrimEnd()
            )
        );
        return string.Join('\n', lines);
    }
}
