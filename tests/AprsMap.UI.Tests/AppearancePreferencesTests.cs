// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Appearance;
using AprsMap.UI.Platform;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

public sealed class AppearancePreferencesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "aprsmap-prefs-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static readonly AppearanceSettings Everything = new()
    {
        Theme = ThemeMode.Dark,
        Palette = Palette.ColourVisionSafe,
        TextScale = 1.3,
        Icons = IconSize.Large,
        IconsFollowText = false,
        Corners = CornerStyle.Sharp,
        Density = Density.Compact,
        Motion = MotionPreference.Reduced,
        MapSymbolScale = 1.5,
        Language = "de",
    };

    [Fact]
    public void EverySettingSurvivesARoundTrip()
    {
        var text = AppearancePreferences.Write(Everything);

        Assert.Equal(Everything, AppearancePreferences.Read(text));
        Assert.Contains("\"palette\": \"ColourVisionSafe\"", text, StringComparison.Ordinal);
        Assert.Equal(new AppearanceSettings(), AppearancePreferences.Read(AppearancePreferences.Write(new())));
    }

    [Fact]
    public void ValuesItDoesntKnowAreSkippedOneByOne()
    {
        var read = AppearancePreferences.Read(
            """
            { "version": 9, "theme": "Purple", "palette": "HighContrast", "textScale": "big", "density": "7",
              "corners": "Sharp", "futureOption": { "x": 1 }, "iconsFollowText": "yes", "language": 5 }
            """
        );

        Assert.Equal(new AppearanceSettings { Palette = Palette.HighContrast, Corners = CornerStyle.Sharp }, read);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("null")]
    public async Task AnUnreadableFileGivesDefaultsAndIsSetAside(string content)
    {
        var store = new FilePreferenceStore(_folder);
        await store.SaveAsync(AppearancePreferences.Key, content, Ct);

        var loaded = await new AppearancePreferences(store, TimeProvider.System).LoadAsync(Ct);

        Assert.Equal(new AppearanceSettings(), loaded);
        Assert.False(File.Exists(store.PathOf(AppearancePreferences.Key)));
        Assert.Equal(content, await File.ReadAllTextAsync(store.PathOf(AppearancePreferences.Key) + ".invalid", Ct));
    }

    [Fact]
    public async Task NothingStoredOrAStoreThatFailsGivesDefaults()
    {
        Assert.Equal(
            new AppearanceSettings(),
            await new AppearancePreferences(new FilePreferenceStore(_folder), TimeProvider.System).LoadAsync(Ct)
        );
        Assert.Equal(
            new AppearanceSettings(),
            await new AppearancePreferences(new BrokenStore(), TimeProvider.System).LoadAsync(Ct)
        );
    }

    [Fact]
    public async Task ABurstOfChangesIsSavedOnceAfterAPause()
    {
        var time = new FakeTimeProvider();
        var store = new MemoryStore();
        using var preferences = new AppearancePreferences(store, time);

        preferences.Schedule(new AppearanceSettings { TextScale = 1.15 });
        time.Advance(TimeSpan.FromMilliseconds(200));
        preferences.Schedule(new AppearanceSettings { TextScale = 1.3 });
        time.Advance(TimeSpan.FromMilliseconds(200));
        preferences.Schedule(Everything);
        time.Advance(TimeSpan.FromMilliseconds(499));
        Assert.Empty(store.Saves);

        time.Advance(TimeSpan.FromMilliseconds(1));
        for (var i = 0; i < 50 && store.Saves.Count == 0; i++)
        {
            await Task.Yield();
        }

        var saved = Assert.Single(store.Saves);
        Assert.Equal(Everything, AppearancePreferences.Read(saved));
        Assert.Equal(Everything, await new AppearancePreferences(store, time).LoadAsync(Ct));
    }

    [Fact]
    public async Task FlushSavesWhatIsWaitingAndAFailedSaveIsHarmless()
    {
        var store = new MemoryStore();
        using var preferences = new AppearancePreferences(store, new FakeTimeProvider());

        await preferences.FlushAsync(); // nothing waiting
        Assert.Empty(store.Saves);
        preferences.Schedule(Everything);
        await preferences.FlushAsync();
        Assert.Single(store.Saves);

        using var broken = new AppearancePreferences(new BrokenStore(), new FakeTimeProvider());
        broken.Schedule(Everything);
        await broken.FlushAsync(); // private browsing: nowhere to save, no error
    }

    [Fact]
    public async Task TheFileStoreWritesWholeFilesAndReportsNothingForMissingOnes()
    {
        var store = new FilePreferenceStore(_folder);
        Assert.Null(await store.LoadAsync("appearance", Ct));
        await store.SetAsideAsync("appearance", Ct); // nothing to set aside

        await store.SaveAsync("appearance", "{\"theme\":\"Dark\"}", Ct);
        await store.SaveAsync("appearance", "{\"theme\":\"Light\"}", Ct);

        Assert.Equal("{\"theme\":\"Light\"}", await store.LoadAsync("appearance", Ct));
        Assert.Equal(["appearance.json"], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task AFileStoreThatCantWriteKeepsTheAppRunning()
    {
        // A file where the folder should be: every write fails.
        await File.WriteAllTextAsync(_folder, "not a folder", Ct);
        try
        {
            var store = new FilePreferenceStore(_folder);
            await store.SaveAsync("appearance", "{}", Ct);
            Assert.Null(await store.LoadAsync("appearance", Ct));
            await store.SetAsideAsync("appearance", Ct);
        }
        finally
        {
            File.Delete(_folder);
        }
    }

    private sealed class MemoryStore : IPreferenceStore
    {
        private readonly Dictionary<string, string> _values = [];

        public List<string> Saves { get; } = [];

        public Task<string?> LoadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.GetValueOrDefault(key));

        public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            Saves.Add(value);
            return Task.CompletedTask;
        }

        public Task SetAsideAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class BrokenStore : IPreferenceStore
    {
        public Task<string?> LoadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromException<string?>(new InvalidOperationException("storage refused"));

        public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("storage refused"));

        public Task SetAsideAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
