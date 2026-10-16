// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Packets;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

public class SourcesShownTests
{
    private static readonly SourceSettings Tnc = new("tnc", "Direwolf", SourceKind.KissTcp);

    private static AppSettings WithTnc() =>
        FakeEngine.TwoProfiles() with
        {
            Sources = [FakeEngine.Rotate, Tnc],
            Profiles =
            [
                FakeEngine.TwoProfiles().Profiles[0] with
                {
                    SourceIds = [FakeEngine.Rotate.Id, Tnc.Id],
                },
                FakeEngine.TwoProfiles().Profiles[1],
            ],
        };

    [Fact]
    public void WithOneSourceNothingAboutSourcesIsShown()
    {
        var names = new SourceNames(FakeEngine.TwoProfiles);
        var row = new PacketRow(
            FakeEngine.Packet("N0RF") with
            {
                SourceId = "rotate",
            },
            Localizer.Instance,
            TimeZoneInfo.Utc,
            names
        );

        Assert.False(names.AreShown);
        Assert.Null(row.SourceText);
    }

    [Fact]
    public void WithSeveralAPacketSaysWhereItArrivedAndWhereElseItWasSeen()
    {
        var names = new SourceNames(WithTnc);

        var row = new PacketRow(
            FakeEngine.Packet("N0RF") with
            {
                SourceId = "tnc",
                SeenOn = ["rotate"],
            },
            Localizer.Instance,
            TimeZoneInfo.Utc,
            names
        );

        Assert.Equal("Direwolf · also rotate.aprs2.net", row.SourceText);
    }

    [Fact]
    public async Task TheDetailsSayHowTheStationWasHeard()
    {
        var engine = new FakeEngine { Settings = WithTnc() };
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        engine.Details["N0RF"] = FakeEngine.Detail("N0RF", DateTimeOffset.UnixEpoch) with
        {
            FirstHeardVia = "tnc",
            SeenOn = ["rotate"],
        };

        await vm.Details.ShowAsync("N0RF");

        Assert.Equal("Direwolf · also rotate.aprs2.net", vm.Details.HeardViaText);
    }

    [Fact]
    public void EachSourceBesidesAprsIsHasAPill()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider());

        vm.Show(
            new EngineStatus(
                ConnectionState.Connected,
                "T2TEST",
                1,
                "1.0",
                Sources:
                [
                    new("rotate", "rotate.aprs2.net", SourceKind.AprsIs, ConnectionState.Connected, "T2TEST"),
                    new("tnc", "Direwolf", SourceKind.KissTcp, ConnectionState.Connected),
                    new(
                        "gps",
                        "Garmin",
                        SourceKind.Gps,
                        ConnectionState.NotConfigured,
                        Error: EngineErrors.Create(EngineErrorCodes.SourceKindNotBuilt, "x") is IHasEngineError e
                            ? e.EngineError
                            : null
                    ),
                ]
            )
        );

        Assert.Equal(["Direwolf connected", "Garmin: This kind of source isn't built yet."], vm.SourcePills);
    }
}
