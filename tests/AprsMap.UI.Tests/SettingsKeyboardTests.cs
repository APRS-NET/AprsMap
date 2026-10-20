// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Appearance;
using AprsMap.UI.ViewModels;
using AprsMap.UI.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Settings by keyboard alone (docs/features/F0.6-appearance.md, behavior 20 and acceptance test 8).</summary>
public class SettingsKeyboardTests
{
    private static async Task Press(
        Window window,
        PhysicalKey key,
        RawInputModifiers modifiers = RawInputModifiers.None
    )
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
        for (var i = 0; i < 10; i++)
        {
            await Task.Yield();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task EverythingWorksFromTheKeyboard()
    {
        var engine = new FakeEngine();
        var appearance = new AppearanceService(); // not the app's: these changes stay in this test
        var vm = new MainViewModel(engine, new FakeTimeProvider(), appearance: appearance);
        vm.Show(engine.Settings);
        var window = new Window
        {
            Content = new MainView { DataContext = vm },
            Width = 1100,
            Height = 900,
        };
        window.Show();

        await Press(window, PhysicalKey.Comma, RawInputModifiers.Control);
        Assert.NotNull(vm.Settings);
        Assert.Equal("profiles", vm.Settings.SelectedSection?.Id);

        // Back to the section list, down to Appearance, and into its first option.
        for (var i = 0; i < 10 && window.FocusManager?.GetFocusedElement() is not ListBoxItem; i++)
        {
            await Press(window, PhysicalKey.Tab, RawInputModifiers.Shift);
        }
        await Press(window, PhysicalKey.ArrowDown); // Sources
        await Press(window, PhysicalKey.ArrowDown); // Beacons
        await Press(window, PhysicalKey.ArrowDown); // Objects
        await Press(window, PhysicalKey.ArrowDown); // Messaging
        await Press(window, PhysicalKey.ArrowDown);
        Assert.Equal("appearance", vm.Settings.SelectedSection?.Id);
        await Press(window, PhysicalKey.Tab);
        Assert.IsType<ComboBox>(window.FocusManager?.GetFocusedElement()); // Theme

        await Press(window, PhysicalKey.ArrowDown); // Auto → Light
        await Press(window, PhysicalKey.Tab);
        await Press(window, PhysicalKey.ArrowDown); // Standard → High contrast
        Assert.Equal(ThemeMode.Light, appearance.Current.Theme);
        Assert.Equal(Palette.HighContrast, appearance.Current.Palette);

        // On to Reset appearance, then Undo.
        for (var i = 0; i < 30 && window.FocusManager?.GetFocusedElement() is not Button { Name: "ResetButton" }; i++)
        {
            await Press(window, PhysicalKey.Tab);
        }
        Assert.Equal("ResetButton", (window.FocusManager?.GetFocusedElement() as Button)?.Name);
        await Press(window, PhysicalKey.Enter);
        Assert.Equal(new AppearanceSettings(), appearance.Current);
        await Press(window, PhysicalKey.Tab);
        Assert.Equal("UndoButton", (window.FocusManager?.GetFocusedElement() as Button)?.Name);
        await Press(window, PhysicalKey.Space);
        Assert.Equal(ThemeMode.Light, appearance.Current.Theme);
        Assert.Equal(Palette.HighContrast, appearance.Current.Palette);

        await Press(window, PhysicalKey.Escape);
        Assert.Null(vm.Settings);
        window.Close();
    }
}
