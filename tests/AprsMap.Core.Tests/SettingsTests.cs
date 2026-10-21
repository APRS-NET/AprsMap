// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsMap.Core;

namespace AprsMap.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "aprsmap-settings-" + Guid.NewGuid().ToString("N")
    );

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static readonly SourceSettings Rotate = InMemorySettingsStore.Rotate;

    /// <summary>Rotate with a filter of its own, for the portable profile.</summary>
    private static readonly SourceSettings Filtered = Rotate with
    {
        Id = "filtered",
        Name = "rotate (filtered)",
        Filter = "r/40/-105/50",
    };

    private static AppSettings TwoProfiles()
    {
        var home = InMemorySettingsStore.Profile("N0CALL", "home") with { Latitude = 40.0, Longitude = -105.0 };
        var portable = InMemorySettingsStore.Profile("N0CALL-7", "portable") with
        {
            Name = "Portable",
            Passcode = 12345,
            SourceIds = [Filtered.Id],
        };
        return new AppSettings([home, portable], "portable", UnitSettings.Imperial, Sources: [Rotate, Filtered]);
    }

    private static string OldSettings(params (string Id, string Server, string? Filter)[] profiles) =>
        $$"""
        {
          "profiles": [{{string.Join(",", profiles.Select(p => $$"""
            { "id": "{{p.Id}}", "name": "{{p.Id}}", "callsign": "N0CALL", "passcode": -1, "server": "{{p.Server}}"{{(p.Filter is null ? "" : $", \"filter\": \"{p.Filter}\"")}} }
            """))}}],
          "activeProfileId": "{{profiles[0].Id}}",
          "units": { "altitude": "Metres", "distance": "Kilometres", "pressure": "Hectopascals", "rain": "Millimetres", "temperature": "Celsius", "wind": "KilometresPerHour" }
        }
        """;

    private async Task<(AppSettings Settings, string Folder)> MigrateAsync(string json)
    {
        var folder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, FileSettingsStore.FileName), json, Ct);
        return ((await new FileSettingsStore(folder).LoadAsync(Ct))!, folder);
    }

    [Fact]
    public async Task OldSettingsOnTwoServersBecomeTwoSourcesEachTickedByItsProfileAndTheOldFileIsKept()
    {
        var json = OldSettings(("home", "rotate.aprs2.net:14580", null), ("away", "euro.aprs2.net:14580", "t/m"));

        var (settings, folder) = await MigrateAsync(json);

        Assert.Equal(["rotate.aprs2.net", "euro.aprs2.net"], settings.SourceList.Select(s => s.Name));
        var home = settings.AprsIsSourceOf(settings.Profiles[0])!;
        var away = settings.AprsIsSourceOf(settings.Profiles[1])!;
        Assert.Equal(("rotate.aprs2.net:14580", null), (home.Server, home.Filter));
        Assert.Equal(("euro.aprs2.net:14580", "t/m"), (away.Server, away.Filter));
        Assert.Equal(json, await File.ReadAllTextAsync(Path.Combine(folder, "settings.json.v1"), Ct));
        SettingsValidator.Validate(settings, TimeProvider.System);

        // Saved converted: the next load reads it as it is.
        var again = await new FileSettingsStore(folder).LoadAsync(Ct);
        Assert.Equal(settings.SourceList, again!.SourceList);
    }

    [Fact]
    public async Task ProfilesOnOneServerShareASourceUnlessTheirFiltersDiffer()
    {
        var (agree, _) = await MigrateAsync(
            OldSettings(("a", "rotate.aprs2.net:14580", "t/m"), ("b", "rotate.aprs2.net:14580", "t/m"))
        );
        var (differ, _) = await MigrateAsync(
            OldSettings(("a", "rotate.aprs2.net:14580", "t/m"), ("b", "rotate.aprs2.net:14580", null))
        );

        Assert.Single(agree.SourceList);
        Assert.Equal(agree.Profiles[0].SourceIds, agree.Profiles[1].SourceIds);
        Assert.Equal(["rotate.aprs2.net", "rotate.aprs2.net (2)"], differ.SourceList.Select(s => s.Name));
        Assert.NotEqual(differ.Profiles[0].SourceIds, differ.Profiles[1].SourceIds);
    }

    [Fact]
    public async Task AFileStoreRoundTripsSettingsAndWritesReadableJson()
    {
        var store = new FileSettingsStore(Path.Combine(_root, "aprsmap"));
        Assert.Null(await store.LoadAsync(Ct));

        var settings = TwoProfiles();
        await store.SaveAsync(settings with { ReadOnly = true }, Ct);
        var loaded = await store.LoadAsync(Ct);

        Assert.NotNull(loaded);
        Assert.Equal(settings.Profiles, loaded.Profiles);
        Assert.Equal("portable", loaded.ActiveProfileId);
        Assert.Equal(UnitSettings.Imperial, loaded.Units);
        Assert.False(loaded.ReadOnly); // a caller's read-only view is never stored
        var json = await File.ReadAllTextAsync(store.FilePath, Ct);
        Assert.Contains("\"InchesOfMercury\"", json, StringComparison.Ordinal);
        Assert.Equal(
            [FileSettingsStore.FileName],
            Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!).Select(Path.GetFileName)
        );
    }

    [Fact]
    public async Task DeletingRemovesTheFileAndItsSetAsideCopyButNothingElse()
    {
        var store = new FileSettingsStore(_root);
        await store.SaveAsync(TwoProfiles(), Ct);
        await File.WriteAllTextAsync(store.FilePath + ".invalid", "{", Ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "appearance.json"), "{}", Ct);

        await store.DeleteAsync(Ct);
        await store.DeleteAsync(Ct); // nothing left: no error

        Assert.Null(await store.LoadAsync(Ct));
        Assert.Equal(["appearance.json"], Directory.GetFiles(_root).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task AnUnreadableFileIsMovedAsideAndTreatedAsAFirstRun(string content)
    {
        var store = new FileSettingsStore(_root);
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(store.FilePath, content, Ct);

        Assert.Null(await store.LoadAsync(Ct));

        Assert.False(File.Exists(store.FilePath));
        Assert.Equal(content, await File.ReadAllTextAsync(store.FilePath + ".invalid", Ct));
    }

    [Fact]
    public void TheCurrentUsersFolderEndsWithTheVendorAndProductFolders()
    {
        var folder = SettingsLocation.UserFolder("aprsmap");

        Assert.Equal("aprsmap", Path.GetFileName(folder));
        Assert.EndsWith(SettingsLocation.VendorFolder, Path.GetDirectoryName(folder)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsFromAPreviousNameAreCopiedOnce()
    {
        var current = Path.Combine(_root, "newname");
        var older = Path.Combine(_root, "oldest");
        var old = Path.Combine(_root, "oldname");
        await new FileSettingsStore(old).SaveAsync(TwoProfiles(), Ct);

        Assert.Equal(old, SettingsLocation.MigrateIfMissing(current, [older, old]));
        Assert.True(File.Exists(Path.Combine(old, FileSettingsStore.FileName))); // left in place
        Assert.Equal(TwoProfiles().Profiles, (await new FileSettingsStore(current).LoadAsync(Ct))!.Profiles);

        // Once the current folder has settings, nothing is copied over them.
        Assert.Null(SettingsLocation.MigrateIfMissing(current, [old]));
        Assert.Null(SettingsLocation.MigrateIfMissing(Path.Combine(_root, "fresh"), [older]));
    }

    [Fact]
    public void AConfiguredFolderWinsOverTheUsersFolder()
    {
        var store = SettingsLocation.CreateStore(new EngineOptions { SettingsFolder = _root });

        Assert.Equal(Path.Combine(_root, FileSettingsStore.FileName), store.FilePath);
        Assert.EndsWith(
            Path.Combine(SettingsLocation.VendorFolder, Brand.Slug, FileSettingsStore.FileName),
            SettingsLocation.CreateStore(new EngineOptions()).FilePath,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void TheFolderIsTheOneTheSettingsFileIsIn()
    {
        Assert.Equal(_root, SettingsLocation.Folder(new EngineOptions { SettingsFolder = _root }));
        Assert.Equal(
            Path.GetDirectoryName(SettingsLocation.CreateStore(new EngineOptions()).FilePath),
            SettingsLocation.Folder(new EngineOptions())
        );
    }

    [Fact]
    public void ValidSettingsPass()
    {
        SettingsValidator.Validate(TwoProfiles(), TimeProvider.System);
        SettingsValidator.Validate(AppSettings.Empty, TimeProvider.System);
    }

    public static TheoryData<string, string?, AppSettings> InvalidSettings()
    {
        var good = TwoProfiles();
        var home = good.Profiles[0];
        AppSettings HomeOnly(StationProfile profile) => good with { Profiles = [profile], ActiveProfileId = "home" };
        AppSettings HomeOn(SourceSettings source) =>
            good with
            {
                Profiles = [home],
                ActiveProfileId = "home",
                Sources = [source],
            };
        return new()
        {
            {
                EngineErrorCodes.ProfileIdDuplicate,
                null,
                good with
                {
                    Profiles = [home, home with { Name = "Other" }],
                }
            },
            { EngineErrorCodes.ProfileNameMissing, null, HomeOnly(home with { Name = " " }) },
            {
                EngineErrorCodes.ProfileNameDuplicate,
                "n0call",
                good with
                {
                    Profiles = [home, good.Profiles[1] with { Name = "n0call" }],
                }
            },
            { EngineErrorCodes.ProfilePositionOffGlobe, "N0CALL", HomeOnly(home with { Latitude = 91 }) },
            { EngineErrorCodes.ProfilePositionIncomplete, "N0CALL", HomeOnly(home with { Longitude = null }) },
            { "aprs.server-port-invalid", "N0CALL", HomeOn(Rotate with { Server = "host:x" }) },
            { "aprs.server-host-invalid", "N0CALL", HomeOn(Rotate with { Server = "bad host" }) },
            { "aprs.callsign-invalid-length", "N0CALL", HomeOnly(home with { Callsign = "" }) },
            { "aprs.filter-control-character", "N0CALL", HomeOn(Rotate with { Filter = "r/1/2/3\n#" }) },
            { EngineErrorCodes.SourceInvalid, null, good with { Sources = [Rotate, Rotate with { Name = "Again" }] } },
            { EngineErrorCodes.SourceInvalid, null, good with { Sources = [Rotate with { Name = " " }, Filtered] } },
            { EngineErrorCodes.SourceUnknown, "N0CALL", HomeOn(Filtered) },
            {
                EngineErrorCodes.ProfileTwoAprsIs,
                "N0CALL",
                HomeOnly(home with { SourceIds = [Rotate.Id, Filtered.Id] })
            },
            { EngineErrorCodes.ActiveProfileMissing, null, good with { ActiveProfileId = "nobody" } },
            { EngineErrorCodes.ActiveProfileMissing, null, AppSettings.Empty with { ActiveProfileId = "home" } },
            { EngineErrorCodes.SettingsIncomplete, null, good with { Units = null! } },
            { EngineErrorCodes.StationAgingInvalid, null, good with { Aging = new StationAging(0, null) } },
            { EngineErrorCodes.StationAgingInvalid, null, good with { Aging = new StationAging(30, 30) } },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void InvalidSettingsAreRejectedWithACode(string code, string? profile, AppSettings settings)
    {
        var ex = Assert.Throws<EngineArgumentException>(() =>
            SettingsValidator.Validate(settings, TimeProvider.System)
        );

        Assert.Equal(code, ex.EngineError.Code);
        Assert.Equal(profile, ex.EngineError.Arg(EngineErrorArgs.Profile));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void OnlyLoginChangesNeedANewLogin()
    {
        var station = new Station(TwoProfiles().Profiles[1], Filtered);

        Assert.False(
            station.NeedsNewLogin(station with { Profile = station.Profile with { Name = "Renamed", Latitude = 1 } })
        );
        Assert.False(station.NeedsNewLogin(station with { Source = Rotate })); // the same server, another filter
        Assert.True(station.NeedsNewLogin(null));
        Assert.True(station.NeedsNewLogin(station with { Profile = station.Profile with { Id = "x" } }));
        Assert.True(station.NeedsNewLogin(station with { Profile = station.Profile with { Callsign = "N0CALL-8" } }));
        Assert.True(station.NeedsNewLogin(station with { Profile = station.Profile with { Passcode = -1 } }));
        Assert.True(station.NeedsNewLogin(station with { Source = Rotate with { Server = "noam.aprs2.net:14580" } }));
    }

    [Fact]
    public void TheSourcesFilterTextAndTheProfilesRangeAndLocationNeedANewFilter()
    {
        var station = new Station(TwoProfiles().Profiles[1] with { Latitude = 40, Longitude = -105 }, Filtered);

        Assert.True(station.NeedsNewFilter(null));
        Assert.True(station.NeedsNewFilter(station with { Source = Rotate }));
        Assert.True(station.NeedsNewFilter(station with { Profile = station.Profile with { RangeKm = 150 } }));
        Assert.True(station.NeedsNewFilter(station with { Profile = station.Profile with { Latitude = 41 } }));
        Assert.False(station.NeedsNewFilter(station with { Profile = station.Profile with { Name = "Renamed" } }));
    }

    [Theory]
    [InlineData(null, 38.9991481, -94.6818162, "r/38.9991/-94.6818/50")]
    [InlineData("  ", -33.86, 151.2, "r/-33.86/151.2/50")]
    [InlineData(null, null, null, "m/50")]
    [InlineData("b/N0CALL*", 40.0, -105.0, "r/40/-105/50 b/N0CALL*")] // the profile's own text is added
    public void AProfileWithoutAFilterGetsOneAroundItsLocation(
        string? filter,
        double? latitude,
        double? longitude,
        string expected
    )
    {
        var profile = InMemorySettingsStore.Profile("N0CALL") with { Latitude = latitude, Longitude = longitude };

        Assert.Equal(expected, FilterComposer.Compose(profile, filter, []));
    }
}
