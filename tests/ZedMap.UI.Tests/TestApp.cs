// SPDX-License-Identifier: AGPL-3.0-only
using Avalonia;
using Avalonia.Headless;
using ZedMap.UI.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace ZedMap.UI.Tests;

/// <summary>Runs the real App (styles and theme tokens) on Avalonia's headless platform.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
