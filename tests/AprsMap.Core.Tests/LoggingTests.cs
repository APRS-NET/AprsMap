// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Packets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>Logging settings in the engine (docs/features/F1.32-logging.md).</summary>
public sealed class LoggingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (Engine Engine, InMemorySettingsStore Store, LoggingState State) Create(AppSettings? settings = null)
    {
        var store = new InMemorySettingsStore(settings ?? AppSettings.Empty);
        var state = new LoggingState();
        var engine = new Engine(
            Options.Create(new EngineOptions()),
            new EngineTests.FakeSourceFactory(),
            store,
            new FakeTimeProvider(),
            NullLogger<Engine>.Instance,
            logging: state
        );
        return (engine, store, state);
    }

    [Theory]
    [InlineData("N0ABC>APRS::NZ0P-9   :hello world{01", false, "N0ABC>APRS::NZ0P-9   :(text not logged){01")]
    [InlineData("N0ABC>APRS::NZ0P-9   :hello world{01", true, "N0ABC>APRS::NZ0P-9   :hello world{01")]
    [InlineData("N0ABC>APRS::BLN1     :Net tonight at 8", false, "N0ABC>APRS::BLN1     :(text not logged)")]
    [InlineData("N0ABC>APRS::NZ0P-9   :ack01", false, "N0ABC>APRS::NZ0P-9   :ack01")]
    [InlineData("N0ABC>APRS:!4903.50N/07201.75W-Home", false, "N0ABC>APRS:!4903.50N/07201.75W-Home")]
    public void AMessagesTextIsLeftOutOfTheLogUnlessAskedFor(string raw, bool texts, string logged) =>
        Assert.Equal(logged, LogAreas.Packet(AprsPacket.Parse(raw), texts));

    [Fact]
    public void MapTilesAreAtWarningsByDefaultAndTheRestFollowTheOverallLevel()
    {
        var logging = LoggingSettings.Default with { Level = LogLevelSetting.Debug };

        Assert.Equal(LogLevelSetting.Warnings, logging.LevelOf(LogArea.Tiles));
        Assert.Equal(LogLevelSetting.Debug, logging.LevelOf(LogArea.Connections));
        Assert.Equal(
            LogLevelSetting.Debug,
            (logging with { Areas = [new LogAreaLevel(LogArea.Tiles, null)] }).LevelOf(LogArea.Tiles)
        ); // "Same as overall"
    }

    [Fact]
    public async Task AHeadersValueIsNeverHandedOutAndASaveWithoutOneKeepsIt()
    {
        var target = new OtlpTarget(
            "t",
            "Seq",
            true,
            "http://seq.local:5341",
            Headers: [new OtlpHeader("X-Key", "secret")]
        );
        var (engine, store, state) = Create(
            AppSettings.Empty with
            {
                Logging = new LoggingSettings(Targets: [target]),
            }
        );

        var shown = await engine.GetSettingsAsync(Ct);
        var header = Assert.Single(shown.EffectiveLogging.TargetList[0].HeaderList);
        Assert.Equal((null, true), (header.Value, header.HasValue));

        await engine.SaveSettingsAsync(shown with { Logging = shown.EffectiveLogging with { Packets = true } }, Ct);

        Assert.Equal("secret", store.Stored!.EffectiveLogging.TargetList[0].HeaderList[0].Value);
        Assert.True(state.Current.Packets); // applied at once (behavior 2)
        Assert.Equal("secret", state.Current.TargetList[0].HeaderList[0].Value);

        await engine.SaveSettingsAsync(
            shown with
            {
                Logging = shown.EffectiveLogging with
                {
                    Targets = [target with { Headers = [new OtlpHeader("X-Key", "replaced")] }],
                },
            },
            Ct
        );
        Assert.Equal("replaced", store.Stored!.EffectiveLogging.TargetList[0].HeaderList[0].Value);
    }

    [Theory]
    [InlineData(0, 14, 100)]
    [InlineData(10, 0, 100)]
    [InlineData(10, 14, 5)]
    [InlineData(101, 14, 100)]
    public async Task ALimitOutOfRangeIsRefused(int megabytes, int days, int total)
    {
        var (engine, _, _) = Create();
        var settings = AppSettings.Empty with
        {
            Logging = new LoggingSettings(
                File: new LogFileSettings(MaxFileMegabytes: megabytes, KeepDays: days, TotalMegabytes: total)
            ),
        };

        var error = await Assert.ThrowsAsync<EngineArgumentException>(() => engine.SaveSettingsAsync(settings, Ct));

        Assert.Equal(EngineErrorCodes.LoggingLimitInvalid, error.EngineError.Code);
    }

    [Theory]
    [InlineData("", "http://localhost:4317", "X-Key")]
    [InlineData("Seq", "localhost:4317", "X-Key")]
    [InlineData("Seq", "ftp://localhost", "X-Key")]
    [InlineData("Seq", "http://localhost:4317", "X Key")]
    public async Task ATargetWithoutANameAnHttpAddressOrASendableHeaderIsRefused(
        string name,
        string endpoint,
        string header
    )
    {
        var (engine, _, _) = Create();
        var settings = AppSettings.Empty with
        {
            Logging = new LoggingSettings(
                Targets: [new OtlpTarget("t", name, true, endpoint, Headers: [new(header, "v")])]
            ),
        };

        var error = await Assert.ThrowsAsync<EngineArgumentException>(() => engine.SaveSettingsAsync(settings, Ct));

        Assert.Equal(EngineErrorCodes.LoggingTargetInvalid, error.EngineError.Code);
    }

    [Fact]
    public async Task AFolderThatCantBeWrittenIsRefused()
    {
        var file = Path.Combine(Path.GetTempPath(), "aprsmap-not-a-folder-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(file, "", Ct);
        try
        {
            var (engine, _, _) = Create();
            var settings = AppSettings.Empty with
            {
                Logging = new LoggingSettings(File: new LogFileSettings(Folder: Path.Combine(file, "logs"))),
            };

            var error = await Assert.ThrowsAsync<EngineArgumentException>(() => engine.SaveSettingsAsync(settings, Ct));

            Assert.Equal(EngineErrorCodes.LoggingFolderNotWritable, error.EngineError.Code);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void TheSameSettingsDontCountAsAChange()
    {
        var state = new LoggingState();
        var changes = 0;
        state.Changed += (_, _) => changes++;

        state.Update(new LoggingSettings(Areas: [new LogAreaLevel(LogArea.Connections, LogLevelSetting.Debug)]));
        state.Update(new LoggingSettings(Areas: [new LogAreaLevel(LogArea.Connections, LogLevelSetting.Debug)]));
        state.Update(new LoggingSettings(Packets: true));

        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task AReceivedMessageIsLoggedWithoutItsTextUnlessAskedFor()
    {
        var log = new Recording();
        var state = new LoggingState();
        using var messenger = new Messenger(
            new NoHost(),
            new InMemoryMessageStore(),
            new FakeTimeProvider(),
            log,
            state
        );
        await messenger.UseAsync("p", "NZ0P-10", Ct);

        messenger.Receive(AprsPacket.Parse("NZ0P-9>APRS::NZ0P-10  :meet at 6{7"), "tnc", heardHere: true);
        state.Update(LoggingSettings.Default with { MessageTexts = true });
        messenger.Receive(AprsPacket.Parse("NZ0P-9>APRS::NZ0P-10  :bring the radio{8"), "tnc", heardHere: true);

        Assert.Contains(
            log.Lines,
            l =>
                l.Contains("NZ0P-9 (7)", StringComparison.Ordinal)
                && l.EndsWith("(text not logged)", StringComparison.Ordinal)
        );
        Assert.DoesNotContain(log.Lines, l => l.Contains("meet at 6", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.EndsWith("bring the radio", StringComparison.Ordinal));
    }

    private sealed class Recording : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Lines.Add(formatter(state, exception));
    }

    private sealed class NoHost : IMessengerHost
    {
        public IReadOnlyList<string> Groups() => [];

        public EngineError? ProfileBlock() => null;

        public bool CanSendOn(string sourceId) => false;

        public IReadOnlyList<(string Id, string Name, bool IsRf)> TransmitSources() => [];

        public EngineError? WhyNotOn(string? sourceId) => null;

        public PingSettings Ping() => PingSettings.Off;

        public AutoReplySettings AutoReply() => AutoReplySettings.Off;

        public Task<bool> SendAsync(string sourceId, MessagePayload payload, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
