// SPDX-License-Identifier: AGPL-3.0-only
using System.ComponentModel;
using AprsMap.UI.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AprsMap.UI.Views;

/// <summary>
/// The Settings page. Code-behind only lays out for the window's width (sections down the side from 720 px, tabs
/// across the top below it; behavior 3) and puts focus on the first option when the page opens (behavior 20).
/// </summary>
public sealed partial class SettingsView : UserControl
{
    private static readonly string[] Sections =
    [
        "ProfilesSection",
        "AppearanceSection",
        "MapSection",
        "DataSection",
        "AboutSection",
    ];

    /// <summary>Below this width the sections become tabs across the top.</summary>
    public const double NarrowWidth = 720;

    private SettingsViewModel? _viewModel;

    /// <summary>Creates the view.</summary>
    public SettingsView() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }
        if (_viewModel?.Profiles is { } oldProfiles)
        {
            oldProfiles.PropertyChanged -= OnProfilesChanged;
        }
        _viewModel = DataContext as SettingsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
            if (_viewModel.Profiles is { } profiles)
            {
                profiles.PropertyChanged += OnProfilesChanged;
            }
            // Choose folder… for the log file (F1.32 behavior 8): a folder on this computer, so the desktop only.
            if (_viewModel.Logging is { } logging && !OperatingSystem.IsBrowser())
            {
                logging.FolderPicker = PickFolderAsync;
            }
        }
    }

    private async Task<string?> PickFolderAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage)
        {
            return null;
        }
        var picked = await storage.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = Localization.Localizer.Instance["Logging.ChooseFolder"],
                AllowMultiple = false,
            }
        );
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    /// <summary>After a delete, Undo takes focus (F0.8 behavior 14); when it goes away, focus goes to Add profile.</summary>
    private void OnProfilesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName != nameof(ProfilesSectionViewModel.CanUndoDelete)
            || _viewModel?.Profiles is not { } profiles
        )
        {
            return;
        }
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (profiles.CanUndoDelete)
        {
            Dispatcher.UIThread.Post(
                () => this.FindControl<Button>("UndoDeleteButton")?.Focus(),
                DispatcherPriority.Loaded
            );
        }
        else if (focused is Button { Name: "UndoDeleteButton" } or null)
        {
            Dispatcher.UIThread.Post(
                () => this.FindControl<Button>("AddProfileButton")?.Focus(),
                DispatcherPriority.Loaded
            );
        }
    }

    /// <summary>When Undo goes away (used, or timed out) with focus on it, focus goes back to Reset, not nowhere.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (
            e.PropertyName == nameof(SettingsViewModel.CanUndoReset)
            && _viewModel?.CanUndoReset == false
            && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Button { Name: "UndoButton" } or null
        )
        {
            Dispatcher.UIThread.Post(() => this.FindControl<Button>("ResetButton")?.Focus(), DispatcherPriority.Loaded);
        }
    }

    /// <inheritdoc />
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var narrow = e.NewSize.Width < NarrowWidth;
        this.FindControl<ListBox>("SectionList")?.Classes.Set("narrow", narrow);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && IsVisible)
        {
            FocusFirstOption();
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        FocusFirstOption();
    }

    private void FocusFirstOption() =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (IsEffectivelyVisible && DataContext is not null)
                {
                    // The first thing to act on in the section showing: an option, or a profile's button.
                    var first = Sections
                        .Select(name => this.FindControl<Control>(name))
                        .FirstOrDefault(section => section?.IsVisible == true)
                        ?.GetLogicalDescendants()
                        .OfType<Control>()
                        .FirstOrDefault(c =>
                            c is ComboBox or Button or CheckBox or HyperlinkButton
                            && c.IsEffectivelyVisible
                            && c.IsEffectivelyEnabled
                        );
                    (first ?? (Control?)this.FindControl<Button>("SettingsBackButton"))?.Focus();
                }
            },
            DispatcherPriority.Loaded
        );
}
