// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>The devices the gateway's configuration names win on every start, not only the first (#424).</summary>
public sealed class ConfiguredSourcesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Tnc = "/dev/serial/by-id/usb-FTDI_FT232R_USB_UART_AH033ET9-if00-port0";

    /// <summary>A station as the first run on 10.10.20.191 left it: the USB TNC at 9600, with a random id, and gpsd.</summary>
    private static InMemorySettingsStore Installed()
    {
        var tnc = SourceSettings.KissSerialFor(Tnc, 9600) with { Name = "USB TNC-X" };
        var profile = InMemorySettingsStore.Profile("NZ0P-4") with
        {
            SourceIds = [InMemorySettingsStore.Rotate.Id, tnc.Id],
        };
        return new InMemorySettingsStore(
            new AppSettings([profile], profile.Id, UnitSettings.Metric, Sources: [InMemorySettingsStore.Rotate, tnc])
        );
    }

    private static Engine Create(InMemorySettingsStore store, Action<EngineOptions> configure)
    {
        var options = new EngineOptions();
        configure(options);
        return new Engine(
            Options.Create(options),
            new EngineTests.FakeSourceFactory(),
            store,
            new FakeTimeProvider(),
            NullLogger<Engine>.Instance
        );
    }

    private static SourceSettings TncOf(AppSettings settings) =>
        Assert.Single(settings.SourceList, s => s.Kind == SourceKind.KissSerial);

    [Fact]
    public async Task AFixedBaudInTheConfigurationTakesEffectAndTheSourceKeepsItsId()
    {
        var store = Installed();
        var id = TncOf(store.Stored!).Id;
        using var engine = Create(
            store,
            o =>
            {
                o.KissSerial.Port = Tnc;
                o.KissSerial.Baud = 1200;
                o.KissSerial.Name = "USB TNC-X";
            }
        );

        var tnc = TncOf(await engine.GetSettingsAsync(Ct));

        Assert.Equal((id, 1200, true), (tnc.Id, tnc.Baud, tnc.Configured)); // adopted: profiles and history carry on
        Assert.Equal(1200, TncOf(store.Stored!).Baud); // and stored
        Assert.Equal(1, store.Saves);
    }

    [Fact]
    public async Task AMovedTncAndANewNameFollowTheConfiguration()
    {
        var store = Installed();
        using var engine = Create(
            store,
            o =>
            {
                o.KissSerial.Port = "/dev/ttyUSB1";
                o.KissSerial.Name = "V71A";
            }
        );

        var tnc = TncOf(await engine.GetSettingsAsync(Ct));

        Assert.Equal(("/dev/ttyUSB1", "V71A", 9600), (tnc.SerialPort, tnc.Name, tnc.Baud)); // unset baud kept
    }

    [Fact]
    public async Task SavingFromTheAppKeepsTheConfiguredConnectionButTheRestChanges()
    {
        var store = Installed();
        using var engine = Create(
            store,
            o =>
            {
                o.KissSerial.Port = Tnc;
                o.KissSerial.Baud = 1200;
            }
        );
        var settings = await engine.GetSettingsAsync(Ct);
        var tnc = TncOf(settings);

        await engine.SaveSettingsAsync(
            settings with
            {
                Sources =
                [
                    .. settings.SourceList.Select(s => s.Id == tnc.Id ? s with { Baud = 9600, TxPath = "WIDE1-1" } : s),
                ],
            },
            Ct
        );

        var saved = TncOf(store.Stored!);
        Assert.Equal((1200, "WIDE1-1"), (saved.Baud, saved.TxPath));
    }

    [Fact]
    public async Task ANewlyConfiguredGpsdIsAddedToTheActiveProfile()
    {
        var store = Installed();
        using var engine = Create(store, o => o.Gpsd.Host = "localhost");

        var settings = await engine.GetSettingsAsync(Ct);

        var gpsd = Assert.Single(settings.SourceList, s => s.Kind == SourceKind.Gps);
        Assert.Equal(
            (Engine.ConfiguredGpsdId, "localhost", GpsLink.Gpsd, true),
            (gpsd.Id, gpsd.Host, gpsd.GpsLink, gpsd.Configured)
        );
        Assert.Contains(gpsd.Id, settings.GetActiveProfile()!.SourceIds);
    }

    [Fact]
    public async Task AFirstRunGivesTheConfiguredDevicesTheirStableIds()
    {
        var store = new InMemorySettingsStore();
        using var engine = Create(
            store,
            o =>
            {
                o.Callsign = "NZ0P-4";
                o.Server = "";
                o.KissSerial.Port = Tnc;
                o.Gpsd.Host = "localhost";
            }
        );

        var settings = await engine.GetSettingsAsync(Ct);

        Assert.Equal([Engine.ConfiguredKissSerialId, Engine.ConfiguredGpsdId], settings.SourceList.Select(s => s.Id));
        Assert.All(settings.SourceList, s => Assert.True(s.Configured));
    }

    [Fact]
    public async Task WithNothingConfiguredNothingIsTouched()
    {
        var store = Installed();
        using var engine = Create(store, _ => { });

        var tnc = TncOf(await engine.GetSettingsAsync(Ct));

        Assert.Equal((9600, false), (tnc.Baud, tnc.Configured));
        Assert.Equal(0, store.Saves);
    }
}
