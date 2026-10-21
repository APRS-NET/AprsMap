// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Map;
using AprsMap.UI.ViewModels;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Where others see the station, on the map and in the Transmit tooltip (docs/features/F2.3-seen-as.md).</summary>
public sealed class SeenAsUiTests
{
    [AvaloniaFact]
    public void TheMapShowsWhereOthersSeeMeOnlyBeyondFiftyMetres()
    {
        using var view = new MapView();
        view.ShowMe(49, -72, "N0TEST");

        view.ShowSeen(49, -71.95, 3.7);
        Assert.NotNull(view.Seen);

        view.ShowSeen(49, -72.0003, 0.02); // GPS noise
        Assert.Null(view.Seen);
        view.ShowSeen(null, null, 0);
        Assert.Null(view.Seen);
    }

    [Theory]
    [InlineData(3.7, "Others see you 3.7 km from here, from your last position's course and speed.")]
    [InlineData(0.01, "Others see you where you are.")]
    public void TheTransmitTooltipSaysHowFarOffOthersSeeMe(double km, string said)
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings with { Toggles = StationToggles.AllOn with { Transmit = true } });

        vm.Show(
            new EngineStatus(
                ConnectionState.Connected,
                "T2TEST",
                1,
                "1.0",
                Toggles: StationToggles.AllOn with
                {
                    Transmit = true,
                },
                Transmit: new TransmitStatus(true, SeenAt: new SeenPosition(49, -71.95, km, DateTimeOffset.UnixEpoch))
            )
        );

        Assert.EndsWith(said, vm.TransmitTip, StringComparison.Ordinal);
        Assert.Equal(km, vm.SeenAt?.DistanceKm);
    }
}
