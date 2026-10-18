// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsNet.Packets;

namespace AprsMap.Gateway.Tests;

/// <summary>What viewers learn of the station's location (docs/features/F1.26-public-console.md, behavior 11).</summary>
public sealed class HomePrivacyTests(OperatorPortGatewayFactory gateway) : IClassFixture<OperatorPortGatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly AprsPosition Home = new(49.0583, -72.0292);
    private static readonly AprsPosition Square = MaidenheadLocator.FromPosition(Home, 4).Center;

    private static HomeShield Grid() => new(HomePrivacy.Grid, Home, "N0GATE");

    private static HomeShield Hidden() => new(HomePrivacy.Hidden, Home, "N0GATE");

    [Fact]
    public void UnderGridEverythingIsMeasuredFromTheSquaresCentre()
    {
        var shield = Grid();
        Assert.Equal(Square, shield.Shown);
        Assert.True(Geodesy.DistanceKm(Home, Square) > 1); // not the house

        var station = shield.Apply(
            new StationState("N0CAR-9", default, 49.5, -72.0, "/>", DistanceKm: 49.3, BearingDegrees: 2)
        );
        Assert.Equal(Geodesy.DistanceKm(Square, new AprsPosition(49.5, -72.0)), station.DistanceKm);

        var route = shield.Apply(
            new PacketRoute(
                "N0CAR-9",
                ArrivalKind.Direct,
                49.5,
                -72.0,
                [new RouteHop("N0GATE", true, Home.Latitude, Home.Longitude, 49.3)],
                HeardHere: true
            )
        );
        Assert.Equal(
            (Square.Latitude, Square.Longitude),
            (route.Hops[0].Latitude!.Value, route.Hops[0].Longitude!.Value)
        );

        var areas = shield.Apply([new FilterArea(Home.Latitude, Home.Longitude, 150), new FilterArea(40, -105, 50)]);
        Assert.Equal(
            [(Square.Latitude, Square.Longitude), (40.0, -105.0)],
            areas!.Select(a => (a.Latitude, a.Longitude))
        );

        var settings = shield.Apply(
            new AppSettings(
                [new StationProfile("h", "Home", "N0GATE", -1, [], Home.Latitude, Home.Longitude)],
                "h",
                UnitSettings.Metric
            )
        );
        Assert.Equal(Square.Latitude, settings.Profiles[0].Latitude);

        var detail = shield.Apply(
            new StationDetail(
                "N0GATE",
                StationKind.Station,
                default,
                0,
                false,
                null,
                null,
                0,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                Me: new ThisStation([new HeardStation("N0CAR-9", 49.3, default)], [])
            )
        );
        Assert.Null(detail.Me!.HeardDirectly[0].DistanceKm); // no measuring back to the house
    }

    [Fact]
    public void UnderHiddenThereIsNoLocationAtAll()
    {
        var shield = Hidden();
        Assert.Null(shield.Shown);

        Assert.Null(
            shield
                .Apply(new StationState("N0CAR-9", default, 49.5, -72.0, "/>", DistanceKm: 49.3, BearingDegrees: 2))
                .DistanceKm
        );
        Assert.Empty(shield.Apply([new FilterArea(Home.Latitude, Home.Longitude, 150)])!);
        Assert.Empty(
            shield.RoutesOut([new PacketRoute("N0GATE", ArrivalKind.Digipeated, Home.Latitude, Home.Longitude, [])])
        );
        Assert.Empty(
            shield.Apply([new NetworkLink("N0CAR-9", 49.5, -72.0, "N0GATE", Home.Latitude, Home.Longitude, 3)])
        );
        Assert.Equal(DestinationInfo.Unknown, shield.Destination(40, -105, 64));
        Assert.Null(
            shield
                .Apply(
                    new AppSettings(
                        [new StationProfile("h", "Home", "N0GATE", -1, [], Home.Latitude, Home.Longitude)],
                        "h",
                        UnitSettings.Metric
                    )
                )
                .Profiles[0]
                .Latitude
        );
        var route = shield.Apply(
            new PacketRoute(
                "N0CAR-9",
                ArrivalKind.Direct,
                49.5,
                -72.0,
                [new RouteHop("N0GATE", true, Home.Latitude, Home.Longitude, 49.3)],
                HeardHere: true
            )
        );
        Assert.Null(route.Hops[0].Latitude);
    }

    [Fact]
    public async Task WithAnOperatorPortAViewerGetsTheSquareAndTheOperatorTheHouse()
    {
        // The operator sets the station's location on the operator port.
        var cookie = await SignInOnOperatorPort();
        await using var operatorPort = gateway.CreateRemote(cookie, localPort: OperatorPortGatewayFactory.OperatorPort);
        await operatorPort.StartAsync(Ct);
        var settings = await operatorPort.GetSettingsAsync(Ct);
        var active = settings.GetActiveProfile()!;
        await operatorPort.SaveSettingsAsync(
            settings with
            {
                Profiles =
                [
                    .. settings.Profiles.Select(p =>
                        p.Id == active.Id ? p with { Latitude = Home.Latitude, Longitude = Home.Longitude } : p
                    ),
                ],
            },
            Ct
        );

        await using var viewer = gateway.CreateRemote();
        await viewer.StartAsync(Ct);

        Assert.Equal(Square.Latitude, (await viewer.GetSettingsAsync(Ct)).GetActiveProfile()!.Latitude); // Grid by default here
        Assert.Equal(Home.Latitude, (await operatorPort.GetSettingsAsync(Ct)).GetActiveProfile()!.Latitude);
        var toDenver = (await viewer.GetDestinationAsync(40, -105, Ct)).DistanceKm!.Value;
        Assert.Equal(Geodesy.DistanceKm(Square, new AprsPosition(40, -105)), toDenver, precision: 6);
    }

    private async Task<string> SignInOnOperatorPort()
    {
        using var browser = gateway.Browser();
        using var request = new HttpRequestMessage(HttpMethod.Post, AuthPaths.SignIn)
        {
            Content = System.Net.Http.Json.JsonContent.Create(
                new SignInRequest(OperatorGatewayFactory.Name, OperatorGatewayFactory.Password),
                EngineJsonContext.Default.SignInRequest
            ),
        };
        request.Headers.Add(
            LocalPortFilter.Header,
            OperatorPortGatewayFactory.OperatorPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        using var response = await browser.SendAsync(request, Ct);
        return response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }
}
