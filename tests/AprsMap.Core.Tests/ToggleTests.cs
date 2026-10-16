// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>Quick toggles in the engine (docs/features/F1.15-quick-toggles.md, acceptance tests 1, 2).</summary>
public sealed class ToggleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (Engine Engine, EngineTests.FakeSourceFactory Sources, InMemorySettingsStore Store) Create(
        StationToggles? toggles = null
    )
    {
        var settings = InMemorySettingsStore.WithProfile();
        var store = new InMemorySettingsStore(settings.Stored! with { Toggles = toggles });
        var sources = new EngineTests.FakeSourceFactory();
        var engine = new Engine(
            Options.Create(new EngineOptions()),
            sources,
            store,
            new FakeTimeProvider(),
            NullLogger<Engine>.Instance
        );
        return (engine, sources, store);
    }

    [Fact]
    public async Task AStationStartedWithAprsIsOffStaysQuietUntilItIsTurnedOn()
    {
        var (engine, sources, store) = Create(new StationToggles(AprsIs: false));
        var session = sources.Enqueue("T2TEST");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var run = engine.RunAsync(cts.Token);
        await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).Connection == ConnectionState.Disconnected);
        Assert.Equal(0, sources.Attempts);
        Assert.False((await engine.GetStatusAsync(Ct)).Toggles!.AprsIs);

        await engine.SetToggleAsync(Toggle.AprsIs, true, Ct);
        await session.Connected.Task.WaitAsync(Ct);

        Assert.True(store.Stored!.EffectiveToggles.AprsIs); // kept for the next start (acceptance test 2)
        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task TurningTheInternetOffDisconnects()
    {
        var (engine, sources, _) = Create();
        var session = sources.Enqueue("T2TEST");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = engine.RunAsync(cts.Token);
        await session.Connected.Task.WaitAsync(Ct);

        await engine.SetToggleAsync(Toggle.Internet, false, Ct);

        await WaitUntil(async () => (await engine.GetStatusAsync(Ct)).Connection == ConnectionState.Disconnected);
        Assert.Equal(1, sources.Attempts);
        await cts.CancelAsync();
        await run;
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public async Task TilesDownloadOnlyWithTheInternetAndTilesOn(bool internet, bool tiles, bool downloads)
    {
        var (engine, _, _) = Create(new StationToggles(Internet: internet, Tiles: tiles));

        await engine.GetSettingsAsync(Ct); // loads them

        Assert.Equal(downloads, engine.DownloadsTiles);
    }

    [Fact]
    public async Task WithTheInternetOffNothingIsLookedUp()
    {
        var (engine, _, _) = Create(new StationToggles(Internet: false));
        await engine.GetSettingsAsync(Ct);

        var refused = await Assert.ThrowsAsync<EngineOperationException>(() =>
            engine.GeocodeAsync("Boulder", null, Ct)
        );

        Assert.Equal(EngineErrorCodes.GeocodingOffline, refused.EngineError.Code);
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 400; i++)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, Ct);
        }
        Assert.Fail("Condition was not met in time.");
    }
}
