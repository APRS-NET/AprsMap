// SPDX-License-Identifier: AGPL-3.0-only
using System.Net;
using AprsMap.Client.Remote;
using AprsMap.Contracts;
using AprsMap.Core;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace AprsMap.Gateway.Tests;

public sealed class GatewayTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HealthCheckReportsHealthy()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/healthz", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task SourceLinkRedirectsToThePublishedSource()
    {
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync(new Uri("/source", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(Brand.SourceUrl, response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task RemoteClientSeesTheSameEngineAsTheHost()
    {
        var engine = factory.Services.GetRequiredService<Engine>();
        engine.Accept("N0CALL-9>APRS,WIDE1-1:!4903.50N/07201.75W>Heading home");
        await using var remote = CreateRemote();
        await remote.StartAsync(Ct);

        var status = await remote.GetStatusAsync(Ct);
        Assert.Equal(ConnectionState.Disconnected, status.Connection);
        Assert.True(status.PacketsReceived >= 1);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var watch = remote.WatchPacketsAsync(null, cts.Token).GetAsyncEnumerator(cts.Token);
        // The fixture's engine is shared, so the replay can start with other tests' packets.
        do
        {
            Assert.True(await watch.MoveNextAsync());
        } while (watch.Current.Source != "N0CALL-9");

        engine.Accept("N0LIVE>APRS:>Live status");
        PacketView live;
        do
        {
            Assert.True(await watch.MoveNextAsync());
            live = watch.Current;
        } while (live.Source != "N0LIVE");
        Assert.Equal("Status", live.Type);
        Assert.Equal(HubConnectionState.Connected, remote.State);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task FilteredPacketStreamSendsOnlyMatchingPackets()
    {
        var engine = factory.Services.GetRequiredService<Engine>();
        engine.Accept("N0FILT-1>APRS:>Hidden by the filter");
        engine.Accept("N0FILT-2>APRS:!9999.99N/99999.99W-");
        await using var remote = CreateRemote();
        await remote.StartAsync(Ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var filter = new PacketFilter(Text: "N0FILT", IssuesOnly: true);
        await using var watch = remote.WatchPacketsAsync(filter, cts.Token).GetAsyncEnumerator(cts.Token);

        Assert.True(await watch.MoveNextAsync());
        Assert.Equal("N0FILT-2", watch.Current.Source);
        Assert.Equal("aprs.parse.invalid-latitude", watch.Current.Issues![0].Code);
        engine.Accept("N0FILT-3>APRS:>Hidden too");
        engine.Accept("N0FILT-4>APRS:!9999.99N/99999.99W-");
        Assert.True(await watch.MoveNextAsync());
        Assert.Equal("N0FILT-4", watch.Current.Source);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task RemoteClientQueriesTheStationList()
    {
        var engine = factory.Services.GetRequiredService<Engine>();
        engine.Accept("N0QUERY-1>APRS:!4903.50N/07201.75W>One");
        engine.Accept("N0QUERY-2>APRS:!4903.50N/07201.75W>Two");
        await using var remote = CreateRemote();
        await remote.StartAsync(Ct);

        var page = await remote.QueryStationsAsync(new StationQuery("n0query", Sort: StationSort.Name, Count: 1), Ct);

        Assert.Equal(2, page.Total);
        Assert.Equal("N0QUERY-1", Assert.Single(page.Rows).Name);
        Assert.Equal("One", page.Rows[0].Comment);
    }

    [Fact]
    public async Task RemoteClientGetsAStationsFullEntry()
    {
        var engine = factory.Services.GetRequiredService<Engine>();
        engine.Accept("N0DETAIL>APDR16,N2DIGI*,qAR,N1GATE:!4903.50N/07201.75W>088/036 Detail test");
        await using var remote = CreateRemote();
        await remote.StartAsync(Ct);

        var detail = await remote.GetStationAsync("N0DETAIL", Ct);

        Assert.NotNull(detail);
        Assert.Equal(StationKind.Station, detail.Kind);
        Assert.Equal(88, detail.CourseDegrees);
        Assert.Equal("N2DIGI*,qAR,N1GATE", detail.Path);
        Assert.Equal("APRSdroid", detail.Device?.Model);
        Assert.Equal("N2DIGI", Assert.Single(detail.Arrival!.Digipeaters));
        Assert.Equal(ArrivalKind.Digipeated, detail.Arrival?.Kind);
        Assert.StartsWith("N0DETAIL>APDR16", detail.LastPacket, StringComparison.Ordinal);
        Assert.Null(await remote.GetStationAsync("N0NOBODY", Ct));
    }

    [Fact]
    public async Task RemoteClientGetsTheStationSnapshotThenChanges()
    {
        const string name = "N0MAP"; // not used by other tests: the fixture's engine is shared
        var engine = factory.Services.GetRequiredService<Engine>();
        engine.Accept($"{name}>APRS:!4903.50N/07201.75W>");
        await using var remote = CreateRemote();
        await remote.StartAsync(Ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var watch = remote.WatchStationsAsync(cancellationToken: cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await watch.MoveNextAsync());
        var station = Assert.Single(watch.Current.Changed, s => s.Name == name);
        Assert.Equal("/>", station.Symbol);
        Assert.Equal(49.0583, station.Latitude, 4);

        engine.Accept($"{name}>APRS:!4904.00N/07202.00W-");
        do
        {
            Assert.True(await watch.MoveNextAsync());
        } while (!watch.Current.Changed.Any(s => s.Name == name));
        var moved = watch.Current.Changed.Single(s => s.Name == name);
        Assert.Equal("/-", moved.Symbol);
        Assert.Equal(49.0667, moved.Latitude, 4);
        await cts.CancelAsync();
    }

    private RemoteEngineService CreateRemote() => factory.CreateRemote();
}
