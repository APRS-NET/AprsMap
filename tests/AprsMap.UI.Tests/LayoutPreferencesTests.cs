// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Platform;
using AprsMap.UI.Shell;
using AprsMap.UI.ViewModels;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Saving the layout (docs/features/F1.0-shell-and-docking.md, behaviors 8 and 10).</summary>
public sealed class LayoutPreferencesTests
{
    [Fact]
    public async Task ChangesAreSavedOnceTheyPause()
    {
        var store = new MemoryStore();
        var time = new FakeTimeProvider();
        using var layout = new LayoutPreferences(store, time);
        var writes = 0;

        layout.Schedule(() => $"first {++writes}");
        time.Advance(TimeSpan.FromMilliseconds(500));
        layout.Schedule(() => $"second {++writes}");
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Empty(store.Saves);
        time.Advance(LayoutPreferences.SaveDelay);
        await WaitUntil(() => store.Saves.Count == 1);

        Assert.Equal("second 1", Assert.Single(store.Saves)); // only the last change is written
        Assert.Equal("second 1", await layout.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FlushWritesWhatIsWaitingAndAnUnreadableLayoutIsSetAside()
    {
        var store = new MemoryStore();
        using var layout = new LayoutPreferences(store, new FakeTimeProvider());

        layout.Schedule(() => "on exit");
        await layout.FlushAsync();
        await layout.SetAsideAsync();

        Assert.Equal(["on exit"], store.Saves);
        Assert.Equal([LayoutPreferences.Key], store.SetAside);
    }

    [AvaloniaFact]
    public void AnUnreadableLayoutIsSaidOnceAndCanBeDismissed()
    {
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider());

        Assert.Equal(LayoutRestore.Unreadable, vm.RestoreLayout("{ not json", []));
        Assert.Equal("Your saved panel layout couldn't be read, so the default layout is shown.", vm.LayoutNotice);
        vm.DismissLayoutNoticeCommand.Execute(null);

        Assert.Null(vm.LayoutNotice);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
        }
        Assert.True(condition(), "Condition was not met in time.");
    }

    private sealed class MemoryStore : IPreferenceStore
    {
        private readonly Dictionary<string, string> _values = [];

        public List<string> Saves { get; } = [];

        public List<string> SetAside { get; } = [];

        public Task<string?> LoadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.GetValueOrDefault(key));

        public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            Saves.Add(value);
            return Task.CompletedTask;
        }

        public Task SetAsideAsync(string key, CancellationToken cancellationToken = default)
        {
            SetAside.Add(key);
            return Task.CompletedTask;
        }
    }
}
