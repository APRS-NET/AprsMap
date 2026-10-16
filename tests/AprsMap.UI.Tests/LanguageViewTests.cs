// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.ViewModels;
using AprsMap.UI.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Changes the app-wide language, so these tests run alone, after the parallel ones.</summary>
[CollectionDefinition(nameof(LanguageViewTests), DisableParallelization = true)]
public sealed class LanguageTestsDefinition;

[Collection(nameof(LanguageViewTests))]
public sealed class LanguageViewTests : IDisposable
{
    public void Dispose() => Localizer.Instance.SetLanguage(null);

    private static (Window Window, MainViewModel ViewModel) Show()
    {
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        vm.Show(engine.Settings);
        vm.Show(new EngineStatus(ConnectionState.Connected, "T2TEST", 2, "1.0.0"));
        vm.Apply(new StationBatch([FakeEngine.Station("N0CALL")], []));
        vm.ShowError(EngineErrors.Create(EngineErrorCodes.SettingsReadOnly, "English"));
        var window = new Window
        {
            Content = new MainView { DataContext = vm },
            Width = 1600,
            Height = 600,
        };
        window.Show();
        return (window, vm);
    }

    private static IEnumerable<TextBlock> Labels(Window window) =>
        window
            .GetLogicalDescendants()
            .OfType<TextBlock>()
            .Where(t => t.Name is "ConnectionLabel" or "PacketCountLabel" or "StationCountLabel" or "ErrorLabel");

    [AvaloniaFact]
    public void InThePseudoLanguageEveryTextIsTranslatedAndFits()
    {
        Localizer.Instance.SetLanguage(Localizer.PseudoLanguage);
        var (window, _) = Show();

        var labels = Labels(window).ToList();
        Assert.Equal(4, labels.Count);
        Assert.All(
            labels,
            label =>
            {
                Assert.StartsWith("[", label.Text, StringComparison.Ordinal);
                Assert.EndsWith("]", label.Text, StringComparison.Ordinal);
                // Laid out in full: the closing bracket is on screen, not cut off.
                Assert.True(label.DesiredSize.Width <= label.Bounds.Width + 0.5, $"{label.Name} is clipped");
            }
        );
        var picker = window.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "ProfilePicker");
        Assert.StartsWith("[Šţåţîöñ", ToolTip.GetTip(picker) as string, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public async Task InThePseudoLanguageTheWizardIsTranslatedAndFits()
    {
        Localizer.Instance.SetLanguage(Localizer.PseudoLanguage);
        var engine = new FakeEngine { Settings = AppSettings.Empty };
        var vm = new MainViewModel(engine, new FakeTimeProvider());
        var window = new Window
        {
            Content = new MainView { DataContext = vm },
            Width = 640,
            Height = 900,
        };
        window.Show();
        vm.Show(engine.Settings);
        for (var i = 0; i < 50 && vm.Setup is null; i++)
        {
            await Task.Yield();
        }
        var wizard = vm.Setup!;
        wizard.Callsign = "AB1CD";

        foreach (var step in Enum.GetValues<SetupStep>())
        {
            wizard.Step = step;
            wizard.IsCalculatorOpen = step == SetupStep.Passcode;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var texts = window
                .GetLogicalDescendants()
                .OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text) && t.Text.Any(char.IsLetter))
                .Where(t => t.Text != "AB1CD" && t.Text != Brand.DisplayName) // data, not UI text
                .ToList();
            Assert.NotEmpty(texts);
            Assert.All(texts, t => Assert.True(t.Text!.StartsWith('[') && t.Text.EndsWith(']'), $"{step}: {t.Text}"));
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheLanguageOptionSwitchesTheWholeAppLive()
    {
        var engine = new FakeEngine();
        var appearance = new AprsMap.UI.Appearance.AppearanceService();
        var vm = new MainViewModel(engine, new FakeTimeProvider(), appearance: appearance, showPseudoLanguage: true);
        vm.Show(engine.Settings);
        vm.Show(new EngineStatus(ConnectionState.Connected, "T2TEST", 2, "1.0.0"));
        var window = new Window
        {
            Content = new MainView { DataContext = vm },
            Width = 1100,
            Height = 800,
        };
        window.Show();
        await vm.OpenSettingsCommand.ExecuteAsync(null);
        var language = Assert.IsType<AprsMap.UI.Settings.ChoiceSetting>(
            vm.Settings!.AppearanceItems.Single(i => i.Id == "appearance.language")
        );

        language.Selected = language.Options.Single(o => Equals(o.Value, Localizer.PseudoLanguage));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(Localizer.PseudoLanguage, appearance.Current.Language);
        Assert.True(Localizer.Instance.IsPseudo);
        Assert.StartsWith("[", vm.ConnectionText, StringComparison.Ordinal);
        foreach (var section in vm.Settings.Sections.ToList())
        {
            vm.Settings.SelectedSection = vm.Settings.Sections.Single(s => s.Id == section.Id);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var texts = window
                .GetLogicalDescendants()
                .OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text) && t.Text.Any(char.IsLetter))
                .Where(t =>
                    t.Text is not ("N0CALL" or "N0CALL-7" or "Home" or "Portable")
                    && t.Text != Brand.DisplayName
                    && !t.Text!.StartsWith("rotate.aprs2.net", StringComparison.Ordinal)
                ) // data: callsigns, profile and source names, servers
                .ToList();
            Assert.NotEmpty(texts);
            Assert.All(
                texts,
                t => Assert.True(t.Text!.StartsWith('[') || t.Text == "English", $"{section.Id}: {t.Text}")
            );
        }

        language.Selected = language.Options[0]; // System default
        Assert.False(Localizer.Instance.IsPseudo);
        window.Close();
    }

    [AvaloniaFact]
    public void SwitchingLanguageUpdatesTheOpenWindow()
    {
        var (window, _) = Show();
        var connection = Labels(window).Single(l => l.Name == "ConnectionLabel");
        var picker = window.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "ProfilePicker");
        Assert.Equal("APRS-IS connected · T2TEST", connection.Text);
        Assert.Equal("Station profile", ToolTip.GetTip(picker));

        Localizer.Instance.SetLanguage(Localizer.PseudoLanguage);
        Assert.StartsWith("[ÅÞŔŠ-ÎŠ", connection.Text, StringComparison.Ordinal);
        Assert.StartsWith("[Šţåţîöñ", ToolTip.GetTip(picker) as string, StringComparison.Ordinal);
        Assert.Equal("home", (picker.SelectedItem as StationProfile)?.Id); // state kept

        Localizer.Instance.SetLanguage(null);
        Assert.Equal("APRS-IS connected · T2TEST", connection.Text);
        window.Close();
    }
}
