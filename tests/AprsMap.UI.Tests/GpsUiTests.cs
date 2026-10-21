// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Map;
using AprsMap.UI.Settings;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>A GPS in Settings › Sources, its pill and the map (docs/features/F3.6-gps.md).</summary>
public sealed class GpsUiTests
{
    private static (SourcesSectionViewModel Section, FakeEngine Engine) Create()
    {
        var engine = new FakeEngine();
        var section = new SourcesSectionViewModel(engine, Localizer.Instance, _ => { });
        section.Show(engine.Settings);
        return (section, engine);
    }

    [Fact]
    public async Task AGpsOnASerialPortIsSavedWithItsSpeedAndNeverSends()
    {
        var (section, engine) = Create();
        section.AddCommand.Execute(null);
        section.EditKind = SourceKind.Gps;

        Assert.True(section.IsGps && section.GpsSerial && section.IsSerial);
        Assert.False(section.ShowsTncSerialParts); // no control lines for a GPS
        Assert.Equal("4800", section.BaudPlaceholder);
        section.EditSerialPort = "/dev/ttyACM0";
        await section.SaveCommand.ExecuteAsync(null);

        var gps = engine.Settings.SourceList.Single(s => s.Kind == SourceKind.Gps);
        Assert.Equal(
            (GpsLink.Serial, "/dev/ttyACM0", (int?)null, true),
            (gps.GpsLink, gps.SerialPort, gps.Baud, gps.RxOnly)
        );
        Assert.Contains(gps.Id, engine.Settings.GetActiveProfile()!.SourceIds); // the active profile uses it
    }

    [Theory]
    [InlineData(GpsLink.Tcp, "10110")]
    [InlineData(GpsLink.Gpsd, "2947")]
    public async Task AGpsOverTheNetworkHasAHostAndItsLinksPort(GpsLink link, string port)
    {
        var (section, engine) = Create();
        section.AddCommand.Execute(null);
        section.EditKind = SourceKind.Gps;
        if (link == GpsLink.Tcp)
        {
            section.GpsTcp = true;
        }
        else
        {
            section.GpsGpsd = true;
        }

        Assert.True(section.ShowsHost);
        Assert.False(section.IsSerial);
        Assert.Equal(port, section.PortPlaceholder);
        section.EditHost = "pi.local";
        await section.SaveCommand.ExecuteAsync(null);

        var gps = engine.Settings.SourceList.Single(s => s.Kind == SourceKind.Gps);
        Assert.Equal((link, "pi.local", (int?)null, (string?)null), (gps.GpsLink, gps.Host, gps.Port, gps.SerialPort));
    }

    [Theory]
    [InlineData(GpsFixKind.Fix3D, 9, true, "GPS · 3D fix · 9 satellites", false)]
    [InlineData(GpsFixKind.Fix2D, null, true, "GPS · 2D fix", false)]
    [InlineData(GpsFixKind.Fix3D, 9, false, "GPS · no fix, using the profile's location", true)]
    public void ItsPillSaysWhatTheGpsKnows(GpsFixKind fix, int? satellites, bool fresh, string said, bool warned)
    {
        var pill = new SourcePill(
            "gps",
            Localizer.Instance,
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            _ => Task.CompletedTask
        );
        var gps = new GpsStatus("gps", fix, DateTimeOffset.UnixEpoch, fresh, 49, -72, Satellites: satellites);

        pill.Show(
            new SourceStatus("gps", "GPS", SourceKind.Gps, ConnectionState.Connected),
            true,
            gps: gps,
            hasFixedLocation: true
        );

        Assert.Equal(said, pill.Text);
        Assert.Equal(warned, pill.IsWarning);
    }

    [Fact]
    public void TheHouseIsWhereAFreshFixSaysAndBackHomeWithoutOne()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider())
        {
            Map = new MapViewModel(null, null, new FakeTimeProvider()),
        };
        var active = engine.Settings.GetActiveProfile()!;
        vm.Show(
            engine.Settings with
            {
                Profiles =
                [
                    .. engine.Settings.Profiles.Select(p =>
                        p.Id == active.Id ? p with { Latitude = 40, Longitude = -105 } : p
                    ),
                ],
            }
        );
        Assert.Equal((40.0, -105.0), vm.Map!.Home);

        var status = new EngineStatus(ConnectionState.Connected, "T2TEST", 1, "1.0");
        vm.Show(
            status with
            {
                Gps = new GpsStatus("gps", GpsFixKind.Fix3D, DateTimeOffset.UnixEpoch, true, 49.06, -72.03),
            }
        );
        Assert.Equal((49.06, -72.03), vm.Map.Home);

        vm.Show(
            status with
            {
                Gps = new GpsStatus("gps", GpsFixKind.Fix3D, DateTimeOffset.UnixEpoch, false, 49.06, -72.03),
            }
        );
        Assert.Equal((40.0, -105.0), vm.Map.Home); // stale: the profile's location
    }
}
