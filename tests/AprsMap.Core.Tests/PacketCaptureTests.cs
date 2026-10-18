// SPDX-License-Identifier: MIT
using System.Text;
using AprsMap.Contracts;
using AprsNet.Corpus;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>Packet capture (docs/features/F1.27-packet-capture.md, acceptance tests 1–5).</summary>
public sealed class PacketCaptureTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string BadLatitude = "N0BAD>APRS:!49X3.50N/07201.75W>bad lat";
    private const string Unsupported = "N0UNK>APRS:%abcdef";
    private const string Good = "N0OK>APRS:!4903.50N/07201.75W>fine";
    private const string UserDefined = "N0USR>APRS:{Xhello";
    private const string UnknownPath = "N0PTH>APRS,TOOLONGCALL-99:!4903.50N/07201.75W>p";

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("aprsmap-capture-");
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-18T12:00:00Z", null));

    public void Dispose() => _folder.Delete(recursive: true);

    private Engine Create(CaptureSettings? capture)
    {
        var store = InMemorySettingsStore.WithProfile();
        var engine = new Engine(
            Options.Create(new EngineOptions { CaptureFolder = _folder.FullName }),
            new EngineTests.FakeSourceFactory(),
            store,
            _time,
            NullLogger<Engine>.Instance
        );
        return engine;
    }

    private static async Task On(Engine engine, CaptureSettings? capture = null)
    {
        var settings = await engine.GetSettingsAsync(Ct);
        await engine.SaveSettingsAsync(settings with { Capture = capture ?? new CaptureSettings(On: true) }, Ct);
    }

    [Fact]
    public async Task OffByDefaultNothingIsWritten()
    {
        using var engine = Create(null);
        await engine.GetSettingsAsync(Ct);

        engine.Accept(BadLatitude);
        await engine.FlushCaptureAsync(Ct);

        Assert.Empty(_folder.GetFiles());
        Assert.Null((await engine.GetStatusAsync(Ct)).CapturedToday);
    }

    [Fact]
    public async Task UnexpectedPacketsAreWrittenWithTheirReasonsAndGoodOnesArent()
    {
        using var engine = Create(null);
        await On(engine);

        engine.Accept(BadLatitude);
        engine.Accept(Unsupported);
        engine.Accept(Good);
        engine.Accept(UnknownPath);
        await engine.FlushCaptureAsync(Ct);

        var view = await engine.GetCaptureAsync(new CaptureQuery(), Ct);
        Assert.Equal([UnknownPath, Unsupported, BadLatitude], view.Packets.Select(p => p.Raw)); // newest first
        Assert.Equal(
            [
                "aprs.unrecognized.unknown-path-entry",
                EngineErrorCodes.CaptureUnsupported,
                "aprs.parse.invalid-latitude",
            ],
            view.Packets.Select(p => p.Reasons.Single().Code)
        );
        Assert.Equal("N0BAD", view.Packets[^1].Station);
        Assert.Equal([new CaptureDay(new DateOnly(2026, 10, 18), 3)], view.Days);
        Assert.Equal(3, (await engine.GetStatusAsync(Ct)).CapturedToday);

        // The log is a plain corpus file.
        var lines = CorpusFile.ReadLines(Path.Combine(_folder.FullName, "2026-10-18.log")).ToList();
        Assert.Equal(BadLatitude, lines[0].Packet);
        Assert.Equal(_time.GetUtcNow(), lines[0].ReceivedAt);

        var onlyBad = await engine.GetCaptureAsync(new CaptureQuery(Reason: "aprs.parse.invalid-latitude"), Ct);
        Assert.Equal([BadLatitude], onlyBad.Packets.Select(p => p.Raw));
    }

    [Fact]
    public async Task APacketThatIsntUtf8IsKeptByteForByte()
    {
        using var engine = Create(null);
        await On(engine);
        // A Mic-E-like packet whose information carries bytes above 0x7F, read as Latin-1.
        var raw = Encoding.Latin1.GetString([.. "N0MIC>APRS:%"u8.ToArray(), 0xB0, 0xE9, 0xFF, 0x1C]);

        engine.Accept(raw);
        await engine.FlushCaptureAsync(Ct);

        var line = CorpusFile.ReadLines(Path.Combine(_folder.FullName, "2026-10-18.log")).Single();
        Assert.Equal(Encoding.Latin1.GetBytes(raw), line.GetPacketBytes());
        Assert.Equal(raw, (await engine.GetCaptureAsync(new CaptureQuery(), Ct)).Packets.Single().Raw);
    }

    [Fact]
    public async Task TheSameStationsBadPacketIsWrittenOnceAnHour()
    {
        using var engine = Create(null);
        await On(engine);

        for (var i = 0; i < 100; i++)
        {
            engine.Accept(BadLatitude);
            _time.Advance(TimeSpan.FromSeconds(30)); // past the duplicate check, inside the hour
        }
        await engine.FlushCaptureAsync(Ct);

        var view = await engine.GetCaptureAsync(new CaptureQuery(), Ct);
        Assert.Single(view.Packets); // 50 minutes of repeats
        Assert.Equal(99, view.RepeatsLeftOut);

        _time.Advance(TimeSpan.FromMinutes(15)); // an hour after the first
        engine.Accept(BadLatitude);
        await engine.FlushCaptureAsync(Ct);
        Assert.Equal(2, (await engine.GetCaptureAsync(new CaptureQuery(), Ct)).Packets.Count);
    }

    [Fact]
    public async Task UserDefinedPacketsAreOnlyCapturedWhenAskedFor()
    {
        using var engine = Create(null);
        await On(engine);
        engine.Accept(UserDefined);
        await engine.FlushCaptureAsync(Ct);
        Assert.Empty((await engine.GetCaptureAsync(new CaptureQuery(), Ct)).Packets);

        await On(engine, new CaptureSettings(On: true, UserDefined: true));
        _time.Advance(TimeSpan.FromMinutes(1));
        engine.Accept(UserDefined);
        await engine.FlushCaptureAsync(Ct);
        Assert.Equal(
            "aprs.unrecognized.user-defined-data",
            (await engine.GetCaptureAsync(new CaptureQuery(), Ct)).Packets.Single().Reasons.Single().Code
        );
    }

    [Fact]
    public async Task OldDaysAndDaysPastTheSizeLimitGoFirst()
    {
        await File.WriteAllTextAsync(Path.Combine(_folder.FullName, "2026-09-01.log"), "old", Ct); // past 30 days
        await File.WriteAllTextAsync(
            Path.Combine(_folder.FullName, "2026-10-16.log"),
            new string('x', 2 * 1024 * 1024),
            Ct
        );
        await File.WriteAllTextAsync(Path.Combine(_folder.FullName, "2026-10-17.log"), "recent", Ct);
        using var engine = Create(null);
        await On(engine, new CaptureSettings(On: true, MaxMegabytes: 1));

        engine.Accept(BadLatitude); // a new day's first line trims
        await engine.FlushCaptureAsync(Ct);

        Assert.Equal(
            ["2026-10-17.log", "2026-10-18.json", "2026-10-18.log"],
            _folder.GetFiles().Select(f => f.Name).Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    public async Task AnAnonymizedExportIsReadyForTheCorpusWithANote()
    {
        using var engine = Create(null);
        await On(engine);
        engine.Accept(BadLatitude);
        engine.Accept(Unsupported);
        await engine.FlushCaptureAsync(Ct);

        var anonymized = await engine.ExportCaptureAsync(new CaptureExport(), Ct);
        var lines = anonymized.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, anonymized.Lines);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.True(CorpusLine.TryParse(l, out _)));
        Assert.DoesNotContain("N0BAD", anonymized.Text, StringComparison.Ordinal);
        Assert.Equal(BadLatitude.Length, CorpusLine.Parse(lines[0]).Packet.Length); // same length
        Assert.True(anonymized.Clean);
        Assert.Contains("`aprs.parse.invalid-latitude`: 1", anonymized.Note, StringComparison.Ordinal);
        Assert.Contains("ready for tests/corpus", anonymized.Note, StringComparison.Ordinal);
        Assert.Contains(Brand.DisplayName, anonymized.Note, StringComparison.Ordinal);

        var raw = await engine.ExportCaptureAsync(new CaptureExport(Anonymized: false), Ct);
        Assert.Contains("N0BAD", raw.Text, StringComparison.Ordinal);
        Assert.Contains("not anonymized", raw.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearDeletesTheFiles()
    {
        using var engine = Create(null);
        await On(engine);
        engine.Accept(BadLatitude);
        await engine.FlushCaptureAsync(Ct);

        await engine.ClearCaptureAsync(Ct);

        Assert.Empty(_folder.GetFiles());
        Assert.Equal(0, (await engine.GetStatusAsync(Ct)).CapturedToday);
    }
}
