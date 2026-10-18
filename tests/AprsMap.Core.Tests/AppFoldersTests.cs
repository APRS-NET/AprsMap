// SPDX-License-Identifier: MIT
using AprsMap.Core;
using static AprsMap.Core.AppFolders;

namespace AprsMap.Core.Tests;

public sealed class AppFoldersTests : IDisposable
{
    private static readonly char S = Path.DirectorySeparatorChar;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "aprsmap-folders-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string P(string path) => path.Replace('/', S);

    private static FolderEnvironment Env(OSPlatform platform, Dictionary<string, string>? variables = null) =>
        new(
            platform,
            P("/home/me"),
            P("/roaming"),
            P("/local"),
            P("/programdata"),
            name => variables is not null && variables.TryGetValue(name, out var value) ? value : null
        );

    [Fact]
    public void WindowsKeepsSettingsInRoamingAndWhatGrowsInLocalAppData()
    {
        var folders = For("aprsmap", Env(OSPlatform.Windows));

        Assert.Equal(P("/roaming/aprsnet/aprsmap"), folders.Settings);
        Assert.Equal(P("/local/aprsnet/aprsmap"), folders.Data);
        Assert.Equal(P("/local/aprsnet/aprsmap"), folders.Cache);
        Assert.Equal([P("/programdata/aprsnet/aprsmap")], folders.Machine);
    }

    [Fact]
    public void MacOsUsesApplicationSupportAndCaches()
    {
        var folders = For("aprsmap", Env(OSPlatform.MacOS));

        Assert.Equal(P("/home/me/Library/Application Support/aprsnet/aprsmap"), folders.Settings);
        Assert.Equal(folders.Settings, folders.Data);
        Assert.Equal(P("/home/me/Library/Caches/aprsnet/aprsmap"), folders.Cache);
        Assert.Equal([P("/Library/Application Support/aprsnet/aprsmap")], folders.Machine);
    }

    [Fact]
    public void LinuxFollowsTheXdgDefaults()
    {
        var folders = For("aprsmap", Env(OSPlatform.Other));

        Assert.Equal(P("/home/me/.config/aprsnet/aprsmap"), folders.Settings);
        Assert.Equal(P("/home/me/.local/share/aprsnet/aprsmap"), folders.Data);
        Assert.Equal(P("/home/me/.cache/aprsnet/aprsmap"), folders.Cache);
        Assert.Equal([P("/etc/xdg/aprsnet/aprsmap")], folders.Machine);
    }

    [Fact]
    public void LinuxHonoursTheXdgVariablesButIgnoresRelativeOnes()
    {
        var folders = For(
            "aprsmap",
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

        Assert.Equal(P("/cfg/aprsnet/aprsmap"), folders.Settings);
        Assert.Equal(P("/home/me/.local/share/aprsnet/aprsmap"), folders.Data);
        Assert.Equal(P("/home/me/.cache/aprsnet/aprsmap"), folders.Cache);
        Assert.Equal([P("/opt/xdg/aprsnet/aprsmap"), P("/etc/xdg/aprsnet/aprsmap")], folders.Machine);
    }

    [Fact]
    public void EarlierVersionsUsedOneFolderOnLinuxAndRoamingOnWindows()
    {
        Assert.Equal(P("/home/me/.aprsnet/aprsmap"), Legacy("aprsmap", Env(OSPlatform.Other)));
        Assert.Equal(P("/roaming/aprsnet/aprsmap"), Legacy("aprsmap", Env(OSPlatform.Windows)));
        Assert.Null(Legacy("aprsmap", Env(OSPlatform.MacOS)));
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
        var legacy = Path.Combine(_root, ".aprsnet", "aprsmap");
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
