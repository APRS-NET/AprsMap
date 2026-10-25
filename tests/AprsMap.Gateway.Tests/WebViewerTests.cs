// SPDX-License-Identifier: AGPL-3.0-only
using System.Net;
using System.Net.Sockets;
using AprsMap.Client.Remote;
using AprsMap.Contracts;

namespace AprsMap.Gateway.Tests;

/// <summary>The desktop's web viewer (docs/features/F1.35-desktop-web-viewer.md, acceptance tests 1–3).</summary>
public sealed class WebViewerTests(GatewayFactory gateway) : IClassFixture<GatewayFactory>, IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _webRoot = Directory.CreateTempSubdirectory("aprsmap-webroot-").FullName;

    public void Dispose() => Directory.Delete(_webRoot, recursive: true);

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>The desktop's own engine, tiles and logging: the gateway test host stands in for the desktop's.</summary>
    private async Task<WebViewer> StartAsync(bool localNetwork = false)
    {
        _ = gateway.Server; // builds the host
        await File.WriteAllTextAsync(Path.Combine(_webRoot, "index.html"), "<!doctype html><title>viewer</title>", Ct);
        return await WebViewer.StartAsync(
            new WebViewerOptions(FreePort(), localNetwork, _webRoot, HomePrivacy.Exact),
            gateway.Services,
            Ct
        );
    }

    [Fact]
    public async Task ABrowserGetsTheAppAndTheStationOnThisComputer()
    {
        await using var viewer = await StartAsync();
        var address = Assert.Single(viewer.Addresses);
        Assert.Equal("localhost", address.Host);

        using var http = new HttpClient { BaseAddress = address };
        var page = await http.GetStringAsync("/", Ct);
        Assert.Contains("<title>viewer</title>", page, StringComparison.Ordinal);
        using var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = address,
        };
        using var source = await noRedirect.GetAsync("/source", Ct); // the AGPL offer, never followed out to the network
        Assert.Equal(HttpStatusCode.Redirect, source.StatusCode);
        Assert.Equal(Brand.SourceUrl, source.Headers.Location?.ToString());
        var providers = await http.GetStringAsync("/tiles/providers", Ct);
        Assert.StartsWith("[", providers, StringComparison.Ordinal);

        await using var browser = RemoteEngineService.Create(address);
        await browser.StartAsync(Ct);
        var status = await browser.GetStatusAsync(Ct);
        Assert.Equal(1, status.Viewers);
        Assert.Equal(1, viewer.ViewerCount);
    }

    [Fact]
    public async Task ABrowserCanOnlyWatch()
    {
        await using var viewer = await StartAsync();
        await using var browser = RemoteEngineService.Create(viewer.Addresses[0]);
        await browser.StartAsync(Ct);

        var settings = await browser.GetSettingsAsync(Ct);
        Assert.True(settings.ReadOnly); // view only (behavior 6)
        await Assert.ThrowsAnyAsync<Exception>(() => browser.SaveSettingsAsync(settings with { ReadOnly = false }, Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => browser.SetToggleAsync(Toggle.Transmit, true, Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => browser.SendPositionAsync(null, Ct));
        await Assert.ThrowsAnyAsync<Exception>(() => browser.GetUpdateStatusAsync(Ct));
    }

    [Fact]
    public async Task OnTheLocalNetworkItSaysEachAddressToOpen()
    {
        await using var viewer = await StartAsync(localNetwork: true);

        Assert.Equal("localhost", viewer.Addresses[0].Host);
        Assert.All(
            viewer.Addresses.Skip(1),
            a => Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, IPAddress.Parse(a.Host).AddressFamily)
        );
        using var http = new HttpClient { BaseAddress = viewer.Addresses[0] };
        Assert.Contains("viewer", await http.GetStringAsync("/", Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItStopsWhileABrowserIsStillWatching()
    {
        var viewer = await StartAsync();
        await using var browser = RemoteEngineService.Create(viewer.Addresses[0]);
        await browser.StartAsync(Ct);
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var streams = new[]
        {
            Drain(browser.WatchStationsAsync(null, watching.Token)),
            Drain(browser.WatchPacketsAsync(null, watching.Token)),
            Drain(browser.WatchBulletinsAsync(watching.Token)),
        };
        await browser.GetStatusAsync(Ct);

        // The app closing with a tab still open (F4.15 behavior 11: Restart now waits for this before the update).
        await viewer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await watching.CancelAsync();
        foreach (var stream in streams)
        {
            await stream.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    [Fact]
    public async Task ItStopsFromAThreadWhoseDispatcherHasStopped()
    {
        var viewer = await StartAsync();
        var stopped = false;

        // As the desktop app closes: the UI thread's context is still set, and nothing runs what's posted to it.
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new StoppedDispatcherContext());
            stopped = viewer.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
        });
        thread.Start();
        thread.Join();

        Assert.True(stopped);
    }

    private sealed class StoppedDispatcherContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }

        public override void Send(SendOrPostCallback d, object? state) { }
    }

    private static async Task Drain<T>(IAsyncEnumerable<T> stream)
    {
        await foreach (var _ in stream) { }
    }

    [Fact]
    public async Task ATakenPortFailsToStart()
    {
        var port = FreePort();
        using var taken = new TcpListener(IPAddress.Loopback, port);
        taken.Start();
        _ = gateway.Server;
        await Assert.ThrowsAnyAsync<IOException>(() =>
            WebViewer.StartAsync(new WebViewerOptions(port, false, _webRoot, HomePrivacy.Exact), gateway.Services, Ct)
        );
    }
}
