// SPDX-License-Identifier: AGPL-3.0-only
using Avalonia.Controls;

namespace AprsMap.UI.Views;

/// <summary>The GPS panel (docs/features/F3.12-gps-status.md); the view model does the work.</summary>
public sealed partial class GpsPanel : UserControl
{
    /// <summary>Creates the panel.</summary>
    public GpsPanel() => InitializeComponent();
}
