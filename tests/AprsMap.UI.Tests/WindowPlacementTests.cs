// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Shell;
using Avalonia;

namespace AprsMap.UI.Tests;

/// <summary>Windows that stay reachable (docs/features/F1.0-shell-and-docking.md, behaviors 12–15; acceptance test 4).</summary>
public sealed class WindowPlacementTests
{
    // A laptop panel at 1.5× and an external monitor to its right at 1×.
    internal static readonly ScreenArea Laptop = new(new(0, 0, 2880, 1800), new(0, 0, 2880, 1740), 1.5);
    internal static readonly ScreenArea External = new(new(2880, 0, 2560, 1440), new(2880, 0, 2560, 1400), 1.0);

    // The same external monitor arranged to the laptop's left: negative coordinates.
    private static readonly ScreenArea ExternalLeft = new(new(-2560, 0, 2560, 1440), new(-2560, 0, 2560, 1400), 1.0);

    [Fact]
    public void AWindowOnAScreenThatStillExistsStaysWhereItWas()
    {
        var (position, size) = WindowPlacement.Fit(new(3000, 100), new(800, 600), [Laptop, External]);

        Assert.Equal(new PixelPoint(3000, 100), position);
        Assert.Equal(new Size(800, 600), size);
    }

    [Fact]
    public void AWindowLeftOnAnUnpluggedMonitorMovesToTheNearestScreen()
    {
        var (position, size) = WindowPlacement.Fit(new(3000, 100), new(800, 600), [Laptop]);

        Assert.True(Laptop.WorkingArea.Contains(position), position.ToString());
        Assert.True(position.X + size.Width * Laptop.Scaling <= Laptop.WorkingArea.Right);
    }

    [Fact]
    public void AWindowAtNegativeCoordinatesComesBackWhenThatMonitorIsGone()
    {
        Assert.Equal(
            new PixelPoint(-2000, 200),
            WindowPlacement.Fit(new(-2000, 200), new(800, 600), [Laptop, ExternalLeft]).Position
        );

        var (position, _) = WindowPlacement.Fit(new(-2000, 200), new(800, 600), [Laptop]);

        Assert.True(Laptop.WorkingArea.Contains(position), position.ToString());
    }

    [Fact]
    public void AWindowWhoseTitleBarIsAboveTheScreenIsBroughtDown()
    {
        // Mostly visible, but its title bar is above the top of the screen, so it couldn't be dragged.
        var (position, _) = WindowPlacement.Fit(new(100, -200), new(800, 600), [Laptop]);

        Assert.True(position.Y >= Laptop.WorkingArea.Y);
    }

    [Fact]
    public void AWindowBiggerThanTheScreenIsShrunkToFit()
    {
        var (_, size) = WindowPlacement.Fit(new(3000, 0), new(2500, 1400), [Laptop]);

        Assert.True(size.Width <= Laptop.WorkingArea.Width / Laptop.Scaling);
        Assert.True(size.Height <= Laptop.WorkingArea.Height / Laptop.Scaling);
    }

    [Fact]
    public void MixedScalingUsesEachScreensOwnScale()
    {
        // 120 px of title strip is enough on the 1× monitor; on the 1.5× laptop it takes 180 px.
        Assert.Equal(
            new PixelPoint(5300, 100),
            WindowPlacement.Fit(new(5300, 100), new(800, 600), [Laptop, External]).Position
        );
        Assert.NotEqual(
            new PixelPoint(2750, 100),
            WindowPlacement.Fit(new(2750, 100), new(800, 600), [Laptop]).Position
        );
    }

    [Fact]
    public void WithoutScreensNothingMoves() =>
        Assert.Equal(new PixelPoint(9000, 9000), WindowPlacement.Fit(new(9000, 9000), new(800, 600), []).Position);

    [Fact]
    public void TheMonitorFingerprintIgnoresOrderButNotArrangement()
    {
        Assert.Equal(WindowPlacement.Fingerprint([Laptop, External]), WindowPlacement.Fingerprint([External, Laptop]));
        Assert.NotEqual(
            WindowPlacement.Fingerprint([Laptop, External]),
            WindowPlacement.Fingerprint([Laptop, ExternalLeft])
        );
        Assert.NotEqual(WindowPlacement.Fingerprint([Laptop, External]), WindowPlacement.Fingerprint([Laptop]));
    }
}
