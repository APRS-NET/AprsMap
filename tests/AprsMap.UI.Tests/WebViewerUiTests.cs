// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.UI.Localization;
using AprsMap.UI.Platform;
using AprsMap.UI.Settings;

namespace AprsMap.UI.Tests;

/// <summary>Settings › Web viewer (docs/features/F1.35-desktop-web-viewer.md, acceptance tests 4, 5).</summary>
public sealed class WebViewerUiTests
{
    /// <summary>Stands in for the desktop's web viewer: it serves when on, unless told the port is taken.</summary>
    private sealed class FakeHost(bool available = true) : IWebViewerHost
    {
        public bool PortTaken { get; set; }

        public int Viewers { get; set; }

        public List<WebViewerSettings> Applied { get; } = [];

        public bool Available { get; } = available;

        public WebViewerSettings Settings { get; private set; } = new();

        public WebViewerState State =>
            !Settings.On ? WebViewerState.Off
            : PortTaken ? new WebViewerState(false, [], 0, WebViewerProblem.PortInUse, "Address already in use")
            : new WebViewerState(
                true,
                Settings.LocalNetwork
                    ? [new($"http://localhost:{Settings.Port}/"), new($"http://192.168.1.20:{Settings.Port}/")]
                    : [new($"http://localhost:{Settings.Port}/")],
                Viewers
            );

        public event EventHandler? Changed;

        public Task ApplyAsync(WebViewerSettings settings, CancellationToken cancellationToken = default)
        {
            Settings = settings;
            Applied.Add(settings);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    private static Localizer English() => new(CultureInfo.GetCultureInfo("en-US"));

    [Fact]
    public async Task SwitchingItOnSharesOnThisComputerAndSaysWhereToLook()
    {
        var host = new FakeHost();
        var section = new WebViewerSectionViewModel(host, English());
        Assert.False(section.On);
        Assert.Equal("Off", section.StatusText);
        Assert.Equal("8090", section.Port);
        Assert.True(section.ThisComputerOnly);

        section.On = true;
        await Task.Yield();

        Assert.Equal(new WebViewerSettings(On: true), Assert.Single(host.Applied));
        Assert.Equal("Sharing: nobody watching yet. Open one of these:", section.StatusText);
        Assert.Equal(["http://localhost:8090/"], section.Addresses.Select(a => a.Address));

        host.Viewers = 2;
        section.Refresh();
        Assert.Equal("Sharing: 2 browsers watching. Open one of these:", section.StatusText);
    }

    [Fact]
    public async Task TheNetworkThePortAndTheLocationApplyTogether()
    {
        var host = new FakeHost();
        var section = new WebViewerSectionViewModel(host, English());
        section.On = true;
        await Task.Yield();

        section.LocalNetwork = true;
        section.Port = "8095";
        section.Location = section.LocationOptions.Single(o => o.Value == ViewerLocation.Grid);
        Assert.True(section.ApplyCommand.CanExecute(null));
        await section.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(new WebViewerSettings(true, 8095, true, ViewerLocation.Grid), host.Settings);
        Assert.Equal(2, section.Addresses.Count);
        Assert.False(section.Changed);

        section.Port = "80";
        Assert.Equal("The port is a number from 1024 to 65535.", section.PortError);
        Assert.False(section.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task ATakenPortIsSaid()
    {
        var host = new FakeHost { PortTaken = true };
        var section = new WebViewerSectionViewModel(host, English());

        section.On = true;
        await Task.Yield();

        Assert.True(section.HasProblem);
        Assert.Equal("Port 8090 is in use by another program. Choose another port and apply.", section.StatusText);
        Assert.Empty(section.Addresses);
    }

    [Fact]
    public void WithoutTheBrowserAppItCantBeTurnedOn()
    {
        var section = new WebViewerSectionViewModel(new FakeHost(available: false), English());

        Assert.False(section.Available);
        Assert.StartsWith("This build doesn't include the browser app", section.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopyPutsAnAddressOnTheClipboard()
    {
        var host = new FakeHost();
        var copied = new List<string>();
        var section = new WebViewerSectionViewModel(host, English())
        {
            CopyToClipboard = text =>
            {
                copied.Add(text);
                return Task.CompletedTask;
            },
        };
        section.On = true;
        await Task.Yield();

        await section.Addresses[0].Copy.ExecuteAsync(null);

        Assert.Equal(["http://localhost:8090/"], copied);
    }
}
