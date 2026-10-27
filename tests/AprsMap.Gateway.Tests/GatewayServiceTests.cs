// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using Microsoft.Extensions.Configuration;

namespace AprsMap.Gateway.Tests;

/// <summary>The gateway as a Linux service (docs/features/F1.37-gateway-service.md, acceptance tests 1, 2).</summary>
public sealed class GatewayServiceTests
{
    private const string Sample = """
        [gateway]
        listen = "0.0.0.0:8080"
        operator_listen = "127.0.0.1:8081"
        home_privacy = "Grid"
        allowed_origins = ["https://a.example", "https://b.example"]

        [engine]
        settings_folder = "/var/lib/aprsmap"
        callsign = "N0CALL-10"
        passcode = -1
        receive_only = false

        [engine.kiss_serial]
        port = "/dev/ttyUSB0"
        baud = 9600
        """;

    [Fact]
    public void TablesAndSnakeCaseKeysAreTheGatewaysSettings()
    {
        var settings = TomlConfiguration.Read(Sample, "gateway.toml");

        Assert.Equal("127.0.0.1:8081", settings["Gateway:OperatorListen"]);
        Assert.Equal("Grid", settings["gateway:homeprivacy"]); // configuration keys ignore case
        Assert.Equal("https://b.example", settings["Gateway:AllowedOrigins:1"]);
        Assert.Equal("-1", settings["Engine:Passcode"]);
        Assert.Equal("false", settings["Engine:ReceiveOnly"]);
        Assert.Equal("/dev/ttyUSB0", settings["Engine:KissSerial:Port"]);
        Assert.Equal("9600", settings["Engine:KissSerial:Baud"]);
    }

    [Fact]
    public void TheFileBindsAsOptionsAndAnEnvironmentVariableWins()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(TomlConfiguration.Read(Sample, "gateway.toml"))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Engine:Callsign"] = "N0ENV-10" }) // as env would
            .Build();

        var engine = configuration.GetSection("Engine").Get<AprsMap.Core.EngineOptions>()!;
        Assert.Equal(
            ("N0ENV-10", -1, "/dev/ttyUSB0", 9600),
            (engine.Callsign, engine.Passcode, engine.KissSerial.Port, engine.KissSerial.Baud)
        );
    }

    [Fact]
    public void TheFileGoesAfterAppsettingsAndBeforeTheEnvironment()
    {
        // As WebApplication.CreateBuilder orders them: prefixed variables, appsettings.json, then the environment.
        var directory = Directory.CreateTempSubdirectory("aprsmap-toml-");
        try
        {
            File.WriteAllText(
                Path.Combine(directory.FullName, "appsettings.json"),
                """{ "Engine": { "Callsign": "", "Server": "x" } }"""
            );
            var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Engine:Filter"] = "prefixed" });
            configuration.SetBasePath(directory.FullName).AddJsonFile("appsettings.json");
            configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Engine:Server"] = "from-env" });

            TomlConfiguration.InsertAfterJsonFiles(
                ((IConfigurationBuilder)configuration).Sources,
                TomlConfiguration.Read("[engine]\ncallsign = \"N0TOML-10\"\nserver = \"from-toml\"\n", "g.toml")
            );

            Assert.Equal("N0TOML-10", configuration["Engine:Callsign"]); // over appsettings.json's empty one
            Assert.Equal("from-env", configuration["Engine:Server"]); // the environment still wins
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void AFileThatIsntTomlStopsTheGatewayWithItsLine()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            TomlConfiguration.Read("[gateway]\nlisten = \"0.0.0.0:8080\nnext = 1\n", "/etc/aprsmap/gateway.toml")
        );
        Assert.Contains("/etc/aprsmap/gateway.toml", error.Message, StringComparison.Ordinal);
        Assert.Contains("2", error.Message, StringComparison.Ordinal); // the line
    }

    [Fact]
    public void TheConfigOptionIsFoundAndLeftOutOfAnOperatorCommand()
    {
        Assert.Equal("/etc/a.toml", TomlConfiguration.PathFrom(["--config", "/etc/a.toml"], _ => null));
        Assert.Equal("/etc/b.toml", TomlConfiguration.PathFrom(["--config=/etc/b.toml"], _ => null));
        Assert.Equal("/etc/c.toml", TomlConfiguration.PathFrom([], v => v == "APRSMAP_CONFIG" ? "/etc/c.toml" : null));
        Assert.Null(TomlConfiguration.PathFrom([], _ => null));
        Assert.Equal(["add", "alice"], TomlConfiguration.WithoutConfig(["add", "alice", "--config", "/etc/a.toml"]));
    }

    [Fact]
    public void TheAdminAppListensOnItsOwnAddressAndPort()
    {
        var both = GatewayListen.Urls(Configuration(("Gateway:OperatorListen", "127.0.0.1:8081")))!.Value;
        Assert.Equal(["http://0.0.0.0:8080", "http://127.0.0.1:8081"], both.Urls);
        Assert.Equal(8081, both.OperatorPort);

        Assert.Null(GatewayListen.Urls(Configuration()));
        Assert.Throws<InvalidOperationException>(() => GatewayListen.Urls(Configuration(("Gateway:Listen", "8080"))));
        Assert.Throws<InvalidOperationException>(() =>
            GatewayListen.Urls(
                Configuration(("Gateway:Listen", "0.0.0.0:8080"), ("Gateway:OperatorListen", "127.0.0.1:8080"))
            )
        );
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    [Fact]
    public void FirstRunStationValuesTheFileSetsThatDifferAreFoundAndOthersAreNot()
    {
        // #429: on 10.10.20.191, the stored station is NZ0P-4 on noam with passcode 3560.
        var rotate = new SourceSettings("is", "noam", SourceKind.AprsIs, "noam.aprs2.net:14580", "r/39/-94.7/25");
        var profile = new StationProfile("p", "NZ0P-4", "nz0p-4", 3560, ["is"], null, null);
        var settings = new AppSettings([profile], "p", UnitSettings.Metric, Sources: [rotate]);
        var file = TomlConfiguration.Read(
            "[engine]\ncallsign = \"NZ0P-5\"\npasscode = 1234\nserver = \"noam.aprs2.net:14580\"\n",
            "g.toml"
        );
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Engine:Filter"] = "r/1/1/1" }) // not in the file
            .AddInMemoryCollection(file)
            .Build();

        var differences = TomlStationCheck
            .Differences(new HashSet<string>(file.Keys, StringComparer.OrdinalIgnoreCase), configuration, settings)
            .ToList();

        Assert.Equal(
            [("Engine:Callsign", "NZ0P-5", "nz0p-4"), ("Engine:Passcode", null, null)],
            differences.Select(d => (d.Key, d.Configured, d.Stored))
        ); // the same server and a filter only from elsewhere aren't; the passcode's values never come out
        Assert.True(TomlStationCheck.Applies(new HashSet<string> { "Engine:Callsign" }));
        Assert.False(TomlStationCheck.Applies(new HashSet<string> { "Gateway:Listen", "Engine:KissSerial:Baud" }));
    }
}
