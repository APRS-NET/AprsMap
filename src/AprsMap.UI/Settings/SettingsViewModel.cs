// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using AprsMap.Contracts;
using AprsMap.UI.Appearance;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Settings;

/// <summary>A section of the Settings page.</summary>
/// <param name="Id">Stable identifier: <c>appearance</c>, <c>about</c>.</param>
/// <param name="Title">The title, in the viewer's language.</param>
public sealed record SettingsSection(string Id, string Title)
{
    /// <inheritdoc />
    public override string ToString() => Title;
}

/// <summary>
/// The Settings page (docs/features/F0.6-appearance.md, behaviors 1–5, 15): its sections, the options of the
/// selected one, Reset with Undo, and About. Changes apply as they are made; the page has no Apply.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>How long Undo stays offered after a reset (behavior 5).</summary>
    public static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(10);

    private readonly AppearanceService _appearance;
    private readonly Localizer _text;
    private readonly TimeProvider _time;
    private AppearanceSettings? _beforeReset;
    private ITimer? _undoTimer;

    /// <summary>Creates the page for <paramref name="appearance"/>.</summary>
    public SettingsViewModel(
        AppearanceService appearance,
        TimeProvider time,
        Localizer? text = null,
        bool showPseudoLanguage = false,
        ProfilesSectionViewModel? profiles = null,
        DataSectionViewModel? data = null,
        IReadOnlyList<SettingItem>? mapItems = null,
        SourcesSectionViewModel? sources = null,
        IReadOnlyList<SettingItem>? unitItems = null,
        BeaconsSectionViewModel? beacons = null,
        Shell.SavedLayoutsViewModel? layouts = null
    )
    {
        ArgumentNullException.ThrowIfNull(appearance);
        _appearance = appearance;
        Profiles = profiles;
        Sources = sources;
        Beacons = beacons;
        Data = data;
        Layouts = layouts;
        _time = time;
        _text = text ?? Localizer.Instance;
        AppearanceItems = [.. SettingsRegistry.Appearance(appearance, _text, showPseudoLanguage)];
        MapItems = [.. mapItems ?? []];
        UnitItems = [.. unitItems ?? []];
        About = new AboutViewModel(_text);
        Sections = [];
        Retranslate();
        SelectedSection = Sections[0];
        _appearance.Changed += OnAppearanceChanged;
        _text.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Raised when the page should close (Back or Escape).</summary>
    public event EventHandler? Closed;

    /// <summary>The sections, in order: Profiles, Appearance, Data, About.</summary>
    public ObservableCollection<SettingsSection> Sections { get; }

    /// <summary>The section showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsAppearanceSection),
        nameof(IsAboutSection),
        nameof(IsProfilesSection),
        nameof(IsSourcesSection),
        nameof(IsBeaconsSection),
        nameof(IsDataSection),
        nameof(IsMapSection),
        nameof(IsUnitsSection),
        nameof(IsLayoutSection)
    )]
    public partial SettingsSection? SelectedSection { get; set; }

    /// <summary>Whether Profiles is showing.</summary>
    public bool IsProfilesSection => SelectedSection?.Id == "profiles";

    /// <summary>The Profiles section (F0.8), when the app manages profiles here.</summary>
    public ProfilesSectionViewModel? Profiles { get; }

    /// <summary>Whether Sources is showing.</summary>
    public bool IsSourcesSection => SelectedSection?.Id == "sources";

    /// <summary>The Sources section (F1.25), when the app manages sources here.</summary>
    public SourcesSectionViewModel? Sources { get; }

    /// <summary>Whether Beacons is showing.</summary>
    public bool IsBeaconsSection => SelectedSection?.Id == "beacons";

    /// <summary>The Beacons section (F2.2), when the app manages profiles here.</summary>
    public BeaconsSectionViewModel? Beacons { get; }

    /// <summary>Whether Appearance is showing.</summary>
    public bool IsAppearanceSection => SelectedSection?.Id == "appearance";

    /// <summary>Whether Map is showing.</summary>
    public bool IsMapSection => SelectedSection?.Id == "map";

    /// <summary>Whether Units is showing.</summary>
    public bool IsUnitsSection => SelectedSection?.Id == "units";

    /// <summary>The Map section's options (F1.4 behavior 21); empty when there is no map.</summary>
    public ObservableCollection<SettingItem> MapItems { get; }

    /// <summary>Settings › Units (F1.10 behavior 6): the station's unit per quantity; empty where it can't be changed.</summary>
    public IReadOnlyList<SettingItem> UnitItems { get; }

    /// <summary>Whether Layout is showing (docs/features/F1.23-saved-layouts.md).</summary>
    public bool IsLayoutSection => SelectedSection?.Id == "layout";

    /// <summary>The saved layouts (F1.23), when the app has a layout to save.</summary>
    public Shell.SavedLayoutsViewModel? Layouts { get; }

    /// <summary>Shows the Layout section (from the app menu's Save layout as…).</summary>
    internal void ShowLayout() => SelectedSection = Sections.FirstOrDefault(s => s.Id == "layout") ?? SelectedSection;

    /// <summary>Whether Data is showing.</summary>
    public bool IsDataSection => SelectedSection?.Id == "data";

    /// <summary>The Data section (F0.10), when the app offers Reset the app.</summary>
    public DataSectionViewModel? Data { get; }

    /// <summary>Whether About is showing.</summary>
    public bool IsAboutSection => SelectedSection?.Id == "about";

    /// <summary>The Appearance section's options, from the registry.</summary>
    public ObservableCollection<SettingItem> AppearanceItems { get; }

    /// <summary>The About section.</summary>
    public AboutViewModel About { get; }

    /// <summary>The page title, in the viewer's language.</summary>
    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    /// <summary>Whether Undo is offered after a reset.</summary>
    [ObservableProperty]
    public partial bool CanUndoReset { get; set; }

    /// <summary>Restores the default appearance; Undo brings it back for <see cref="UndoWindow"/>.</summary>
    [RelayCommand]
    private void ResetAppearance()
    {
        _beforeReset = _appearance.Current;
        _appearance.Apply(new AppearanceSettings());
        CanUndoReset = true;
        _undoTimer?.Dispose();
        // The timer fires on a pool thread; the change goes back to the UI thread that made the reset.
        var context = SynchronizationContext.Current;
        _undoTimer = _time.CreateTimer(
            _ =>
            {
                if (context is null)
                {
                    CanUndoReset = false;
                }
                else
                {
                    context.Post(_ => CanUndoReset = false, null);
                }
            },
            null,
            UndoWindow,
            Timeout.InfiniteTimeSpan
        );
    }

    /// <summary>Puts back the appearance from before the reset.</summary>
    [RelayCommand]
    private void UndoReset()
    {
        if (CanUndoReset && _beforeReset is { } before)
        {
            _appearance.Apply(before);
        }
        CanUndoReset = false;
        _undoTimer?.Dispose();
        _undoTimer = null;
    }

    /// <summary>Closes the page.</summary>
    [RelayCommand]
    private void Close() => Closed?.Invoke(this, EventArgs.Empty);

    /// <summary>Gives About the engine's details (versions, address lookup) when the page opens.</summary>
    public void Show(EngineStatus? status, GeocodingInfo? geocoding, AuthInfo? auth = null)
    {
        About.Show(status, geocoding, auth);
        Profiles?.ShowFilter(status?.Filter);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _appearance.Changed -= OnAppearanceChanged;
        _text.LanguageChanged -= OnLanguageChanged;
        _undoTimer?.Dispose();
        Profiles?.Dispose();
    }

    private void OnAppearanceChanged(object? sender, AppearanceSettings e)
    {
        foreach (var item in AppearanceItems.Concat(MapItems))
        {
            item.Refresh();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Retranslate();

    /// <summary>Data's counts are fetched each time it's shown: the store changes all the time (F1.19 behavior 1).</summary>
    partial void OnSelectedSectionChanged(SettingsSection? value)
    {
        if (value?.Id == "data" && Data?.Clear is { } clear)
        {
            _ = clear.RefreshAsync();
        }
    }

    /// <summary>Shows Settings › Data (the main menu's Clear data).</summary>
    internal void ShowData() => SelectedSection = Sections.FirstOrDefault(s => s.Id == "data") ?? SelectedSection;

    private void Retranslate()
    {
        Title = _text["Settings.Title"];
        var selected = SelectedSection?.Id;
        Sections.Clear();
        if (Profiles is not null)
        {
            Sections.Add(new SettingsSection("profiles", _text["Settings.Profiles"]));
        }
        if (Sources is not null)
        {
            Sections.Add(new SettingsSection("sources", _text["Settings.Sources"]));
        }
        if (Beacons is not null)
        {
            Sections.Add(new SettingsSection("beacons", _text["Settings.Beacons"]));
        }
        Sections.Add(new SettingsSection("appearance", _text["Settings.Appearance"]));
        if (MapItems.Count > 0)
        {
            Sections.Add(new SettingsSection("map", _text["Settings.Map"]));
        }
        if (Layouts is not null)
        {
            Sections.Add(new SettingsSection("layout", _text["Settings.Layout"]));
        }
        if (UnitItems.Count > 0)
        {
            Sections.Add(new SettingsSection("units", _text["Settings.Units"]));
        }
        if (Data is not null)
        {
            Sections.Add(new SettingsSection("data", _text["Settings.Data"]));
        }
        Sections.Add(new SettingsSection("about", _text["Settings.About"]));
        SelectedSection = Sections.FirstOrDefault(s => s.Id == selected) ?? (selected is null ? null : Sections[0]);
        foreach (var item in AppearanceItems.Concat(MapItems).Concat(UnitItems))
        {
            item.Retranslate();
        }
        About.Retranslate();
        Profiles?.Retranslate();
        Sources?.Retranslate();
        Beacons?.Retranslate();
        Data?.Retranslate();
    }
}

/// <summary>The About section (behavior 15).</summary>
public sealed partial class AboutViewModel(Localizer text) : ObservableObject
{
    private EngineStatus? _status;
    private GeocodingInfo? _geocoding;
    private AuthInfo? _auth;

    /// <summary>The product name.</summary>
    public static string ProductName => Brand.DisplayName;

    /// <summary>Where the source code is: the AGPL network-use offer.</summary>
    public static Uri? SourceUri => Uri.TryCreate(Brand.SourceUrl, UriKind.Absolute, out var uri) ? uri : null;

    /// <summary>OpenStreetMap's copyright page, for the lookup credit.</summary>
    public static Uri OpenStreetMapCopyrightUri { get; } = new("https://www.openstreetmap.org/copyright");

    /// <summary>"Version 0.1.0-alpha.11".</summary>
    [ObservableProperty]
    public partial string VersionText { get; set; } = string.Empty;

    /// <summary>The engine and APRS.NET versions, once the engine has answered.</summary>
    [ObservableProperty]
    public partial string? EngineText { get; set; }

    /// <summary>"3 viewers connected", on a gateway (F1.14 behavior 11); null on desktop.</summary>
    [ObservableProperty]
    public partial string? ViewersText { get; set; }

    /// <summary>"No failed sign-ins in the last day", to a signed-in gateway operator (F2.9 behavior 10); null otherwise.</summary>
    [ObservableProperty]
    public partial string? SignInsText { get; set; }

    /// <summary>Whether the APRS-IS login is verified and how often it reconnected, while connected.</summary>
    [ObservableProperty]
    public partial string? ConnectionText { get; set; }

    /// <summary>The licences, in one line.</summary>
    [ObservableProperty]
    public partial string LicenceText { get; set; } = string.Empty;

    /// <summary>Whether to credit OpenStreetMap and Nominatim (address lookup is on).</summary>
    [ObservableProperty]
    public partial bool ShowLookupCredit { get; set; }

    /// <summary><c>0.1.0-alpha.10.3+e86ba03a…</c> → <c>0.1.0-alpha.10.3 (e86ba03)</c>: the build's commit, short.</summary>
    internal static string ShortVersion(string version)
    {
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return version;
        }
        var commit = version[(plus + 1)..];
        return $"{version[..plus]} ({commit[..Math.Min(7, commit.Length)]})";
    }

    internal void Show(EngineStatus? status, GeocodingInfo? geocoding, AuthInfo? auth = null)
    {
        _status = status;
        _geocoding = geocoding;
        _auth = auth;
        Retranslate();
    }

    internal void Retranslate()
    {
        VersionText = text.Format(
            "Settings.About.Version",
            new Dictionary<string, object?> { ["version"] = ShortVersion(Brand.Version) }
        );
        LicenceText = text["Settings.About.Licence"];
        EngineText = _status is null
            ? null
            : text.Format(
                "Settings.About.Engine",
                new Dictionary<string, object?>
                {
                    ["version"] = ShortVersion(_status.Version),
                    ["aprsnet"] = _status.AprsNetVersion ?? "?",
                }
            );
        ConnectionText = _status is null ? null : ViewModels.ConnectionText.Details(text, _status);
        ViewersText = _status?.Viewers is { } viewers
            ? text.Format("Settings.About.Viewers", new Dictionary<string, object?> { ["count"] = viewers })
            : null;
        SignInsText = _auth is { Operator: true } signedIn
            ? text.Format(
                "Settings.About.FailedSignIns",
                new Dictionary<string, object?> { ["count"] = signedIn.FailedSignIns }
            )
            : null;
        ShowLookupCredit = _geocoding?.Enabled == true;
    }
}
