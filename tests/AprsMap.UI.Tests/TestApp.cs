// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Tests;
using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace AprsMap.UI.Tests;

/// <summary>Runs the real App (styles and theme tokens) on Avalonia's headless platform.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
