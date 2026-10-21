// SPDX-License-Identifier: MIT
using System.Text;
using AprsMap.Contracts;

namespace AprsMap.Core.Tests;

/// <summary>Reading and writing the components list (docs/features/F1.34-about-components.md, behaviors 1, 4).</summary>
public sealed class ComponentReportTests
{
    [Fact]
    public void TheBuildsListIsReadWithFrameworksAndLicences()
    {
        const string json = """
            {"host":"Browser","components":[
              {"name":"Microsoft.NETCore.App","version":"10.0.12","licence":"MIT","framework":true},
              {"name":"AprsNet.DeviceId","version":"0.5.0-alpha.2","licence":"file: LICENSE.md","framework":false},
              {"name":"MessageFormat","version":"8.0.0","licence":null,"framework":false},
              {"name":"","version":"1.0.0"}
            ]}
            """;

        var (host, list) = ComponentReport.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(ComponentHost.Browser, host);
        Assert.Equal(
            [
                new ComponentInfo("Microsoft.NETCore.App", "10.0.12", "MIT", true),
                new ComponentInfo("AprsNet.DeviceId", "0.5.0-alpha.2", "file: LICENSE.md", false),
                new ComponentInfo("MessageFormat", "8.0.0", null, false),
            ],
            list
        );
    }

    [Fact]
    public void AnAssemblyWithoutAListGivesItsVersionAndNoComponents()
    {
        var report = ComponentReport.Of(typeof(ComponentReportTests).Assembly);

        Assert.Equal(ComponentHost.Other, report.Host);
        Assert.Empty(report.Components);
        Assert.StartsWith(".NET", report.Runtime, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(report.OperatingSystem));
    }

    [Fact]
    public void AsTextEachComponentIsALineInColumns()
    {
        var report = new ComponentReport(
            ComponentHost.Desktop,
            "0.6.0",
            ".NET 10.0.12",
            "Linux (X64)",
            [
                new ComponentInfo("Microsoft.NETCore.App", "10.0.12", "MIT", true),
                new ComponentInfo("Avalonia", "12.1.0", "MIT", false),
                new ComponentInfo("MessageFormat", "8.0.0", null, false),
            ]
        );

        Assert.Equal(
            """
            Desktop app 0.6.0
            .NET 10.0.12 · Linux (X64)
            Microsoft.NETCore.App  10.0.12  MIT
            Avalonia               12.1.0   MIT
            MessageFormat          8.0.0    ?
            """.ReplaceLineEndings("\n"),
            report.ToText("Desktop app")
        );
    }

    [Fact]
    public async Task TheEngineAnswersWithTheRunningAppsList()
    {
        using var engine = new Engine(
            Microsoft.Extensions.Options.Options.Create(new EngineOptions()),
            new EngineTests.FakeSourceFactory(),
            new InMemorySettingsStore(),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Engine>.Instance
        );
        var report = await engine.GetComponentsAsync(TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrEmpty(report.Runtime));
    }
}
