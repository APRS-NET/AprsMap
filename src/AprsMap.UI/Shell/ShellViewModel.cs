// SPDX-License-Identifier: AGPL-3.0-only
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Controls;

namespace AprsMap.UI.Shell;

/// <summary>Spike: the shell's layout and factory, created with the default layout.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public ShellViewModel(object context, bool managedWindows)
    {
        // In the browser there are no OS windows: floating panels, drag previews and dock targets stay in the page.
        Dock.Settings.DockSettings.UseManagedWindows = managedWindows;
        Factory = new ShellFactory(context, managedWindows);
        Layout = Factory.CreateLayout();
        Factory.InitLayout(Layout);
    }

    public ShellFactory Factory { get; }

    [ObservableProperty]
    public partial IRootDock Layout { get; set; }

    /// <summary>Replaces the layout with a loaded one (already fitted to the screens).</summary>
    public void Use(IRootDock layout)
    {
        Factory.InitLayout(layout);
        Layout = layout;
    }
}
