// SPDX-License-Identifier: AGPL-3.0-only
using Avalonia.Headless.XUnit;

namespace ZedMap.UI.Tests;

public class AppIconTests
{
    [AvaloniaFact]
    public void TheWindowIconLoads()
    {
        var icon = App.AppIcon();

        Assert.NotNull(icon);
    }

    [Fact]
    public void TheWebIconIsTheBrandingIcon()
    {
        var root = RepoRoot();

        // tools/make-icons.sh copies it; this catches an icon changed in one place only.
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(root, "build", "branding", "icon.svg")),
            File.ReadAllBytes(Path.Combine(root, "src", "ZedMap.Browser", "wwwroot", "favicon.svg"))
        );
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ZedMap.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("Can't find the repository root.");
    }
}
