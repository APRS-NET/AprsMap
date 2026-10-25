// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>The open conversation's route line follows the Transmit switch (F2.4 behavior 8).</summary>
public sealed class MessagesRouteRefreshTests
{
    [Fact]
    public async Task TurningTransmitOnClearsTheTransmitIsOffLine()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        var off = engine.Status with { Toggles = StationToggles.AllOn with { Transmit = false } };
        vm.Show(off);
        vm.Messages.Open("N0ABC");
        await Task.Yield();
        Assert.StartsWith("Waiting", vm.Messages.RouteText ?? string.Empty, StringComparison.Ordinal);

        vm.Show(off with { Toggles = StationToggles.AllOn with { Transmit = true } });
        await Task.Yield();

        Assert.DoesNotContain("Transmit", vm.Messages.RouteText ?? string.Empty, StringComparison.Ordinal);
    }
}
