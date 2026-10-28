// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>The packet history's store (docs/features/F1.41-persistent-history.md, acceptance tests 1, 2, 6–8).</summary>
public sealed class PacketHistoryTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Position = "N0POS-9>APRS,WIDE1-1:!4903.50N/07201.75W>moving";
    private const string Object = "N0OBJ>APRS:;AID-3    *111111z4903.50N/07201.75WAaid station";

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("aprsmap-history-");
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-27T12:00:00Z", null));

    public void Dispose() => _folder.Delete(recursive: true);

    private Engine Create(Action<HistoryOptions>? configure = null)
    {
        var options = new EngineOptions { History = { Folder = _folder.FullName } };
        configure?.Invoke(options.History);
        return new Engine(
            Options.Create(options),
            new EngineTests.FakeSourceFactory(),
            InMemorySettingsStore.WithProfile(),
            _time,
            NullLogger<Engine>.Instance
        );
    }

    private PacketHistory Store(int days = 7, int megabytes = 1024) =>
        new(_folder.FullName, _time, NullLogger.Instance) { Settings = new HistorySettings(days, megabytes) };

    private HistoryRecord Record(int bytes = 64, string name = "N0CALL") =>
        new(_time.GetUtcNow(), "aprs-is", false, false, name, StationKind.Station, null, null, new byte[bytes]);

    private string[] DayFiles() => [.. _folder.GetFiles("*.db").Select(f => f.Name).Order(StringComparer.Ordinal)];

    private static async Task<List<HistoryRecord>> ReadAll(PacketHistory history) =>
        await history.ReadAsync(DateTimeOffset.UnixEpoch, Ct).ToListAsync(Ct);

    [Fact]
    public async Task EveryPacketListedIsKeptWithItsTimeSourceAndStation()
    {
        using var engine = Create();
        await engine.GetSettingsAsync(Ct);

        engine.Accept(Position);
        _time.Advance(TimeSpan.FromSeconds(30));
        engine.Accept(Object);
        engine.Accept(Object); // a duplicate is kept, marked
        await engine.FlushHistoryAsync();
        var kept = await engine.ReadHistoryAsync(DateTimeOffset.UnixEpoch).ToListAsync(Ct);

        Assert.Equal(3, kept.Count);
        var position = kept[0];
        Assert.Equal(
            ("N0POS-9", StationKind.Station, false, Position),
            (position.Name, position.Kind, position.Duplicate, System.Text.Encoding.Latin1.GetString(position.Raw))
        );
        Assert.Equal(49.058, position.Latitude!.Value, 3);
        Assert.Equal(DateTimeOffset.Parse("2026-10-27T12:00:00Z", null), position.ReceivedAt);
        Assert.Equal(("AID-3", StationKind.Object, false), (kept[1].Name, kept[1].Kind, kept[1].Duplicate));
        Assert.True(kept[2].Duplicate);
        Assert.Equal(["2026-10-27.db"], DayFiles());
    }

    [Fact]
    public async Task EachDayHasItsOwnFileAndDaysPastTheLengthAreDeleted()
    {
        await using var history = Store(days: 2);
        for (var i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromDays(i == 0 ? 0 : 1));
            history.Record(Record());
            await history.FlushAsync(Ct);
        }
        Assert.Equal(["2026-10-27.db", "2026-10-28.db", "2026-10-29.db"], DayFiles());

        await history.MaintainAsync(Ct); // the 29th: it and the 28th are the two days kept

        Assert.Equal(["2026-10-28.db", "2026-10-29.db"], DayFiles());
        var status = await history.StatusAsync(configured: false, Ct);
        Assert.Equal((2, DateTimeOffset.Parse("2026-10-28T12:00:00Z", null)), (status.DaysKept, status.KeptSince));
        Assert.Equal(HistoryProblem.None, status.Problem);
    }

    [Fact]
    public async Task PastTheSizeLimitTheOldestDaysGoAndTheStatusSaysHowManyFit()
    {
        await using var history = Store(days: 7, megabytes: 1);
        for (var day = 0; day < 3; day++)
        {
            _time.Advance(TimeSpan.FromDays(day == 0 ? 0 : 1));
            for (var i = 0; i < 150; i++)
            {
                history.Record(Record(bytes: 2048)); // over half a megabyte a day on disk
            }
            await history.FlushAsync(Ct);
        }

        await history.MaintainAsync(Ct);
        var status = await history.StatusAsync(configured: false, Ct);

        Assert.Equal(["2026-10-29.db"], DayFiles()); // the 27th and 28th went to keep within 1 MB
        Assert.Equal((HistoryProblem.SizeLimitShortens, (int?)1), (status.Problem, status.DaysFit));
        Assert.True(status.Bytes <= 1024 * 1024);
    }

    [Fact]
    public async Task TheSizeLimitEstimatesTheDaysThatFitFromWholeDays()
    {
        await using var history = Store(days: 7, megabytes: 1);
        for (var i = 0; i < 50; i++)
        {
            history.Record(Record(bytes: 2048));
        }
        await history.FlushAsync(Ct);
        _time.Advance(TimeSpan.FromDays(1));

        await history.MaintainAsync(Ct);
        var status = await history.StatusAsync(configured: false, Ct);

        // 1 MB holds a few such days, not seven. Not exact: Windows reports an open file's size late.
        Assert.Equal(HistoryProblem.SizeLimitShortens, status.Problem);
        Assert.InRange(status.DaysFit!.Value, 1, 6);
    }

    [Fact]
    public async Task WhenTodayAloneReachesTheLimitNothingMoreIsKeptToday()
    {
        await using var history = Store(days: 7, megabytes: 1);
        for (var i = 0; i < 700; i++)
        {
            history.Record(Record(bytes: 2048));
        }
        await history.FlushAsync(Ct);
        await history.MaintainAsync(Ct);
        var before = (await ReadAll(history)).Count;

        history.Record(Record());
        await history.FlushAsync(Ct);

        Assert.Equal(before, (await ReadAll(history)).Count);
        Assert.Equal(HistoryProblem.TodayFull, (await history.StatusAsync(false, Ct)).Problem);
        Assert.Equal(HistoryProblem.TodayFull, history.SizeProblem()!.Problem); // for the dashboard (behavior 5)
        _time.Advance(TimeSpan.FromDays(1)); // a new day starts again
        history.Record(Record());
        await history.FlushAsync(Ct);
        Assert.Equal(before + 1, (await ReadAll(history)).Count);
    }

    [Fact]
    public async Task AnUnreadableDayIsSetAsideAndANewOneStarted()
    {
        await File.WriteAllTextAsync(Path.Combine(_folder.FullName, "2026-10-27.db"), "not a database", Ct);
        await using var history = Store();

        history.Record(Record());
        await history.FlushAsync(Ct);

        Assert.Single(await ReadAll(history));
        Assert.True(File.Exists(Path.Combine(_folder.FullName, "2026-10-27.db.invalid")));
        Assert.Equal([new DateOnly(2026, 10, 27)], (await history.StatusAsync(false, Ct)).SetAside);
    }

    [Fact]
    public async Task ASecondEngineOnTheSameFolderKeepsNothing()
    {
        await using var first = Store();
        await using var second = Store();

        second.Record(Record());
        await second.FlushAsync(Ct);

        Assert.Equal(HistoryProblem.InUseElsewhere, (await second.StatusAsync(false, Ct)).Problem);
        Assert.Empty(DayFiles());
    }

    [Fact]
    public async Task TurnedOffWhatsKeptIsDeletedAndNothingMoreIsKept()
    {
        using var engine = Create();
        var settings = await engine.GetSettingsAsync(Ct);
        engine.Accept(Position);
        await engine.FlushHistoryAsync();
        Assert.Single(DayFiles());

        await engine.SaveSettingsAsync(settings with { History = new HistorySettings(Days: 0) }, Ct);
        engine.Accept(Position);
        await engine.FlushHistoryAsync();

        await WaitUntilAsync(() => DayFiles().Length == 0);
        Assert.Empty(await engine.ReadHistoryAsync(DateTimeOffset.UnixEpoch).ToListAsync(Ct));
    }

    [Fact]
    public async Task StartingOverDeletesTheHistory()
    {
        using var engine = Create();
        await engine.GetSettingsAsync(Ct);
        engine.Accept(Position);
        await engine.FlushHistoryAsync();

        await engine.ResetSettingsAsync(Ct);

        Assert.Empty(DayFiles());
    }

    [Fact]
    public async Task TheConfigurationsLengthAndSizeWinAndAreShownAsConfigured()
    {
        using var engine = Create(h =>
        {
            h.Days = 3;
            h.MaxMegabytes = 500;
        });
        var settings = await engine.GetSettingsAsync(Ct);
        await engine.SaveSettingsAsync(settings with { History = new HistorySettings(Days: 20) }, Ct);

        var status = await engine.GetHistoryStatusAsync(Ct);

        Assert.Equal((new HistorySettings(3, 500), true), (status.Settings, status.Configured));
    }

    [Fact]
    public async Task TheConfigurationsValuesAreBroughtIntoRange()
    {
        var options = new HistoryOptions { Days = 90, MaxMegabytes = 1 };
        await using var history = Store();

        Assert.Equal(new HistorySettings(30, 100), options.Apply(HistorySettings.Default));
        Assert.Null(history.SizeProblem()); // nothing standing in the way
    }

    [Fact]
    public async Task OutOfRangeSettingsAreRefused()
    {
        using var engine = Create();
        var settings = await engine.GetSettingsAsync(Ct);

        var refused = await Assert.ThrowsAsync<EngineArgumentException>(() =>
            engine.SaveSettingsAsync(settings with { History = new HistorySettings(Days: 31) }, Ct)
        );

        Assert.Equal(EngineErrorCodes.HistoryInvalid, refused.EngineError.Code);
    }

    [Fact]
    public async Task AnEngineWithoutAFolderKeepsNoHistory()
    {
        using var engine = new Engine(
            Options.Create(new EngineOptions()),
            new EngineTests.FakeSourceFactory(),
            InMemorySettingsStore.WithProfile(),
            _time,
            NullLogger<Engine>.Instance
        );

        Assert.Equal(HistoryStatus.NotAvailable, await engine.GetHistoryStatusAsync(Ct));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }
        Assert.True(condition());
    }
}
