// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Platform;
using AprsMap.UI.Settings;

namespace AprsMap.UI.Tests;

/// <summary>Help › About's Files (docs/features/F1.31-files.md, behaviors 1–4a, 6, 7).</summary>
public sealed class AboutFilesTests
{
    private static Localizer English() => new(CultureInfo.GetCultureInfo("en-US"));

    private sealed class Opener(bool works) : IFolderOpener
    {
        public List<string> Opened { get; } = [];

        public Task<bool> OpenAsync(string path)
        {
            Opened.Add(path);
            return Task.FromResult(works);
        }
    }

    [Fact]
    public async Task OnTheDesktopEachFolderHasItsPathCopyAndOpenFolder()
    {
        var engine = new FakeEngine();
        engine.StorageFolders.Add(new(StorageFolderKind.Machine, "/etc/xdg/aprsnet/aprsmap", false, false));
        var copied = new List<string>();
        var opener = new Opener(works: true);
        var about = new AboutViewModel(English()) { FolderOpener = opener };
        about.CopyToClipboard = text =>
        {
            copied.Add(text);
            return Task.CompletedTask;
        };

        await about.ShowFoldersAsync(engine, sharedStation: false, isOperator: true);

        Assert.Equal(["Settings", "Data", "Defaults for every user"], about.Folders.Select(f => f.Name));
        var settings = about.Folders[0];
        Assert.Equal(("Copy the settings folder", "Open the settings folder"), (settings.CopyName, settings.OpenName));
        Assert.True(settings.ShowsCopy && settings.ShowsOpen && settings.CanOpen);
        Assert.Equal("not created yet", about.Folders[2].Note);
        Assert.False(about.Folders[2].CanOpen);
        Assert.Equal("Not created yet: nothing has been kept there.", about.Folders[2].OpenTip);
        Assert.Null(settings.OpenTip);

        await settings.CopyCommand.ExecuteAsync(null);
        await settings.OpenCommand.ExecuteAsync(null);

        Assert.Equal(["/home/test/.config/aprsnet/aprsmap"], copied);
        Assert.Equal(["/home/test/.config/aprsnet/aprsmap"], opener.Opened);
        Assert.Null(about.FilesNote);
    }

    [Fact]
    public async Task AFolderTheSystemCantOpenHasItsPathCopiedInstead()
    {
        var copied = new List<string>();
        var about = new AboutViewModel(English()) { FolderOpener = new Opener(works: false) };
        about.CopyToClipboard = text =>
        {
            copied.Add(text);
            return Task.CompletedTask;
        };
        await about.ShowFoldersAsync(new FakeEngine(), sharedStation: false, isOperator: true);

        await about.Folders[1].OpenCommand.ExecuteAsync(null);

        Assert.Equal(["/home/test/.local/share/aprsnet/aprsmap"], copied);
        Assert.Equal("Couldn't open the folder; its path is copied instead.", about.Folders[1].Said);
    }

    [Fact]
    public async Task AGatewayViewerSeesNoServerPathAndTheOperatorIsToldWhereTheLogIs()
    {
        var viewer = new AboutViewModel(English());
        await viewer.ShowFoldersAsync(new FakeEngine(), sharedStation: true, isOperator: false);
        Assert.Empty(viewer.Folders);
        Assert.Equal("This browser keeps your layout and appearance in its own storage.", viewer.FilesNote);

        var operatorAbout = new AboutViewModel(English());
        await operatorAbout.ShowFoldersAsync(new FakeEngine(), sharedStation: true, isOperator: true);
        Assert.Equal(2, operatorAbout.Folders.Count);
        Assert.False(operatorAbout.Folders[0].ShowsOpen); // a browser can't open a folder
        Assert.StartsWith(
            "The gateway's log is the container's output",
            operatorAbout.FilesNote,
            StringComparison.Ordinal
        );
    }
}
