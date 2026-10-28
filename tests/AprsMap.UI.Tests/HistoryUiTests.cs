// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Settings;

namespace AprsMap.UI.Tests;

/// <summary>Settings › Data › History (docs/features/F1.41-persistent-history.md, behaviors 2, 3, 5, 13).</summary>
public sealed class HistoryUiTests
{
    private static readonly DateTimeOffset Since = DateTimeOffset.Parse("2026-10-21T14:02:00Z", null);

    private static async Task<HistorySettingsViewModel> Section(FakeEngine engine)
    {
        var section = new HistorySettingsViewModel(
            engine,
            new Localizer(CultureInfo.GetCultureInfo("en-US")),
            TimeZoneInfo.Utc,
            _ => { }
        );
        await section.ShowAsync(engine.Settings);
        return section;
    }

    [Fact]
    public async Task ItShowsWhatsKeptAndSavesTheLengthAndSizeLimit()
    {
        var engine = new FakeEngine
        {
            History = new HistoryStatus(
                true,
                HistorySettings.Default,
                Bytes: 412L * 1024 * 1024,
                KeptSince: Since,
                DaysKept: 7
            ),
        };
        var section = await Section(engine);
        Assert.True(section.On); // on by default
        Assert.Equal(("7", "1024"), (section.Days, section.MaxMegabytes));
        Assert.StartsWith("7 days kept, 412 MB, since 10/21/2026 2:02", section.StatusText, StringComparison.Ordinal);
        Assert.Null(section.Warning);

        section.Days = "14";
        section.MaxMegabytes = "2048";
        await section.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new HistorySettings(14, 2048), engine.Settings.History);
        Assert.Equal("Saved", section.SavedText);

        section.Days = "31";
        await section.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Keep for 1 to 30 days, within 100 to 102,400 MB.", section.Error);
        Assert.Equal(new HistorySettings(14, 2048), engine.Settings.History); // unchanged
    }

    [Fact]
    public async Task TurningItOffAsksFirstBecauseWhatsKeptIsDeleted()
    {
        var engine = new FakeEngine
        {
            History = new HistoryStatus(true, HistorySettings.Default, KeptSince: Since, DaysKept: 2),
        };
        var section = await Section(engine);

        section.On = false;
        await section.SaveCommand.ExecuteAsync(null);
        Assert.Null(engine.Settings.History);
        Assert.StartsWith("Turning history off deletes", section.Error, StringComparison.Ordinal);

        await section.SaveCommand.ExecuteAsync(null);
        Assert.Equal(0, engine.Settings.History!.Days);
        Assert.Equal(
            "History is off: stations are kept in memory only, and start empty after a restart.",
            section.StatusText
        );
    }

    [Fact]
    public async Task TheConfigurationsLimitsAreShownNotChanged()
    {
        var engine = new FakeEngine
        {
            History = new HistoryStatus(true, new HistorySettings(3, 500), Configured: true),
        };
        var section = await Section(engine);

        Assert.False(section.CanChange);
        Assert.Equal(("3", "500"), (section.Days, section.MaxMegabytes));
        Assert.EndsWith(
            "set in the gateway's configuration ([engine.history]).",
            section.StatusText,
            StringComparison.Ordinal
        );
    }

    [Theory]
    [InlineData(HistoryProblem.SizeLimitShortens, "The size limit keeps about 3 days of the 7 asked for.")]
    [InlineData(HistoryProblem.TodayFull, "Today's packets alone reached the size limit")]
    [InlineData(HistoryProblem.InUseElsewhere, "Another copy of the app is using this history folder")]
    [InlineData(HistoryProblem.CantWrite, "The history folder can't be written to")]
    public async Task WhatStandsInItsWayIsAWarning(HistoryProblem problem, string warning)
    {
        var engine = new FakeEngine
        {
            History = new HistoryStatus(true, HistorySettings.Default, Problem: problem, DaysFit: 3),
        };

        var section = await Section(engine);

        Assert.StartsWith(warning, section.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DaysSetAsideAreNamed()
    {
        var engine = new FakeEngine
        {
            History = new HistoryStatus(true, HistorySettings.Default, SetAside: [new DateOnly(2026, 10, 25)]),
        };

        var section = await Section(engine);

        Assert.Equal("Days that couldn't be read were set aside: 10/25/2026.", section.Warning);
    }

    [Fact]
    public async Task WithoutHistoryItSaysSo()
    {
        var engine = new FakeEngine { History = HistoryStatus.NotAvailable };

        var section = await Section(engine);

        Assert.Equal("This station keeps no history.", section.StatusText);
    }
}
