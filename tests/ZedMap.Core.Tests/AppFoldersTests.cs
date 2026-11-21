// SPDX-License-Identifier: MIT
using ZedMap.Core;
using static ZedMap.Core.AppFolders;

namespace ZedMap.Core.Tests;

public sealed class AppFoldersTests : IDisposable
{
    private static readonly char S = Path.DirectorySeparatorChar;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "zedmap-folders-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string P(string path) => path.Replace('/', S);

    private static FolderEnvironment Env(OSPlatform platform, Dictionary<string, string>? variables = null) =>
        new(platform, P("/home/me"), P("/roaming"), P("/local"), P("/programdata"), name =>
            variables is not null && variables.TryGetValue(name, out var value) ? value : null
        );

    [Fact]
    public void WindowsKeepsSettingsInRoamingAndWhatGrowsInLocalAppData()
    {
        var folders = For("zedmap", Env(OSPlatform.Windows));

        Assert.Equal(P("/roaming/aprsnet/zedmap"), folders.Settings);
        Assert.Equal(P("/local/aprsnet/zedmap"), folders.Data);
        Assert.Equal(P("/local/aprsnet/zedmap"), folders.Cache);
        Assert.Equal([P("/programdata/aprsnet/zedmap")], folders.Machine);
    }

    [Fact]
    public void MacOsUsesApplicationSupportAndCaches()
    {
        var folders = For("zedmap", Env(OSPlatform.MacOS));

        Assert.Equal(P("/home/me/Library/Application Support/aprsnet/zedmap"), folders.Settings);
        Assert.Equal(folders.Settings, folders.Data);
        Assert.Equal(P("/home/me/Library/Caches/aprsnet/zedmap"), folders.Cache);
        Assert.Equal([P("/Library/Application Support/aprsnet/zedmap")], folders.Machine);
    }

    [Fact]
    public void LinuxFollowsTheXdgDefaults()
    {
        var folders = For("zedmap", Env(OSPlatform.Other));

        Assert.Equal(P("/home/me/.config/aprsnet/zedmap"), folders.Settings);
        Assert.Equal(P("/home/me/.local/share/aprsnet/zedmap"), folders.Data);
        Assert.Equal(P("/home/me/.cache/aprsnet/zedmap"), folders.Cache);
        Assert.Equal([P("/etc/xdg/aprsnet/zedmap")], folders.Machine);
    }

    [Fact]
    public void LinuxHonoursTheXdgVariablesButIgnoresRelativeOnes()
    {
        var folders = For(
            "zedmap",
            Env(
                OSPlatform.Other,
                new()
                {
                    ["XDG_CONFIG_HOME"] = P("/cfg"),
                    ["XDG_DATA_HOME"] = "relative/data",
                    ["XDG_CACHE_HOME"] = "",
                    ["XDG_CONFIG_DIRS"] = $"{P("/opt/xdg")}:relative:{P("/etc/xdg")}",
                }
            )
        );

        Assert.Equal(P("/cfg/aprsnet/zedmap"), folders.Settings);
        Assert.Equal(P("/home/me/.local/share/aprsnet/zedmap"), folders.Data);
        Assert.Equal(P("/home/me/.cache/aprsnet/zedmap"), folders.Cache);
        Assert.Equal([P("/opt/xdg/aprsnet/zedmap"), P("/etc/xdg/aprsnet/zedmap")], folders.Machine);
    }

    [Fact]
    public void EarlierVersionsUsedOneFolderOnLinuxAndRoamingOnWindows()
    {
        Assert.Equal(P("/home/me/.aprsnet/zedmap"), Legacy("zedmap", Env(OSPlatform.Other)));
        Assert.Equal(P("/roaming/aprsnet/zedmap"), Legacy("zedmap", Env(OSPlatform.Windows)));
        Assert.Null(Legacy("zedmap", Env(OSPlatform.MacOS)));
    }

    [Fact]
    public void CreateMakesTheFoldersPrivateToTheirOwner()
    {
        var folders = new AppFolders(
            Path.Combine(_root, "config"),
            Path.Combine(_root, "data"),
            Path.Combine(_root, "cache"),
            []
        );

        folders.Create();

        Assert.All([folders.Settings, folders.Data, folders.Cache], f => Assert.True(Directory.Exists(f)));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(folders.Settings)
            );
        }
    }

    [Fact]
    public async Task AnEarlierVersionsFolderIsMovedAndThenRemoved()
    {
        var ct = TestContext.Current.CancellationToken;
        var legacy = Path.Combine(_root, ".aprsnet", "zedmap");
        Directory.CreateDirectory(Path.Combine(legacy, "capture"));
        await File.WriteAllTextAsync(Path.Combine(legacy, "settings.json"), "{}", ct);
        await File.WriteAllTextAsync(Path.Combine(legacy, "layout.json"), "{}", ct);
        await File.WriteAllTextAsync(Path.Combine(legacy, "messages-home.json"), "[]", ct);
        await File.WriteAllTextAsync(Path.Combine(legacy, "capture", "2026-10-18.log"), "x", ct);
        var folders = new AppFolders(
            Path.Combine(_root, "config"),
            Path.Combine(_root, "data"),
            Path.Combine(_root, "cache"),
            []
        );

        Assert.Equal(4, folders.MoveFromLegacy(legacy));

        Assert.True(File.Exists(Path.Combine(folders.Settings, "settings.json")));
        Assert.True(File.Exists(Path.Combine(folders.Settings, "layout.json")));
        Assert.True(File.Exists(Path.Combine(folders.Data, "messages-home.json")));
        Assert.True(File.Exists(Path.Combine(folders.Data, "capture", "2026-10-18.log")));
        Assert.False(Directory.Exists(Path.Combine(_root, ".aprsnet"))); // emptied, so gone with its vendor folder
        Assert.Equal(0, folders.MoveFromLegacy(legacy));
    }

    private AppFolders Under(string name) =>
        new(
            Path.Combine(_root, name, "config"),
            Path.Combine(_root, name, "data"),
            Path.Combine(_root, name, "cache"),
            []
        );

    [Fact]
    public async Task APreviousNamesFilesAreCopiedOnceAndLeftInPlace()
    {
        // docs/branding.md, rule 5: after a rename, the desktop app starts under its new name with everything the old
        // one had (not only settings.json), and the old version can still run from its folders.
        var ct = TestContext.Current.CancellationToken;
        var old = Under("oldname");
        old.Create();
        await File.WriteAllTextAsync(Path.Combine(old.Settings, "settings.json"), "old settings", ct);
        await File.WriteAllTextAsync(Path.Combine(old.Settings, "layout.json"), "{}", ct);
        await File.WriteAllTextAsync(Path.Combine(old.Data, "messages-home.json"), "[]", ct);
        Directory.CreateDirectory(Path.Combine(old.Data, "history"));
        await File.WriteAllTextAsync(Path.Combine(old.Data, "history", "2026-11-20.db"), "packets", ct);
        await File.WriteAllTextAsync(Path.Combine(old.Cache, "tile.png"), "x", ct);
        var current = Under("newname");

        var prepared = SettingsLocation.Prepare(current, legacy: null, [Under("oldest"), old]);

        Assert.Equal(current, prepared);
        Assert.Equal("old settings", await File.ReadAllTextAsync(Path.Combine(current.Settings, "settings.json"), ct));
        Assert.True(File.Exists(Path.Combine(current.Settings, "layout.json")));
        Assert.True(File.Exists(Path.Combine(current.Data, "messages-home.json")));
        Assert.True(File.Exists(Path.Combine(current.Data, "history", "2026-11-20.db")));
        Assert.False(File.Exists(Path.Combine(current.Cache, "tile.png"))); // fetched again
        Assert.True(File.Exists(Path.Combine(old.Settings, "settings.json"))); // left as it was
        Assert.True(File.Exists(Path.Combine(old.Data, "history", "2026-11-20.db")));

        // Once the new name has settings, nothing is copied again, even what the old one has since.
        await File.WriteAllTextAsync(Path.Combine(old.Settings, "later.json"), "{}", ct);
        Assert.False(current.CopyFromPreviousName(old));
        Assert.False(File.Exists(Path.Combine(current.Settings, "later.json")));
    }

    [Fact]
    public async Task APreviousNameWithoutSettingsIsNotCopiedFrom()
    {
        var ct = TestContext.Current.CancellationToken;
        var old = Under("oldname");
        old.Create();
        await File.WriteAllTextAsync(Path.Combine(old.Settings, "layout.json"), "{}", ct);
        var current = Under("newname");
        current.Create();

        Assert.False(current.CopyFromPreviousName(old));
        Assert.False(File.Exists(Path.Combine(current.Settings, "layout.json")));
    }

    [Fact]
    public async Task NothingAlreadyInPlaceIsReplaced()
    {
        var ct = TestContext.Current.CancellationToken;
        var legacy = Path.Combine(_root, "old");
        Directory.CreateDirectory(legacy);
        await File.WriteAllTextAsync(Path.Combine(legacy, "settings.json"), "old", ct);
        var folders = new AppFolders(
            Path.Combine(_root, "config"),
            Path.Combine(_root, "data"),
            Path.Combine(_root, "cache"),
            []
        );
        folders.Create();
        await File.WriteAllTextAsync(Path.Combine(folders.Settings, "settings.json"), "new", ct);

        Assert.Equal(0, folders.MoveFromLegacy(legacy));

        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(folders.Settings, "settings.json"), ct));
        Assert.True(File.Exists(Path.Combine(legacy, "settings.json"))); // left, since it wasn't moved
    }

    [Fact]
    public async Task OnWindowsOnlyMessagesAndCapturesLeaveTheRoamingFolder()
    {
        var ct = TestContext.Current.CancellationToken;
        var roaming = Path.Combine(_root, "roaming");
        Directory.CreateDirectory(Path.Combine(roaming, "capture"));
        await File.WriteAllTextAsync(Path.Combine(roaming, "settings.json"), "{}", ct);
        await File.WriteAllTextAsync(Path.Combine(roaming, "messages-home.json"), "[]", ct);
        var folders = new AppFolders(roaming, Path.Combine(_root, "local"), Path.Combine(_root, "local"), []);

        Assert.Equal(2, folders.MoveFromLegacy(roaming));

        Assert.True(File.Exists(Path.Combine(roaming, "settings.json")));
        Assert.False(File.Exists(Path.Combine(roaming, "messages-home.json")));
        Assert.True(File.Exists(Path.Combine(folders.Data, "messages-home.json")));
        Assert.True(Directory.Exists(Path.Combine(folders.Data, "capture")));
    }

    [Fact]
    public void AGatewaysVolumeHoldsItsDataUnlessToldOtherwise()
    {
        Assert.Equal("/data", SettingsLocation.DataFolder(new EngineOptions { SettingsFolder = "/data" }));
        Assert.Equal(
            "/elsewhere",
            SettingsLocation.DataFolder(new EngineOptions { SettingsFolder = "/data", DataFolder = "/elsewhere" })
        );
    }
}
