// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>Where files are kept (docs/features/F1.31-files.md, behaviors 1, 2).</summary>
public sealed class StorageFolderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheEnginesFoldersAndTheOthersAreListedInOrderSayingWhatsConfiguredOrMissing()
    {
        // Never the real user folders: a temp folder, created for settings only.
        var root = Path.Combine(Path.GetTempPath(), "aprsmap-folders-" + Guid.NewGuid().ToString("N"));
        var settings = Path.Combine(root, "settings");
        Directory.CreateDirectory(settings);
        try
        {
            var logs = new StorageFolderSource(() =>
                [StorageFolderSource.At(StorageFolderKind.Logs, Path.Combine(root, "logs"), false)]
            );
            var tiles = new StorageFolderSource(() => [StorageFolderSource.At(StorageFolderKind.Tiles, root, true)]);
            using var engine = new Engine(
                Options.Create(new EngineOptions { SettingsFolder = settings }),
                new EngineTests.FakeSourceFactory(),
                new InMemorySettingsStore(),
                new FakeTimeProvider(),
                NullLogger<Engine>.Instance,
                folders: [logs, tiles]
            );

            var folders = await engine.GetStorageFoldersAsync(Ct);

            Assert.Equal(
                [StorageFolderKind.Settings, StorageFolderKind.Data, StorageFolderKind.Tiles, StorageFolderKind.Logs],
                folders.Select(f => f.Kind)
            );
            Assert.Equal((settings, true, true), (folders[0].Path, folders[0].Configured, folders[0].Exists));
            Assert.Equal(settings, folders[1].Path); // data beside the settings when only they are set
            Assert.True(folders[2].Configured);
            Assert.False(folders[3].Exists); // not created yet
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
