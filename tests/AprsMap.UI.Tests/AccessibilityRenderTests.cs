// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Appearance;
using AprsMap.UI.ViewModels;
using AprsMap.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>
/// Every screen in every Theme × Colours combination at 200 % text renders without clipped text
/// (docs/features/F0.6-appearance.md, behavior 21 and acceptance test 9). Changes the app's appearance, so these run
/// alone, after the parallel tests.
/// </summary>
[Collection(nameof(LanguageViewTests))]
public sealed class AccessibilityRenderTests : IDisposable
{
    private const double WindowWidth = 1280;

    public void Dispose() => App.Appearance.Apply(new AppearanceSettings());

    private static App App => Assert.IsType<App>(Application.Current);

    public static TheoryData<ThemeMode, Palette> Combinations()
    {
        var data = new TheoryData<ThemeMode, Palette>();
        foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            foreach (var palette in Enum.GetValues<Palette>())
            {
                data.Add(theme, palette);
            }
        }
        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Combinations))]
    public async Task EveryScreenFitsAtDoubleTextSize(ThemeMode theme, Palette palette)
    {
        App.Appearance.Apply(
            new AppearanceSettings
            {
                Theme = theme,
                Palette = palette,
                TextScale = 2.0,
            }
        );
        var failures = new List<string>();

        // The main view, with packets, an error and a profile.
        var engine = new FakeEngine();
        var vm = new MainViewModel(engine, new FakeTimeProvider(), appearance: App.Appearance);
        vm.Show(engine.Settings);
        vm.Show(new EngineStatus(ConnectionState.Connected, "T2CAWEST", 123456, "1.0.0"));
        vm.Apply(new StationBatch([FakeEngine.Station("N0CALL-15")], []));
        vm.Add(FakeEngine.Packet("N0CALL-15") with { Summary = "Car at 40.0150, -105.2705: heading home" });
        vm.ShowError(EngineErrors.Create(EngineErrorCodes.OperatorOnly, "English"));
        var window = Show(vm);
        Check(window, "main view", failures);

        // Settings, both sections.
        await vm.OpenSettingsCommand.ExecuteAsync(null);
        foreach (var section in vm.Settings!.Sections.ToList())
        {
            vm.Settings.SelectedSection = vm.Settings.Sections.Single(s => s.Id == section.Id);
            Check(window, "settings " + section.Id, failures);
        }
        // Adding a profile: the wizard's extra fields.
        await vm.OpenProfileEditorAsync(WizardMode.Add, null, engine.Settings);
        var add = vm.Setup!;
        add.Callsign = "AB1CD-9";
        foreach (var step in new[] { SetupStep.Callsign, SetupStep.Server, SetupStep.Passcode })
        {
            add.Step = step;
            Check(window, "add profile " + step, failures);
        }
        vm.Settings.CloseCommand.Execute(null);
        window.Close();

        // The setup wizard: every step and the passcode calculator.
        var setupEngine = new FakeEngine { Settings = AppSettings.Empty };
        var setupVm = new MainViewModel(setupEngine, new FakeTimeProvider(), appearance: App.Appearance);
        var setupWindow = Show(setupVm);
        setupVm.Show(setupEngine.Settings);
        for (var i = 0; i < 50 && setupVm.Setup is null; i++)
        {
            await Task.Yield();
        }
        var wizard = setupVm.Setup!;
        wizard.Callsign = "AB1CD-9";
        wizard.LocatorText = "DN70kb";
        await wizard.UseLocatorCommand.ExecuteAsync(null);
        foreach (var step in Enum.GetValues<SetupStep>())
        {
            wizard.Step = step;
            Check(setupWindow, "wizard " + step, failures);
        }
        wizard.HasPasscode = true;
        wizard.OpenCalculatorCommand.Execute(null);
        wizard.IsLicensed = true;
        await wizard.CalculateCommand.ExecuteAsync(null);
        Check(setupWindow, "passcode calculator", failures);
        setupWindow.Close();

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static Window Show(MainViewModel vm)
    {
        var window = new Window
        {
            Content = new MainView { DataContext = vm },
            Width = WindowWidth,
            Height = 900,
        };
        window.Show();
        return window;
    }

    /// <summary>
    /// Text is clipped when its block is narrower than the text wants (and it isn't meant to trim), or when it reaches
    /// past the right edge of the window.
    /// </summary>
    private static void Check(Window window, string screen, List<string> failures)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        foreach (var text in window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible))
        {
            if (string.IsNullOrEmpty(text.Text) || text.Bounds.Width <= 0)
            {
                continue;
            }
            // The laid-out text against the room it has; trimming text (long packet summaries) is meant to be cut.
            var room = text.Bounds.Width - text.Padding.Left - text.Padding.Right;
            var width = text.TextLayout.Width; // trailing spaces at wrapped line ends aren't drawn
            if (text.TextTrimming == TextTrimming.None && width > room + 1)
            {
                failures.Add($"{screen}: “{text.Text}” is cut short ({width:0} > {room:0})");
            }
            var right = text.TranslatePoint(new Point(text.Bounds.Width, 0), window)?.X;
            if (right > window.Bounds.Width + 1)
            {
                failures.Add($"{screen}: “{text.Text}” runs off the window ({right:0} > {window.Bounds.Width:0})");
            }
        }
    }
}
