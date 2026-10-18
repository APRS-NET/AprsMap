// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Localization;
using AprsMap.UI.Platform;
using AprsMap.UI.Shell;
using Avalonia;
using Avalonia.Headless.XUnit;

namespace AprsMap.UI.Tests;

/// <summary>Saved layouts (docs/features/F1.23-saved-layouts.md, acceptance tests 1–3).</summary>
public class SavedLayoutsTests
{
    private static readonly ScreenArea Laptop = new(
        new PixelRect(0, 0, 1920, 1080),
        new PixelRect(0, 0, 1920, 1040),
        1.25
    );
    private static readonly ScreenArea Monitor = new(
        new PixelRect(1920, 0, 2560, 1440),
        new PixelRect(1920, 0, 2560, 1400),
        1
    );

    private sealed class Setup
    {
        public List<ScreenArea> Screens { get; set; } = [Laptop];
        public ShellViewModel Shell { get; } = new(new object(), Localizer.Instance, managedWindows: true);
        public MemoryStore Store { get; } = new();

        public SavedLayoutsViewModel Create(MemoryStore? store = null)
        {
            var layouts = new SavedLayoutsViewModel(Shell, Localizer.Instance);
            layouts.Attach(store ?? Store, () => Screens, () => null, null);
            return layouts;
        }
    }

    [AvaloniaFact]
    public async Task ALayoutCanBeSavedUsedRenamedUpdatedAndDeletedAndIsKept()
    {
        var setup = new Setup();
        var layouts = setup.Create();
        layouts.NewName = "Laptop";
        await layouts.SaveAsCommand.ExecuteAsync(null);
        setup.Shell.Show(PanelKinds.Console); // a change after saving
        var changed = setup.Shell.Save();

        await layouts.Layouts[0].ApplyCommand.ExecuteAsync(null);
        Assert.NotEqual(changed, setup.Shell.Save()); // back as saved

        layouts.Layouts[0].EditName = "Travel";
        await layouts.Layouts[0].RenameCommand.ExecuteAsync(null);
        layouts.NewName = "Desk";
        await layouts.SaveAsCommand.ExecuteAsync(null);
        Assert.Equal(["Travel", "Desk"], layouts.Layouts.Select(l => l.Name));
        await layouts.Layouts[0].UpdateCommand.ExecuteAsync(null);
        await layouts.Layouts[1].DeleteCommand.ExecuteAsync(null);

        // After a restart.
        var again = setup.Create();
        await again.LoadAsync();
        Assert.Equal(["Travel"], again.Layouts.Select(l => l.Name));
        Assert.Equal(WindowPlacement.Fingerprint([Laptop]), again.Layouts[0].Saved.Screens);
    }

    [AvaloniaFact]
    public async Task ChangingToMonitorsALayoutWasUsedWithAsksAndNeverSwitchesByItself()
    {
        var setup = new Setup { Screens = [Laptop, Monitor] };
        var layouts = setup.Create();
        layouts.NewName = "Desk";
        await layouts.SaveAsCommand.ExecuteAsync(null);
        setup.Screens = [Laptop];
        layouts.ScreensChanged();
        Assert.False(layouts.IsAsking); // nothing saved for the laptop alone

        setup.Shell.Show(PanelKinds.Console);
        var before = setup.Shell.Save();
        setup.Screens = [Laptop, Monitor];
        layouts.ScreensChanged();
        Assert.Equal("Monitors changed. Use layout Desk?", layouts.PromptText);
        Assert.Equal(before, setup.Shell.Save()); // not switched

        layouts.KeepCurrentCommand.Execute(null);
        Assert.False(layouts.IsAsking);
        Assert.Equal(before, setup.Shell.Save());

        setup.Screens = [Laptop];
        layouts.ScreensChanged();
        setup.Screens = [Laptop, Monitor];
        layouts.ScreensChanged();
        Assert.True(layouts.IsAsking);
        await layouts.UsePromptedCommand.ExecuteAsync(null);
        Assert.NotEqual(before, setup.Shell.Save());
        Assert.False(layouts.IsAsking);
    }

    [AvaloniaFact]
    public async Task DontAskAgainStopsTheQuestionForThatSetupOnlyAndAskingCanBeTurnedOff()
    {
        var setup = new Setup { Screens = [Laptop, Monitor] };
        var layouts = setup.Create();
        layouts.NewName = "Desk";
        await layouts.SaveAsCommand.ExecuteAsync(null);
        setup.Screens = [Monitor];
        layouts.NewName = "Monitor";
        await layouts.SaveAsCommand.ExecuteAsync(null);

        setup.Screens = [Laptop, Monitor];
        layouts.ScreensChanged();
        await layouts.DontAskAgainCommand.ExecuteAsync(null);
        setup.Screens = [Laptop];
        layouts.ScreensChanged();
        setup.Screens = [Laptop, Monitor];
        layouts.ScreensChanged();
        Assert.False(layouts.IsAsking); // not for this setup again, even after a restart:
        var again = setup.Create();
        await again.LoadAsync();
        again.ScreensChanged();
        Assert.False(again.IsAsking);

        setup.Screens = [Monitor];
        again.ScreensChanged();
        Assert.Equal("Monitors changed. Use layout Monitor?", again.PromptText); // another setup still asks

        again.KeepCurrentCommand.Execute(null);
        again.Ask = false;
        setup.Screens = [Laptop];
        again.ScreensChanged();
        setup.Screens = [Monitor];
        var third = setup.Create();
        await third.LoadAsync();
        third.ScreensChanged();
        Assert.False(third.IsAsking);
    }

    [AvaloniaFact]
    public async Task StartingWithTheSameMonitorsAsLastTimeAsksNothingAndDifferentOnesAsk()
    {
        var setup = new Setup { Screens = [Laptop, Monitor] };
        var layouts = setup.Create();
        layouts.NewName = "Desk";
        await layouts.SaveAsCommand.ExecuteAsync(null);
        layouts.ScreensChanged(); // seen with the desk's monitors

        var sameDesk = setup.Create();
        await sameDesk.LoadAsync();
        sameDesk.ScreensChanged();
        Assert.False(sameDesk.IsAsking);

        setup.Screens = [Laptop];
        sameDesk.ScreensChanged(); // closed on the laptop
        setup.Screens = [Laptop, Monitor];
        var backAtDesk = setup.Create();
        await backAtDesk.LoadAsync();
        backAtDesk.ScreensChanged();
        Assert.Equal("Monitors changed. Use layout Desk?", backAtDesk.PromptText);
    }

    [AvaloniaFact]
    public async Task SavedLayoutsThatCantBeReadAreSetAsideWithANote()
    {
        var setup = new Setup();
        await setup.Store.SaveAsync(SavedLayoutsViewModel.Key, "{not json");
        var layouts = setup.Create();

        await layouts.LoadAsync();

        Assert.Equal("The saved layouts couldn't be read, so they were set aside.", layouts.Notice);
        Assert.Empty(layouts.Layouts);
        Assert.True(setup.Store.SetAside);
    }

    internal sealed class MemoryStore : IPreferenceStore
    {
        private readonly Dictionary<string, string> _values = [];

        public bool SetAside { get; private set; }

        public Task<string?> LoadAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.GetValueOrDefault(key));

        public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task SetAsideAsync(string key, CancellationToken cancellationToken = default)
        {
            SetAside = true;
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
