// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Platform;
using AprsMap.UI.Shell;
using AprsMap.UI.ViewModels;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Dock.Model.Core;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Web pop-out panels (docs/features/F1.24-web-pop-out.md, acceptance tests 1–3).</summary>
public sealed class PopOutTests
{
    private sealed class FakeChannel : ISelectionChannel
    {
        public List<string?> Published { get; } = [];

        public event Action<string?>? Received;

        public void Publish(string? station) => Published.Add(station);

        public void Receive(string? station) => Received?.Invoke(station);
    }

    private static ShellViewModel NewShell() =>
        new(new object(), new Localizer(CultureInfo.GetCultureInfo("en-US")), managedWindows: true);

    [AvaloniaFact]
    public void APanelOpensInANewTabOnlyWhereThereAreTabs()
    {
        var shell = NewShell();
        var packets = ShellViewModel.Dockables(shell.Layout).First(d => d.Id == PanelKinds.Packets);
        Assert.False(shell.Factory.CanPopOut); // the desktop

        string? opened = null;
        shell.Factory.PopOutOpener = kind => opened = kind;

        Assert.True(shell.Factory.CanPopOut);
        shell.Factory.OpenInNewTab.Execute(packets);
        Assert.Equal(PanelKinds.Packets, opened);
    }

    [AvaloniaFact]
    public void APoppedOutTabShowsItsPanelAlone()
    {
        // The app doesn't wire layout saving in a popped-out tab (App.StartLayout returns first).
        var shell = NewShell();

        shell.ShowOnly(PanelKinds.Details);

        var panel = Assert.Single(ShellViewModel.Dockables(shell.Layout).OfType<PanelTool>());
        Assert.Equal(PanelKinds.Details, panel.Kind);
        Assert.False(panel.CanClose || panel.CanFloat);
        Assert.Equal("Details", panel.Title);
        Assert.Empty(ShellViewModel.Dockables(shell.Layout).OfType<MapDocument>());
    }

    [AvaloniaFact]
    public void TheSelectionIsSharedBetweenTabsWithoutEchoing()
    {
        var channel = new FakeChannel();
        var vm = new MainViewModel(new FakeEngine(), new FakeTimeProvider()) { SelectionChannel = channel };
        var titles = new List<string>();

        vm.SelectedStation = "N0CAR-9";
        Assert.Equal(["N0CAR-9"], channel.Published);

        channel.Receive("W1AW-9");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("W1AW-9", vm.SelectedStation);
        Assert.Equal(["N0CAR-9"], channel.Published); // not sent back

        var popped = new MainViewModel(new FakeEngine(), new FakeTimeProvider())
        {
            SetPageTitle = titles.Add,
            PopOutTitle = "Packets",
        };
        popped.Show(new EngineStatus(ConnectionState.Connected, null, 0, "1") { Title = "NZ0P-10" });
        Assert.StartsWith("Packets · NZ0P-10 · ", titles.Last(), StringComparison.Ordinal);
    }
}
