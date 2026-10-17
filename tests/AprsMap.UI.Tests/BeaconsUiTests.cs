// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Settings;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Beacons in the UI (docs/features/F2.2-beacons.md, behaviors 1–5, 10).</summary>
public sealed class BeaconsUiTests
{
    private static readonly SourceSettings AprsIs = FakeEngine.Rotate with { Transmit = true };

    private static readonly SourceSettings Tnc = new(
        "tnc",
        "Direwolf",
        SourceKind.KissTcp,
        Host: "127.0.0.1",
        Transmit: true
    );

    private static AppSettings Licensed(bool confirmed = true, bool licensed = true) =>
        new(
            [
                new StationProfile("home", "Home", "N0TEST", 12345, [AprsIs.Id, Tnc.Id], 49.0, -72.0) with
                {
                    Licensed = licensed,
                    PositionConfirmed = confirmed,
                },
            ],
            "home",
            UnitSettings.Metric,
            Sources: [AprsIs, Tnc]
        );

    private static (BeaconsSectionViewModel Section, FakeEngine Engine) Create(AppSettings settings)
    {
        var engine = new FakeEngine { Settings = settings };
        var section = new BeaconsSectionViewModel(engine, Localizer.Instance, _ => { });
        section.Show(settings);
        return (section, engine);
    }

    [Fact]
    public async Task BeaconsAreOffAndSavingThemKeepsSendViaOnEverySourceUnlessOneIsUnticked()
    {
        var (section, engine) = Create(Licensed());

        Assert.Equal("Beacons for Home", section.Heading);
        Assert.True(section.CanEdit);
        Assert.False(section.PositionOn);
        Assert.False(section.StatusOn);
        Assert.Equal(PositionBeaconSchedule.Decay, section.SelectedSchedule!.Value);
        Assert.Equal(["Soon after a change, then less often", "Every interval"], section.Schedules.Select(s => s.Name));
        Assert.Equal("30 minutes", section.SelectedPositionInterval!.Name);
        Assert.Equal("1 hour", section.SelectedStatusInterval!.Name);
        Assert.Equal(["rotate.aprs2.net", "Direwolf"], section.PositionVia.Select(v => v.Name));
        Assert.All(section.PositionVia.Concat(section.StatusVia), v => Assert.True(v.IsChecked));

        section.PositionOn = true;
        section.StatusOn = true;
        section.StatusText = " QRV on 146.52 ";
        section.StatusVia[1].IsChecked = false;
        section.IncludePhg = true;
        await section.SaveCommand.ExecuteAsync(null);

        var profile = engine.Settings.GetActiveProfile()!;
        Assert.Equal(new PositionBeaconSettings(On: true, Phg: new PhgSettings(9, 20, 3)), profile.PositionBeacon);
        Assert.Equal(new StatusBeaconSettings(true, "QRV on 146.52", 60, [AprsIs.Id]), profile.StatusBeacon);
        Assert.Equal("Saved", section.SavedText);
        Assert.Null(section.Error);
    }

    [Fact]
    public void AProfileThatOnlyListensOrIsUnconfirmedSaysSo()
    {
        var (listening, _) = Create(Licensed(licensed: false));
        Assert.Equal("This profile only listens, so it doesn't beacon.", listening.ListensOnlyText);
        Assert.False(listening.CanEdit);

        var settings = Licensed(confirmed: false);
        settings = settings with
        {
            Profiles = [settings.Profiles[0] with { PositionBeacon = new PositionBeaconSettings(On: true) }],
        };
        var (unconfirmed, _) = Create(settings);
        Assert.Equal("Send your position once by hand to start beaconing.", unconfirmed.UnconfirmedText);
        Assert.True(unconfirmed.CanEdit);
    }

    [Fact]
    public async Task ARefusedSaveShowsTheEnginesReason()
    {
        var (section, engine) = Create(Licensed());
        engine.SaveFailure = EngineErrors.ToException(
            new EngineError(
                EngineErrorCodes.ProfileStatusInvalid,
                "bad",
                new Dictionary<string, string> { [EngineErrorArgs.Profile] = "Home" }
            )
        );

        await section.SaveCommand.ExecuteAsync(null);

        Assert.Equal(
            "Profile “Home”: a status can have up to 62 letters, digits and plain punctuation, without accents.",
            section.Error
        );
    }

    [Fact]
    public void TheStatusBarSaysWhenTheNextBeaconIsDueAndWhy()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T12:00:00Z", null));
        var engine = new FakeEngine { Settings = Licensed() };
        var vm = new MainViewModel(engine, time);
        vm.Show(engine.Settings);
        var toggles = StationToggles.AllOn with { Transmit = true };
        EngineStatus Status(TransmitStatus transmit) =>
            new(
                ConnectionState.Connected,
                "T2TEST",
                0,
                "1.0",
                Verified: true,
                Toggles: toggles,
                Transmit: transmit,
                Sources: [new SourceStatus(Tnc.Id, Tnc.Name, SourceKind.KissTcp, ConnectionState.Connected)]
            );

        vm.Show(
            Status(
                new TransmitStatus(
                    true,
                    NextBeacon: new NextBeacon(
                        time.GetUtcNow().AddMinutes(11.5),
                        BeaconKind.Position,
                        BeaconDueReason.Spacing,
                        Tnc.Id
                    )
                )
            )
        );
        Assert.Equal("Transmitting · next beacon in 12 min", vm.TransmitText);
        Assert.Equal(
            "Next: your position on Direwolf, held back to keep beacons spaced out on this source.",
            vm.TransmitTip
        );

        vm.Show(
            Status(
                new TransmitStatus(true, BeaconHeld: new EngineError(EngineErrorCodes.TransmitPositionUnconfirmed, "x"))
            )
        );
        Assert.Equal("Transmitting · beacons waiting", vm.TransmitText);
        Assert.Equal("Beacons are waiting: Send your position once by hand to start beaconing.", vm.TransmitTip);
        Assert.False(vm.IsTransmitHeld); // a hand send is still possible: that's what starts them
    }
}
