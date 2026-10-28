// SPDX-License-Identifier: AGPL-3.0-only
using System.Diagnostics;
using AprsMap.Contracts;
using AprsMap.Core;
using AprsMap.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using OpenTelemetry.Exporter;

namespace AprsMap.Gateway.Tests;

/// <summary>The log file, the level rules and OTLP targets (docs/features/F1.32-logging.md).</summary>
public sealed class LoggingTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Never the real user folders: every file and setting here is in a temporary folder.
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "aprsmap-logging-" + Guid.NewGuid().ToString("N")
    );

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private LoggingState State(LoggingSettings? settings = null, bool on = true)
    {
        var state = new LoggingState { DefaultFolder = Path.Combine(_folder, "logs"), FileOnByDefault = on };
        state.Update(settings ?? LoggingSettings.Default);
        return state;
    }

    private static void Write(ILogger logger, LogLevel level, string message, Exception? exception = null) =>
        logger.Log(level, default, message, exception, (text, _) => text);

    private static string[] Lines(string path)
    {
        // Serilog keeps the file open; read it shared.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries); // CRLF on Windows
    }

    [Fact]
    public void ALineHasTheUtcTimeLevelCategoryMessageAndTheTraceWhenThereIsOne()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-19T13:45:07.123Z", null));
        using var provider = new FileLoggerProvider(State(), time);
        var logger = provider.CreateLogger("AprsMap.Connections");

        Write(logger, LogLevel.Information, "Connected to T2TEST");
        using (var activity = new Activity("test").SetIdFormat(ActivityIdFormat.W3C).Start())
        {
            Write(logger, LogLevel.Warning, "Lost it", new IOException("cable"));
        }
        provider.Dispose(); // flushes

        var path = Path.Combine(_folder, "logs", "aprsmap-2026-10-19.log");
        var lines = Lines(path);
        Assert.Equal("2026-10-19T13:45:07.123Z INF AprsMap.Connections Connected to T2TEST", lines[0]);
        Assert.Matches(
            "^2026-10-19T13:45:07.123Z WRN AprsMap.Connections Lost it trace=[0-9a-f]{32} span=[0-9a-f]{16}$",
            lines[1]
        );
        Assert.StartsWith("System.IO.IOException: cable", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void ANewUtcDayStartsANewFileAndOffWritesNothing()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-19T23:59:00Z", null));
        var state = State();
        using var provider = new FileLoggerProvider(state, time);
        var logger = provider.CreateLogger("Test");
        Write(logger, LogLevel.Information, "before midnight");

        time.Advance(TimeSpan.FromMinutes(2)); // past midnight UTC
        Write(logger, LogLevel.Information, "after midnight");
        Assert.EndsWith("aprsmap-2026-10-20.log", provider.CurrentFile, StringComparison.Ordinal);

        state.Update(LoggingSettings.Default with { File = new LogFileSettings(On: false) });
        Assert.Null(provider.CurrentFile);
        Assert.False(logger.IsEnabled(LogLevel.Error));
        provider.Dispose();

        Assert.Contains("before midnight", Lines(Path.Combine(_folder, "logs", "aprsmap-2026-10-19.log"))[0]);
        Assert.Contains("after midnight", Lines(Path.Combine(_folder, "logs", "aprsmap-2026-10-20.log"))[0]);
    }

    [Fact]
    public void OldFilesGoByDaysThenByTheTotalAndOthersAreLeftAlone()
    {
        Directory.CreateDirectory(_folder);
        void Make(string name, int kilobytes) =>
            File.WriteAllBytes(Path.Combine(_folder, name), new byte[kilobytes * 1024]);
        Make("aprsmap-2026-09-01.log", 1); // 48 days old
        Make("aprsmap-2026-10-10.log", 600);
        Make("aprsmap-2026-10-10_001.log", 600);
        Make("aprsmap-2026-10-18.log", 600);
        Make("aprsmap-2026-10-19.log", 10); // today's
        Make("notes.log", 1); // not ours
        Make("aprsmap-backup.log", 1); // not a day

        var settings = new LogFileSettings(KeepDays: 14, TotalMegabytes: 1); // 1 MB in all, for the test
        FileLoggerProvider.Clean(
            _folder,
            settings,
            DateTimeOffset.Parse("2026-10-19T12:00:00Z", null),
            Path.Combine(_folder, "aprsmap-2026-10-19.log")
        );

        // Both sides sorted: with another product name (tools/rename-product.py), its files sort elsewhere.
        string[] kept = ["aprsmap-2026-10-18.log", "aprsmap-2026-10-19.log", "aprsmap-backup.log", "notes.log"];
        Assert.Equal(
            kept.Order(StringComparer.Ordinal),
            Directory.GetFiles(_folder).Select(Path.GetFileName).Order(StringComparer.Ordinal)
        );
    }

    [Theory]
    [InlineData("aprsmap-2026-10-19.log", true)]
    [InlineData("aprsmap-2026-10-19_003.log", true)]
    [InlineData("aprsmap-2026-13-19.log", false)]
    [InlineData("aprsmap-2026-10-19.txt", false)]
    [InlineData("other-2026-10-19.log", false)]
    public void ADayIsReadOnlyFromThisAppsFileNames(string name, bool ours) =>
        Assert.Equal(ours, FileLoggerProvider.DayOf(name) is not null);

    private IHost Host(LoggingSettings logging, params string[] commandLine)
    {
        var settings = Path.Combine(_folder, "settings");
        new FileSettingsStore(settings)
            .SaveAsync(AppSettings.Empty with { Logging = logging }, Ct)
            .GetAwaiter()
            .GetResult();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { DisableDefaults = true }
        );
        builder.Configuration["Engine:SettingsFolder"] = settings;
        builder.Configuration.AddCommandLine(commandLine);
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging")); // as a real host does, first
        builder.AddAprsMapTelemetry("test", logFile: false);
        // A logger is enabled only where a provider listens: one that always does, so the level rules decide.
        builder.Services.AddSingleton<ILoggerProvider>(new Listening());
        return builder.Build();
    }

    private sealed class Listening : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) { }

        public void Dispose() { }
    }

    [Fact]
    public void TheLevelsApplyByAreaAndChangeWithoutARestart()
    {
        using var host = Host(LoggingSettings.Default);
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        var tiles = factory.CreateLogger("AprsMap.Tiles.TileService");
        var tileRequests = factory.CreateLogger("System.Net.Http.HttpClient.AprsMap.Tiles.LogicalHandler");
        var connections = factory.CreateLogger(LogAreas.Connections);

        Assert.False(tiles.IsEnabled(LogLevel.Information)); // Map tiles at Warnings (decision 7)
        Assert.False(tileRequests.IsEnabled(LogLevel.Information));
        Assert.True(tiles.IsEnabled(LogLevel.Warning));
        Assert.True(connections.IsEnabled(LogLevel.Information));
        Assert.False(connections.IsEnabled(LogLevel.Debug));

        host.Services.GetRequiredService<LoggingState>()
            .Update(
                LoggingSettings.Default with
                {
                    Areas = [new LogAreaLevel(LogArea.Connections, LogLevelSetting.Debug)],
                }
            );

        Assert.True(connections.IsEnabled(LogLevel.Debug));
        Assert.False(factory.CreateLogger(LogAreas.Messages).IsEnabled(LogLevel.Debug));
    }

    [Fact]
    public void TheEnvironmentOrCommandLineWinsAndIsSaidSo()
    {
        using var host = Host(
            LoggingSettings.Default with
            {
                Level = LogLevelSetting.Trace,
            },
            "--Logging:LogLevel:Default=Error",
            "--Logging:LogLevel:AprsMap.Tiles=Debug"
        );
        var state = host.Services.GetRequiredService<LoggingState>();
        var factory = host.Services.GetRequiredService<ILoggerFactory>();

        Assert.True(state.EnvironmentLevel);
        Assert.Equal([LogArea.Tiles], state.EnvironmentAreas);
        Assert.False(factory.CreateLogger(LogAreas.Connections).IsEnabled(LogLevel.Information));
        Assert.True(factory.CreateLogger("AprsMap.Tiles").IsEnabled(LogLevel.Debug));
    }

    [Fact]
    public void TheTargetsAreReadAsTheAppStartsAndAChangeWaitsForARestart()
    {
        var target = new OtlpTarget("t1", "Seq", true, "http://127.0.0.1:9");
        using var host = Host(LoggingSettings.Default with { Targets = [target] });
        var state = host.Services.GetRequiredService<LoggingState>();
        Assert.False(state.Status().RestartPending);

        state.Update(LoggingSettings.Default with { Targets = [target with { Endpoint = "http://127.0.0.1:10" }] });

        Assert.True(state.Status().RestartPending);
    }

    [Theory]
    [InlineData(OtlpProtocol.HttpProtobuf, "http://seq.local:5341", "http://seq.local:5341/v1/logs")]
    [InlineData(
        OtlpProtocol.HttpProtobuf,
        "http://seq.local:5341/ingest/otlp/v1/logs",
        "http://seq.local:5341/ingest/otlp/v1/logs"
    )]
    [InlineData(OtlpProtocol.Grpc, "http://localhost:4317", "http://localhost:4317/")]
    public void ATargetsAddressProtocolAndHeadersAreGivenToItsExporter(
        OtlpProtocol protocol,
        string endpoint,
        string expected
    )
    {
        var options = new OtlpExporterOptions();
        var target = new OtlpTarget(
            "t",
            "Seq",
            true,
            endpoint,
            protocol,
            Headers: [new OtlpHeader("X-Seq-ApiKey", "a b,c")]
        );

        HostingExtensions.Apply(options, target, "logs");

        Assert.Equal(new Uri(expected), options.Endpoint);
        Assert.Equal(
            protocol == OtlpProtocol.Grpc ? OtlpExportProtocol.Grpc : OtlpExportProtocol.HttpProtobuf,
            options.Protocol
        );
        Assert.Equal("X-Seq-ApiKey=a%20b%2Cc", options.Headers);
    }
}

/// <summary>On a gateway, logging is the signed-in operator's (F1.32 behaviors 1, 13.5).</summary>
public sealed class GatewayLoggingTests(OperatorGatewayFactory gateway) : IClassFixture<OperatorGatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OnlyTheOperatorSeesLoggingAndAHeadersValueNeverLeavesTheEngine()
    {
        var engine = gateway.Services.GetRequiredService<Engine>();
        var stored = await engine.GetSettingsAsync(Ct);
        await engine.SaveSettingsAsync(
            stored with
            {
                Logging = LoggingSettings.Default with
                {
                    Targets =
                    [
                        new OtlpTarget("t", "Seq", true, "http://seq.local:5341", Headers: [new("X-Key", "secret")]),
                    ],
                },
            },
            Ct
        );

        await using var viewer = gateway.CreateRemote();
        await viewer.StartAsync(Ct);
        Assert.Null((await viewer.GetSettingsAsync(Ct)).Logging);
        var refused = await Assert.ThrowsAsync<EngineAccessException>(() => viewer.GetLoggingStatusAsync(Ct));
        Assert.Equal(EngineErrorCodes.OperatorOnly, refused.EngineError.Code);

        await using var signedIn = gateway.CreateRemote(await gateway.SignInAsync(Ct));
        await signedIn.StartAsync(Ct);
        var header = Assert.Single((await signedIn.GetSettingsAsync(Ct)).EffectiveLogging.TargetList[0].HeaderList);
        Assert.Equal(("X-Key", null, true), (header.Name, header.Value, header.HasValue));
        var status = await signedIn.GetLoggingStatusAsync(Ct);
        Assert.False(status.FileOnByDefault); // a gateway's log is the container's output
        Assert.StartsWith(gateway.SettingsFolder, status.DefaultFolder, StringComparison.Ordinal); // never the user's

        // Saved back as handed out, the stored value is kept.
        await signedIn.SaveSettingsAsync(await signedIn.GetSettingsAsync(Ct), Ct);
        var kept = (await engine.GetSettingsAsync(Ct)).EffectiveLogging.TargetList[0].HeaderList[0];
        Assert.True(kept.HasValue);
        var file = await File.ReadAllTextAsync(Path.Combine(gateway.SettingsFolder, "settings.json"), Ct);
        Assert.Contains("secret", file, StringComparison.Ordinal);
    }
}

/// <summary>On a gateway, the server's folders are the signed-in operator's (F1.31 behavior 6).</summary>
public sealed class GatewayFolderTests(OperatorGatewayFactory gateway) : IClassFixture<OperatorGatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OnlyASignedInOperatorGetsTheServersFolders()
    {
        await using var viewer = gateway.CreateRemote();
        await viewer.StartAsync(Ct);
        var refused = await Assert.ThrowsAsync<EngineAccessException>(() => viewer.GetStorageFoldersAsync(Ct));
        Assert.Equal(EngineErrorCodes.OperatorOnly, refused.EngineError.Code);

        await using var signedIn = gateway.CreateRemote(await gateway.SignInAsync(Ct));
        await signedIn.StartAsync(Ct);
        var folders = await signedIn.GetStorageFoldersAsync(Ct);

        Assert.Contains(folders, f => f.Kind == StorageFolderKind.Settings && f.Path == gateway.SettingsFolder);
        Assert.Contains(folders, f => f.Kind == StorageFolderKind.Tiles);
        Assert.DoesNotContain(folders, f => f.Kind == StorageFolderKind.Logs); // a gateway's log file is off
    }
}
