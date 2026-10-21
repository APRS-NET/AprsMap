// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Settings;

namespace AprsMap.UI.Tests;

/// <summary>Help › About's components (docs/features/F1.34-about-components.md, acceptance tests 2–4).</summary>
public sealed class AboutComponentsTests
{
    private static Localizer English() => new(CultureInfo.GetCultureInfo("en-US"));

    private static readonly ComponentReport Browser = new(
        ComponentHost.Browser,
        "0.6.0-alpha.1+0123456789abcdef",
        ".NET 10.0.12",
        "Browser (Wasm)",
        [
            new ComponentInfo("Microsoft.NETCore.App", "10.0.12", "MIT", true),
            new ComponentInfo("Avalonia", "12.1.0", "MIT", false),
            new ComponentInfo("AprsNet.DeviceId", "0.5.0-alpha.2", "file: LICENSE.md", false),
            new ComponentInfo("MessageFormat", "8.0.0", null, false),
        ]
    );

    [Fact]
    public async Task OnTheDesktopTheAppsOwnListShowsOnce()
    {
        var engine = new FakeEngine { Components = Browser with { Host = ComponentHost.Desktop } };
        var about = new AboutViewModel(English());

        await about.ShowComponentsAsync(engine, Browser, sharedStation: false, isOperator: true);

        var group = Assert.Single(about.ComponentGroups);
        Assert.Equal("Desktop app 0.6.0-alpha.1 (0123456)", group.Heading);
        Assert.Equal(".NET 10.0.12 · Browser (Wasm)", group.RuntimeText);
        Assert.Equal("Components (4)", about.ComponentsHeading);
        Assert.Equal(
            ["MIT", "MIT", "licence file in the package", "licence not stated"],
            group.Rows.Select(r => r.Licence)
        );
    }

    [Fact]
    public async Task AViewerSeesTheBrowserAppsListAndTheOperatorTheGatewaysToo()
    {
        var engine = new FakeEngine();
        var viewer = new AboutViewModel(English());
        await viewer.ShowComponentsAsync(engine, Browser, sharedStation: true, isOperator: false);
        Assert.Equal(["Browser app"], viewer.ComponentGroups.Select(g => g.Name));

        var operatorAbout = new AboutViewModel(English());
        await operatorAbout.ShowComponentsAsync(engine, Browser, sharedStation: true, isOperator: true);
        Assert.Equal(["Browser app", "Gateway"], operatorAbout.ComponentGroups.Select(g => g.Name));
        Assert.Equal("Components (6)", operatorAbout.ComponentsHeading);
    }

    [Fact]
    public async Task CopyPutsEveryListOnTheClipboardAsText()
    {
        var copied = new List<string>();
        var about = new AboutViewModel(English())
        {
            CopyToClipboard = text =>
            {
                copied.Add(text);
                return Task.CompletedTask;
            },
        };
        await about.ShowComponentsAsync(new FakeEngine(), Browser, sharedStation: true, isOperator: true);

        await about.CopyComponentsCommand.ExecuteAsync(null);

        var text = Assert.Single(copied);
        Assert.StartsWith(
            "Browser app 0.6.0-alpha.1+0123456789abcdef\n.NET 10.0.12 · Browser (Wasm)\n",
            text,
            StringComparison.Ordinal
        );
        Assert.Contains("\n\nGateway 0.6.0-alpha.1\n", text, StringComparison.Ordinal);
        Assert.Contains("MessageFormat          8.0.0          ?", text, StringComparison.Ordinal);
        Assert.Equal("Copied", about.ComponentsSaid);
    }
}
