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

    private static AppSettings TwoProfiles()
    {
        var home = InMemorySettingsStore.Profile("N0CALL", "home") with { Latitude = 40.0, Longitude = -105.0 };
        var portable = InMemorySettingsStore.Profile("N0CALL-7", "portable") with
        {
            Name = "Portable",
            Passcode = 12345,
            Filter = "r/40/-105/50",
        };
        return new AppSettings([home, portable], "portable", UnitSettings.Imperial);
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

    [Theory]
    [InlineData("Windows", "/appdata/aprsnet/aprsmap")]
    [InlineData("MacOS", "/home/Library/Application Support/aprsnet/aprsmap")]
    [InlineData("Other", "/home/.aprsnet/aprsmap")]
    public void EachPlatformHasItsOwnPerUserFolder(string platform, string expected) =>
        Assert.Equal(
            expected.Replace('/', Path.DirectorySeparatorChar),
            SettingsLocation.UserFolder(
                "aprsmap",
                Enum.Parse<SettingsLocation.OSPlatform>(platform),
                $"{Path.DirectorySeparatorChar}home",
                $"{Path.DirectorySeparatorChar}appdata"
            )
        );

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
    public void ValidSettingsPass()
    {
        SettingsValidator.Validate(TwoProfiles(), TimeProvider.System);
        SettingsValidator.Validate(AppSettings.Empty, TimeProvider.System);
    }

    public static TheoryData<string, AppSettings> InvalidSettings()
    {
        var good = TwoProfiles();
        var home = good.Profiles[0];
        return new()
        {
            { "own id", good with { Profiles = [home, home with { Name = "Other" }] } },
            { "needs a name", good with { Profiles = [home with { Name = " " }], ActiveProfileId = "home" } },
            {
                "more than one profile named",
                good with
                {
                    Profiles = [home, good.Profiles[1] with { Name = "n0call" }],
                }
            },
            { "off the globe", good with { Profiles = [home with { Latitude = 91 }], ActiveProfileId = "home" } },
            {
                "both latitude and longitude",
                good with
                {
                    Profiles = [home with { Longitude = null }],
                    ActiveProfileId = "home",
                }
            },
            {
                "not a valid APRS-IS server",
                good with
                {
                    Profiles = [home with { Server = "bad host:x" }],
                    ActiveProfileId = "home",
                }
            },
            { "Profile 'N0CALL'", good with { Profiles = [home with { Callsign = "" }], ActiveProfileId = "home" } },
            { "active profile", good with { ActiveProfileId = "nobody" } },
            { "active profile", AppSettings.Empty with { ActiveProfileId = "home" } },
            { "incomplete", good with { Units = null! } },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public void InvalidSettingsAreRejectedWithAReason(string reason, AppSettings settings)
    {
        var ex = Assert.Throws<ArgumentException>(() => SettingsValidator.Validate(settings, TimeProvider.System));

        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyLoginAndFilterChangesNeedANewConnection()
    {
        var profile = TwoProfiles().Profiles[1];

        Assert.False(profile.ConnectsDifferentlyFrom(profile with { Name = "Renamed", Latitude = 1, Longitude = 2 }));
        Assert.True(profile.ConnectsDifferentlyFrom(null));
        Assert.True(profile.ConnectsDifferentlyFrom(profile with { Id = "x" }));
        Assert.True(profile.ConnectsDifferentlyFrom(profile with { Callsign = "N0CALL-8" }));
        Assert.True(profile.ConnectsDifferentlyFrom(profile with { Passcode = -1 }));
        Assert.True(profile.ConnectsDifferentlyFrom(profile with { Server = "noam.aprs2.net:14580" }));
        Assert.True(profile.ConnectsDifferentlyFrom(profile with { Filter = null }));
    }
}
