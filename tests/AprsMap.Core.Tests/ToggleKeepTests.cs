// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>
/// The switches are the switches' to change (docs/features/F1.15-quick-toggles.md, behavior 3): a settings save made
/// from a copy taken before a switch changed doesn't turn it back.
/// </summary>
public sealed class ToggleKeepTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASaveFromAnOlderCopyKeepsTheSwitchesAsTheyAre()
    {
        using var engine = new Engine(
            Options.Create(new EngineOptions()),
            new EngineTests.FakeSourceFactory(),
            InMemorySettingsStore.WithProfile(),
            new FakeTimeProvider(),
            NullLogger<Engine>.Instance
        );
        var before = await engine.GetSettingsAsync(Ct); // a Settings page, say, opened with everything on

        await engine.SetToggleAsync(Toggle.Internet, false, Ct);
        await engine.SetToggleAsync(Toggle.AprsIs, false, Ct);
        await engine.SaveSettingsAsync(before with { Units = UnitSettings.Imperial }, Ct); // then a setting saved there

        var after = await engine.GetSettingsAsync(Ct);
        Assert.Equal(UnitSettings.Imperial, after.Units); // the save itself counts
        Assert.False(after.EffectiveToggles.Internet);
        Assert.False(after.EffectiveToggles.AprsIs);
    }
}
