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
        Shell.SavedLayoutsViewModel? layouts = null,
        LoggingSectionViewModel? logging = null,
        MessagingSectionViewModel? messaging = null,
        ObjectsSectionViewModel? objects = null,
        WebViewerSectionViewModel? webViewer = null,
        StationListsSectionViewModel? stationLists = null,
        GatewayAccessSectionViewModel? gatewayAccess = null,
        ConnectionSectionViewModel? connection = null
    )
    {
        Connection = connection;
        StationLists = stationLists;
        GatewayAccess = gatewayAccess;
        WebViewer = webViewer;
        ArgumentNullException.ThrowIfNull(appearance);
        _appearance = appearance;
        Profiles = profiles;
        Sources = sources;
        Beacons = beacons;
        Data = data;
        Layouts = layouts;
        Logging = logging;
        Messaging = messaging;
        Objects = objects;
        _time = time;
        _text = text ?? Localizer.Instance;
        AppearanceItems = [.. SettingsRegistry.Appearance(appearance, _text, showPseudoLanguage)];
        MapItems = [.. mapItems ?? []];
        UnitItems = [.. unitItems ?? []];
        Sections = [];
        Retranslate();
        SelectedSection = Sections[0];
        _appearance.Changed += OnAppearanceChanged;
        _text.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Raised when the page should close (Back or Escape).</summary>
    public event EventHandler? Closed;

    /// <summary>The sections, in order; About is a dialog of its own (F1.29 behavior 10).</summary>
    public ObservableCollection<SettingsSection> Sections { get; }

    /// <summary>The section showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsAppearanceSection),
        nameof(IsProfilesSection),
        nameof(IsSourcesSection),
        nameof(IsBeaconsSection),
        nameof(IsDataSection),
        nameof(IsWebViewerSection),
        nameof(IsGatewayAccessSection),
        nameof(IsConnectionSection),
        nameof(IsMapSection),
        nameof(IsUnitsSection),
        nameof(IsLayoutSection),
        nameof(IsLoggingSection),
        nameof(IsMessagingSection),
        nameof(IsObjectsSection),
        nameof(IsStationListsSection)
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
    internal void ShowSection(string id) =>
        SelectedSection = Sections.FirstOrDefault(s => s.Id == id) ?? SelectedSection;

    internal void ShowLayout() => SelectedSection = Sections.FirstOrDefault(s => s.Id == "layout") ?? SelectedSection;

    /// <summary>Whether Objects is showing (docs/features/F2.7-objects-and-items.md).</summary>
    public bool IsObjectsSection => SelectedSection?.Id == "objects";

    /// <summary>Settings › Objects: the active profile's objects, where this viewer may transmit.</summary>
    public ObjectsSectionViewModel? Objects { get; }

    /// <summary>Whether Station lists is showing (docs/features/F1.33-station-lists.md).</summary>
    public bool IsStationListsSection => SelectedSection?.Id == "station-lists";

    /// <summary>Settings › Station lists: the active profile's lists, where this viewer may change settings.</summary>
    public StationListsSectionViewModel? StationLists { get; }

    /// <summary>Whether Messaging is showing (docs/features/F2.13-ping.md).</summary>
    public bool IsMessagingSection => SelectedSection?.Id == "messaging";

    /// <summary>Settings › Messaging: the active profile's ping responder, where this viewer may change the station.</summary>
    public MessagingSectionViewModel? Messaging { get; }

    /// <summary>Whether Logging is showing (docs/features/F1.32-logging.md).</summary>
    public bool IsLoggingSection => SelectedSection?.Id == "logging";

    /// <summary>Settings › Logging, for the desktop user and a gateway's signed-in operator (behavior 1).</summary>
    public LoggingSectionViewModel? Logging { get; }

    /// <summary>Whether Data is showing.</summary>
    public bool IsDataSection => SelectedSection?.Id == "data";

    /// <summary>Whether Web viewer is showing (docs/features/F1.35-desktop-web-viewer.md).</summary>
    public bool IsWebViewerSection => SelectedSection?.Id == "web-viewer";

    /// <summary>Whether Connection is showing.</summary>
    public bool IsConnectionSection => SelectedSection?.Id == "connection";

    /// <summary>Connection (F1.39): the desktop's own station or a gateway's; null in the browser.</summary>
    public ConnectionSectionViewModel? Connection { get; }

    /// <summary>Whether Gateway access is showing.</summary>
    public bool IsGatewayAccessSection => SelectedSection?.Id == "gateway-access";

    /// <summary>Gateway access (F1.38), for the signed-in operator of a gateway.</summary>
    public GatewayAccessSectionViewModel? GatewayAccess { get; }

    /// <summary>The desktop's web viewer (F1.35); null in the browser.</summary>
    public WebViewerSectionViewModel? WebViewer { get; }

    /// <summary>The Data section (F0.10), when the app offers Reset the app.</summary>
    public DataSectionViewModel? Data { get; }

    /// <summary>The Appearance section's options, from the registry.</summary>
    public ObservableCollection<SettingItem> AppearanceItems { get; }

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

    /// <summary>Gives the profiles the station's filter when the page opens.</summary>
    public void Show(EngineStatus? status)
    {
        Profiles?.ShowFilter(status?.Filter);
        Sources?.ShowFilter(status?.Filter);
        Sources?.ShowIGate(status?.IGate);
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
        if (Objects is not null)
        {
            Sections.Add(new SettingsSection("objects", _text["Settings.Objects"]));
        }
        if (StationLists is not null)
        {
            Sections.Add(new SettingsSection("station-lists", _text["Settings.StationLists"]));
        }
        if (Messaging is not null)
        {
            Sections.Add(new SettingsSection("messaging", _text["Settings.Messaging"]));
        }
        Sections.Add(new SettingsSection("appearance", _text["Settings.Appearance"]));
        if (WebViewer is not null)
        {
            Sections.Add(new SettingsSection("web-viewer", _text["Settings.WebViewer"]));
        }
        if (Connection is not null)
        {
            Sections.Add(new SettingsSection("connection", _text["Settings.Connection"]));
        }
        if (GatewayAccess is not null)
        {
            Sections.Add(new SettingsSection("gateway-access", _text["Settings.GatewayAccess"]));
        }
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
        if (Data?.Capture is not null)
        {
            // Packet capture; Clear data and Start over are in the Tools menu (F1.29 behavior 9).
            Sections.Add(new SettingsSection("data", _text["Settings.Data"]));
        }
        if (Logging is not null)
        {
            Sections.Add(new SettingsSection("logging", _text["Settings.Logging"]));
        }
        SelectedSection = Sections.FirstOrDefault(s => s.Id == selected) ?? (selected is null ? null : Sections[0]);
        foreach (var item in AppearanceItems.Concat(MapItems).Concat(UnitItems))
        {
            item.Retranslate();
        }
        Profiles?.Retranslate();
        GatewayAccess?.Retranslate();
        Connection?.Retranslate();
        Sources?.Retranslate();
        Logging?.Retranslate();
        Messaging?.Retranslate();
        Objects?.Retranslate();
        StationLists?.Retranslate();
        Beacons?.Retranslate();
        Data?.Retranslate();
    }
}

/// <summary>
/// A folder in Help › About's Files part (docs/features/F1.31-files.md, behaviors 1–4a): its name, what it holds, its
/// path, Copy and, on the desktop, Open folder.
/// </summary>
public sealed partial class StorageFolderRow : ObservableObject
{
    private readonly AboutViewModel _owner;
    private readonly Localizer _text;

    internal StorageFolderRow(StorageFolder folder, AboutViewModel owner, Localizer text)
    {
        Folder = folder;
        _owner = owner;
        _text = text;
        var kind = folder.Kind.ToString();
        Name = text[$"About.Folder.{kind}"];
        Holds = text[$"About.Folder.{kind}.Holds"];
        CopyName = text[$"About.Folder.{kind}.Copy"];
        OpenName = text[$"About.Folder.{kind}.Open"];
        Note =
            !folder.Exists ? text["About.Folder.NotCreated"]
            : folder.Configured ? text["About.Folder.Configured"]
            : null;
    }

    /// <summary>The folder.</summary>
    public StorageFolder Folder { get; }

    /// <summary>"Settings".</summary>
    public string Name { get; }

    /// <summary>"Profiles, sources, appearance and layouts".</summary>
    public string Holds { get; }

    /// <summary>The full path, never shortened (behavior 4).</summary>
    public string Path => Folder.Path;

    /// <summary>"set in configuration" or "not created yet" (behavior 2); null otherwise.</summary>
    public string? Note { get; }

    /// <summary>Copy's accessible name: "Copy the settings folder" (behavior 3).</summary>
    public string CopyName { get; }

    /// <summary>Open folder's accessible name: "Open the settings folder" (behavior 4a).</summary>
    public string OpenName { get; }

    /// <summary>Whether Copy shows: there's a clipboard (behavior 3).</summary>
    public bool ShowsCopy => _owner.CopyToClipboard is not null;

    /// <summary>Whether Open folder shows: the desktop app (behavior 4a).</summary>
    public bool ShowsOpen => _owner.FolderOpener is not null;

    /// <summary>Whether Open folder can be used: the folder is there.</summary>
    public bool CanOpen => Folder.Exists;

    /// <summary>Why Open folder is disabled, as its tooltip: "Not created yet…"; null when it isn't (behavior 4a).</summary>
    public string? OpenTip => CanOpen ? null : _text["About.Folder.NotCreatedTip"];

    /// <summary>"Copied", or "Couldn't open the folder…", for a moment; null otherwise.</summary>
    [ObservableProperty]
    public partial string? Said { get; private set; }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(ShowsCopy));
        OnPropertyChanged(nameof(ShowsOpen));
    }

    /// <summary>Puts the path on the clipboard (behavior 3).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task CopyAsync()
    {
        if (_owner.CopyToClipboard is { } copy)
        {
            await copy(Path);
            Said = _text["About.Folder.Copied"];
        }
    }

    /// <summary>Opens the folder, or copies its path when the system can't (behavior 4a).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task OpenAsync()
    {
        if (_owner.FolderOpener is { } opener && !await opener.OpenAsync(Path))
        {
            if (_owner.CopyToClipboard is { } copy)
            {
                await copy(Path);
            }
            Said = _text["About.Folder.OpenFailed"];
        }
    }
}

/// <summary>
/// One app's components in Help › About (docs/features/F1.34-about-components.md, behaviors 1, 2): "Gateway 0.6.0", the
/// runtime and OS, and each package with its version and licence.
/// </summary>
public sealed class ComponentGroup
{
    internal ComponentGroup(ComponentReport report, Localizer text)
    {
        Report = report;
        Name = text[$"About.Components.{report.Host}"];
        Heading = $"{Name} {AboutViewModel.ShortVersion(report.Version)}";
        RuntimeText = $"{report.Runtime} · {report.OperatingSystem}";
        Rows =
        [
            .. report.Components.Select(c => new ComponentRow(
                c.Name,
                c.Version,
                c.Licence switch
                {
                    null => text["About.Components.NoLicence"],
                    var l when l.StartsWith("file: ", StringComparison.Ordinal) => text["About.Components.LicenceFile"],
                    var l => l,
                }
            )),
        ];
    }

    /// <summary>The report.</summary>
    public ComponentReport Report { get; }

    /// <summary>"Gateway".</summary>
    public string Name { get; }

    /// <summary>"Gateway 0.6.0-alpha.1".</summary>
    public string Heading { get; }

    /// <summary>".NET 10.0.12 · Debian GNU/Linux 13 (X64)".</summary>
    public string RuntimeText { get; }

    /// <summary>The frameworks, then the packages by name.</summary>
    public IReadOnlyList<ComponentRow> Rows { get; }
}

/// <summary>A component's line: name, version and licence (F1.34 behavior 1).</summary>
/// <param name="Name">The package or framework.</param>
/// <param name="Version">Its version.</param>
/// <param name="Licence">What its package states, in words when it isn't an SPDX expression.</param>
public sealed record ComponentRow(string Name, string Version, string Licence);

/// <summary>About (behavior 15), now a dialog from Help › About (F1.29 behavior 10).</summary>
public sealed partial class AboutViewModel(Localizer text) : ObservableObject
{
    /// <summary>
    /// The components of each app this viewer may see (docs/features/F1.34-about-components.md, behaviors 1–3): the app
    /// in front of them, and on a gateway, for its operator, the gateway's too.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<ComponentGroup> ComponentGroups { get; } = [];

    /// <summary>"Components (98)", the expander's heading.</summary>
    [ObservableProperty]
    public partial string ComponentsHeading { get; private set; } = string.Empty;

    /// <summary>"Copied", for a moment after Copy (behavior 4); null otherwise.</summary>
    [ObservableProperty]
    public partial string? ComponentsSaid { get; private set; }

    /// <summary>Where the release's SBOMs are, with every licence in full (behavior 5, F4.17).</summary>
    public static Uri? ReleasesUri =>
        SourceUri is { } source ? new Uri(source.ToString().TrimEnd('/') + "/releases") : null;

    /// <summary>
    /// Lists the components: on the desktop the app's (engine and UI in one); in a browser the browser app's, and the
    /// gateway's for its operator (behaviors 2, 3).
    /// </summary>
    internal async Task ShowComponentsAsync(
        IEngineService engine,
        ComponentReport here,
        bool sharedStation,
        bool isOperator
    )
    {
        ComponentGroups.Clear();
        if (!sharedStation)
        {
            ComponentGroups.Add(new ComponentGroup(await engine.GetComponentsAsync(), text));
        }
        else
        {
            ComponentGroups.Add(new ComponentGroup(here, text));
            if (isOperator)
            {
                try
                {
                    ComponentGroups.Add(new ComponentGroup(await engine.GetComponentsAsync(), text));
                }
                catch (Exception ex) when (ex is EngineAccessException or InvalidOperationException)
                {
                    // Signed out meanwhile: the browser app's list still stands.
                }
            }
        }
        ComponentsHeading = text.Format(
            "About.Components",
            new Dictionary<string, object?> { ["count"] = ComponentGroups.Sum(g => g.Rows.Count) }
        );
    }

    /// <summary>Puts every list on the clipboard as text, for a bug report (behavior 4).</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task CopyComponentsAsync()
    {
        if (CopyToClipboard is { } copy)
        {
            await copy(string.Join("\n\n", ComponentGroups.Select(g => g.Report.ToText(g.Name))));
            ComponentsSaid = text["About.Folder.Copied"];
        }
    }

    /// <summary>The folders the app keeps things in (docs/features/F1.31-files.md); empty where they're not this viewer's.</summary>
    public System.Collections.ObjectModel.ObservableCollection<StorageFolderRow> Folders { get; } = [];

    /// <summary>
    /// What a viewer is told instead of a server's paths (behavior 6), or where a gateway's log is (behavior 7); null
    /// otherwise.
    /// </summary>
    [ObservableProperty]
    public partial string? FilesNote { get; private set; }

    /// <summary>Puts text on the clipboard; set by the view where there's one (behavior 3).</summary>
    [ObservableProperty]
    public partial Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>Opens a folder in the file manager; the desktop app's, null in the browser (behavior 4a).</summary>
    public Platform.IFolderOpener? FolderOpener { get; set; }

    partial void OnCopyToClipboardChanged(Func<string, Task>? value)
    {
        foreach (var row in Folders)
        {
            row.Refresh();
        }
    }

    /// <summary>
    /// Lists the folders: everyone's on the desktop, only a signed-in operator's on a gateway, where a viewer gets a note
    /// instead (behaviors 1, 6, 7).
    /// </summary>
    /// <param name="engine">The engine whose folders these are.</param>
    /// <param name="sharedStation">A gateway's station.</param>
    /// <param name="isOperator">Whether this viewer is its operator.</param>
    /// <param name="service">The Linux service gateway, whose log is the journal's (F1.40).</param>
    internal async Task ShowFoldersAsync(
        IEngineService engine,
        bool sharedStation,
        bool isOperator,
        bool service = false
    )
    {
        Folders.Clear();
        if (sharedStation && !isOperator)
        {
            FilesNote = text["About.Files.Browser"];
            return;
        }
        try
        {
            foreach (var folder in await engine.GetStorageFoldersAsync())
            {
                Folders.Add(new StorageFolderRow(folder, this, text));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            FilesNote = text.Describe(ex);
            return;
        }
        FilesNote =
            sharedStation && Folders.All(f => f.Folder.Kind != StorageFolderKind.Logs)
                ? service
                    ? text.Format(
                        "About.Files.JournalLogs",
                        new Dictionary<string, object?> { ["service"] = Brand.Slug + "-gateway" }
                    )
                    : text["About.Files.DockerLogs"]
                : null;
    }

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

    /// <summary>
    /// The engine and APRS.NET versions, once the engine has answered; "Gateway 0.8.0-alpha.2 (Linux service) · …" when
    /// it's a gateway's (#453).
    /// </summary>
    [ObservableProperty]
    public partial string? EngineText { get; set; }

    /// <summary>That the gateway's version differs from this app's, in its major.minor (#453); null when they match.</summary>
    [ObservableProperty]
    public partial string? VersionsDifferText { get; set; }

    /// <summary>Whether the engine is a gateway's, reached over the network: the browser, or the desktop as its client.</summary>
    internal bool Remote { get; set; }

    /// <summary>"3 viewers connected", on a gateway (F1.14 behavior 11); null on desktop.</summary>
    [ObservableProperty]
    public partial string? ViewersText { get; set; }

    /// <summary>"Pre-releases · checked 18/10 14:02 · up to date" (F4.15 behavior 10); null where updates aren't this viewer's.</summary>
    [ObservableProperty]
    public partial string? UpdatesText { get; set; }

    /// <summary>Check now; null where it's hidden (behavior 6) or not this viewer's.</summary>
    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand? CheckUpdatesCommand { get; set; }

    /// <summary>
    /// Restart gateway, for the operator of the Linux service gateway (docs/features/F1.40-gateway-service-updates.md,
    /// behavior 2); null elsewhere.
    /// </summary>
    public System.Windows.Input.ICommand? RestartGatewayCommand { get; set; }

    /// <summary>"No failed sign-ins in the last day", to a signed-in gateway operator (F2.9 behavior 10); null otherwise.</summary>
    [ObservableProperty]
    public partial string? SignInsText { get; set; }

    /// <summary>Whether the APRS-IS login is verified and how often it reconnected, while connected.</summary>
    [ObservableProperty]
    public partial string? ConnectionText { get; set; }

    /// <summary>The licences, in one line.</summary>
    [ObservableProperty]
    public partial string LicenceText { get; set; } = string.Empty;

    /// <summary>"In use: built in, from 18 October 2026": the device list (F4.16 behavior 11); null before the status.</summary>
    [ObservableProperty]
    public partial string? DeviceListText { get; set; }

    /// <summary>The aprs-deviceid list's home, for its credit (F4.16 behavior 11).</summary>
    public static Uri DeviceListUri { get; } = new("https://github.com/aprsorg/aprs-deviceid");

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
                !Remote ? "Settings.About.Engine"
                    : Platform.GatewayVersions.InstallText(text, _status.Install) is null ? "Settings.About.Gateway"
                    : "Settings.About.GatewayInstall",
                new Dictionary<string, object?>
                {
                    ["version"] = ShortVersion(_status.Version),
                    ["aprsnet"] = _status.AprsNetVersion ?? "?",
                    ["install"] = Platform.GatewayVersions.InstallText(text, _status.Install),
                }
            );
        VersionsDifferText =
            Remote && _status is not null
                ? Platform.GatewayVersions.DifferText(text, _status.Version, Brand.Version)
                : null;
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
        // The device list in use, credited (docs/features/F4.16-device-list.md, behavior 11).
        DeviceListText = _status?.DeviceList is { } devices
            ? text.Format(
                devices.Origin == DeviceListOrigin.Downloaded ? "DeviceList.Downloaded" : "DeviceList.BuiltIn",
                new Dictionary<string, object?>
                {
                    ["date"] = devices.GeneratedAt is { } at
                        ? at.ToString("d MMMM yyyy", text.Culture)
                        : text["DeviceList.UnknownDate"],
                }
            )
            : null;
    }
}
