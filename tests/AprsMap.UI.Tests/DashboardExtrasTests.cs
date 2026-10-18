// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.UI.Localization;
using AprsMap.UI.Stations;
using AprsMap.UI.Statistics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace AprsMap.UI.Tests;

/// <summary>Dashboard extras (docs/features/F1.22-statistics.md, behaviors 6, 8; acceptance test 8).</summary>
public sealed class DashboardExtrasTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-18T12:00:00Z", null);

    private static Tile Tile()
    {
        var text = new Localizer(CultureInfo.GetCultureInfo("en-US"));
        var tile = new Tile();
        tile.Set(text, "Dashboard.Packets", 1234, null, [3, 0, 12], "Last hour");
        tile.Times(text, Start, TimeSpan.FromMinutes(5), at => at.ToString("HH:mm", CultureInfo.InvariantCulture));
        return tile;
    }

    [Fact]
    public void EachPointIsSaidWithItsValueAndTime()
    {
        Assert.Equal(["3 at 12:00", "0 at 12:05", "12 at 12:10"], Tile().PointLabels);
    }

    [Fact]
    public void CopyGivesTheTitleNumberAndEachPointTabSeparated()
    {
        Assert.Equal("Packets received\t1,234\n12:00\t3\n12:05\t0\n12:10\t12\n", Tile().CopyText());
    }

    [AvaloniaFact]
    public void ASparklinesPointsAreReachedByKeyboardWithTheirWordsAsATooltip()
    {
        var spark = new Sparkline
        {
            Values = [3, 0, 12],
            Labels = ["3 at 12:00", "0 at 12:05", "12 at 12:10"],
            Stroke = Brushes.Black,
            Width = 120,
            Height = 30,
        };
        var window = new Window
        {
            Content = spark,
            Width = 200,
            Height = 100,
        };
        window.Show();
        Assert.True(spark.Focusable);

        spark.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, spark.Point); // the newest first
        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, spark.Point);
        Assert.Equal("0 at 12:05", ToolTip.GetTip(spark));
        window.KeyPressQwerty(PhysicalKey.Home, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("3 at 12:00", ToolTip.GetTip(spark));
        window.Close();
    }
}
