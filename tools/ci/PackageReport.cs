#:property TargetFramework=net10.0
#:property TargetFrameworks=
#:property PublishAot=false
// SPDX-License-Identifier: MIT
// Copied from APRS.NET (same author, MIT) so both repos report packages the same way.

// Turns `dotnet list package --format json` output into the markdown package report that CI posts to the run summary
// and to pull requests.
//
//   dotnet list AprsMap.slnx package --vulnerable --include-transitive --format json > vulnerable.json
//   dotnet list AprsMap.slnx package --outdated --format json > outdated.json
//   dotnet run tools/ci/PackageReport.cs -- --vulnerable vulnerable.json --outdated outdated.json --out report.md
//
// Writes `vulnerable=<count>` to $GITHUB_OUTPUT when set. Always exits 0 on a valid report so CI can post it before
// failing on the count.

using System.Globalization;
using System.Text;
using System.Text.Json;

string? vulnerablePath = null;
string? outdatedPath = null;
string? outPath = null;
for (var i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--vulnerable":
            vulnerablePath = args[++i];
            break;
        case "--outdated":
            outdatedPath = args[++i];
            break;
        case "--out":
            outPath = args[++i];
            break;
    }
}

if (vulnerablePath is null || outdatedPath is null || outPath is null)
{
    Console.Error.WriteLine("usage: PackageReport.cs --vulnerable <json> --outdated <json> --out <markdown>");
    return 3;
}

// Vulnerable packages: one row per package version and advisory, with the projects that use it.
var vulnerable = new SortedDictionary<(string Id, string Version, string Advisory), Vulnerability>();
foreach (var (project, package, transitive) in Packages(vulnerablePath))
{
    var version = Text(package, "resolvedVersion") ?? "?";
    if (!package.TryGetProperty("vulnerabilities", out var advisories))
    {
        continue;
    }

    foreach (var advisory in advisories.EnumerateArray())
    {
        var url = Text(advisory, "advisoryurl") ?? "";
        var key = (Text(package, "id")!, version, url);
        if (!vulnerable.TryGetValue(key, out var row))
        {
            row = new Vulnerability(Text(advisory, "severity") ?? "Unknown", transitive);
            vulnerable[key] = row;
        }

        row.Projects.Add(project);
        row.Transitive &= transitive; // direct anywhere wins: that is where the fix goes
    }
}

// Available updates for direct packages, once per package and current version.
var updates = new SortedDictionary<(string Id, string Current), string>();
foreach (var (_, package, transitive) in Packages(outdatedPath))
{
    var latest = Text(package, "latestVersion");
    var current = Text(package, "resolvedVersion");
    if (
        transitive
        || latest is null
        || current is null
        || latest == current
        || latest.StartsWith("Not found", StringComparison.Ordinal)
    )
    {
        continue;
    }

    updates[(Text(package, "id")!, current)] = latest;
}

var md = new StringBuilder();
md.Append("<!-- aprsmap-package-report -->\n## Package report\n\n### Vulnerable packages\n\n");
if (vulnerable.Count == 0)
{
    md.Append("No known vulnerabilities in direct or transitive packages.\n");
}
else
{
    md.Append(
        "| Package | Version | Severity | Advisory | Reference | Projects |\n| --- | --- | --- | --- | --- | --- |\n"
    );
    foreach (
        var ((id, version, url), row) in vulnerable.OrderBy(v => SeverityRank(v.Value.Severity)).ThenBy(v => v.Key)
    )
    {
        var advisory = url.Length > 0 ? $"[{url[(url.LastIndexOf('/') + 1)..]}]({url})" : "";
        md.Append(
            CultureInfo.InvariantCulture,
            $"| `{id}` | {version} | {row.Severity} | {advisory} | {(row.Transitive ? "transitive" : "direct")} | "
                + $"{string.Join(", ", row.Projects.Order(StringComparer.Ordinal))} |\n"
        );
    }
}

md.Append("\n### Available package updates\n\n");
if (updates.Count == 0)
{
    md.Append("All direct packages are on their latest version.\n");
}
else
{
    md.Append("| Package | Current version | Available version |\n| --- | --- | --- |\n");
    foreach (var ((id, current), latest) in updates)
    {
        md.Append(CultureInfo.InvariantCulture, $"| `{id}` | {current} | {latest} |\n");
    }
}

var sha = Environment.GetEnvironmentVariable("GITHUB_SHA");
md.Append(
    sha is { Length: >= 7 }
        ? $"\n<sub>Checked at {sha[..7]} against nuget.org.</sub>\n"
        : "\n<sub>Checked against nuget.org.</sub>\n"
);
File.WriteAllText(outPath, md.ToString());

if (Environment.GetEnvironmentVariable("GITHUB_OUTPUT") is { Length: > 0 } output)
{
    File.AppendAllText(output, $"vulnerable={vulnerable.Count}\nupdates={updates.Count}\n");
}

Console.WriteLine($"{vulnerable.Count} vulnerable package advisories, {updates.Count} available updates -> {outPath}");
return 0;

IEnumerable<(string Project, JsonElement Package, bool Transitive)> Packages(string path)
{
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    var rows = new List<(string, JsonElement, bool)>();
    foreach (var project in document.RootElement.GetProperty("projects").EnumerateArray())
    {
        var name = Path.GetFileNameWithoutExtension(Text(project, "path") ?? "?");
        if (!project.TryGetProperty("frameworks", out var frameworks))
        {
            continue;
        }

        foreach (var framework in frameworks.EnumerateArray())
        {
            foreach (var (property, transitive) in new[] { ("topLevelPackages", false), ("transitivePackages", true) })
            {
                if (framework.TryGetProperty(property, out var packages))
                {
                    rows.AddRange(packages.EnumerateArray().Select(p => (name, p.Clone(), transitive)));
                }
            }
        }
    }

    return rows;
}

static string? Text(JsonElement element, string property) =>
    element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;

static int SeverityRank(string severity) =>
    severity switch
    {
        "Critical" => 0,
        "High" => 1,
        "Moderate" => 2,
        "Low" => 3,
        _ => 4,
    };

internal sealed class Vulnerability(string severity, bool transitive)
{
    public string Severity { get; } = severity;

    public bool Transitive { get; set; } = transitive;

    public SortedSet<string> Projects { get; } = new(StringComparer.Ordinal);
}
