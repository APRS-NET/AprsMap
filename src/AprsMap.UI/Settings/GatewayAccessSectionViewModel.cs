// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Settings;

/// <summary>
/// Settings › Gateway access (docs/features/F1.38-gateway-api-key.md, behaviors 1, 6, 9, 10): a key for each app. The
/// list says what each key allows, when and where it was last used and whether it's connected, with Revoke; Add an app
/// makes a new key, named, operator or watch only, shown this once to copy. Test checks a key an app has. The signed-in
/// operator's, on a gateway only.
/// </summary>
public sealed partial class GatewayAccessSectionViewModel : ObservableObject
{
    private readonly IEngineService _engine;
    private readonly Localizer _text;
    private readonly TimeZoneInfo _zone;

    internal GatewayAccessSectionViewModel(IEngineService engine, Localizer text, TimeZoneInfo zone)
    {
        _engine = engine;
        _text = text;
        _zone = zone;
        AccessOptions =
        [
            new(ApiKeyAccess.Operator, text["GatewayAccess.Access.Operator"]),
            new(ApiKeyAccess.Watch, text["GatewayAccess.Access.Watch"]),
        ];
        NewAccess = AccessOptions[0];
        StatusText = text["GatewayAccess.None"];
    }

    /// <summary>Puts text on the clipboard; set by the view.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>The apps with a key, oldest first.</summary>
    public ObservableCollection<ApiKeyRow> Keys { get; } = [];

    /// <summary>\"No app has a key: apps can only watch.\" or \"3 apps have keys.\"</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; }

    /// <summary>Operator, or Watch only.</summary>
    public IReadOnlyList<ApiKeyAccessOption> AccessOptions { get; }

    /// <summary>The new key's name: \"Derek's laptop\".</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    public partial string NewName { get; set; } = string.Empty;

    /// <summary>What the new key will allow.</summary>
    [ObservableProperty]
    public partial ApiKeyAccessOption NewAccess { get; set; }

    /// <summary>The key just created, shown this once with Copy; null otherwise (behavior 6).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCreated))]
    public partial string? CreatedKey { get; private set; }

    /// <summary>\"Key for Derek's laptop: copy it into the app now. It won't be shown again.\"</summary>
    [ObservableProperty]
    public partial string? CreatedText { get; private set; }

    /// <summary>Whether a new key is showing.</summary>
    public bool HasCreated => CreatedKey is not null;

    /// <summary>A key pasted to Test (#447).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    public partial string TestKey { get; set; } = string.Empty;

    /// <summary>What went wrong, or what happened; null for nothing.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <summary>Whether <see cref="Message"/> is a problem, for its colour.</summary>
    [ObservableProperty]
    public partial bool HasProblem { get; private set; }

    /// <summary>Reads the keys.</summary>
    internal async Task ShowAsync()
    {
        try
        {
            Show(await _engine.GetApiKeysAsync());
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Problem(ex);
        }
    }

    private void Show(IReadOnlyList<ApiKeyInfo> keys)
    {
        Keys.Clear();
        foreach (var key in keys)
        {
            Keys.Add(new ApiKeyRow(key, Describe(key), this));
        }
        StatusText =
            keys.Count == 0
                ? _text["GatewayAccess.None"]
                : _text.Format("GatewayAccess.Count", new Dictionary<string, object?> { ["count"] = keys.Count });
    }

    private bool CanCreate => NewName.Trim().Length > 0;

    /// <summary>Makes a key for the app named, shows it once to copy, and lists it (behaviors 1, 6).</summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        try
        {
            var created = await _engine.CreateApiKeyAsync(NewName.Trim(), NewAccess.Value);
            CreatedKey = created.Key;
            CreatedText = _text.Format(
                "GatewayAccess.Created",
                new Dictionary<string, object?> { ["name"] = created.Info.Name }
            );
            NewName = string.Empty;
            Message = null;
            HasProblem = false;
            Show(await _engine.GetApiKeysAsync());
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            Problem(ex);
        }
    }

    /// <summary>Copies the new key: the only time it can be seen (behavior 6).</summary>
    [RelayCommand]
    private Task CopyAsync() =>
        CreatedKey is { } key ? CopyToClipboard?.Invoke(key) ?? Task.CompletedTask : Task.CompletedTask;

    /// <summary>Done: the new key is hidden for good.</summary>
    [RelayCommand]
    private void Done()
    {
        CreatedKey = null;
        CreatedText = null;
    }

    /// <summary>Revokes <paramref name="row"/>'s key, after asking once while its app is connected.</summary>
    internal async Task RevokeAsync(ApiKeyRow row)
    {
        try
        {
            if (row.Info.InUse > 0 && !row.Confirming)
            {
                row.Confirming = true;
                HasProblem = true;
                Message = _text.Format(
                    "GatewayAccess.ConfirmRevoke",
                    new Dictionary<string, object?> { ["name"] = row.Name, ["count"] = row.Info.InUse }
                );
                return;
            }
            await _engine.RevokeApiKeyAsync(row.Info.Id);
            HasProblem = false;
            Message = _text.Format("GatewayAccess.Revoked", new Dictionary<string, object?> { ["name"] = row.Name });
            Show(await _engine.GetApiKeysAsync());
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Problem(ex);
        }
    }

    /// <summary>
    /// Tests a key an app or a password manager has against the gateway's keys, saving nothing (#447): the way to check
    /// one, since the gateway keeps only their hashes.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasTestKey))]
    private async Task TestAsync()
    {
        try
        {
            var check = await _engine.CheckApiKeyAsync(TestKey.Trim());
            HasProblem = check != ApiKeyCheck.Accepted;
            Message = _text[$"GatewayAccess.Test.{check}"];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Problem(ex);
        }
    }

    private bool HasTestKey => TestKey.Trim().Length > 0;

    private void Problem(Exception ex)
    {
        HasProblem = true;
        Message = _text.Describe(ex);
    }

    /// <summary>\"Operator · last used 15/11/2026 09:40 from 10.10.20.130 · connected\", in the current language.</summary>
    private string Describe(ApiKeyInfo key)
    {
        var parts = new List<string>
        {
            _text[$"GatewayAccess.Access.{key.Access}"],
            key is { LastUsedAt: { } at }
                ? _text.Format(
                    "GatewayAccess.LastUsed",
                    new Dictionary<string, object?>
                    {
                        ["time"] = TimeZoneInfo.ConvertTime(at, _zone).ToString("g", _text.Culture),
                        ["address"] = key.LastUsedFrom ?? "?",
                    }
                )
                : _text["GatewayAccess.NeverUsed"],
        };
        if (key.InUse > 0)
        {
            parts.Add(
                _text.Format("GatewayAccess.Connected", new Dictionary<string, object?> { ["count"] = key.InUse })
            );
        }
        return string.Join(" · ", parts);
    }

    /// <summary>The texts again, in the new language.</summary>
    internal void Retranslate() => Show([.. Keys.Select(k => k.Info)]);
}

/// <summary>A key's access choice.</summary>
/// <param name="Value">The access.</param>
/// <param name="Label">How it reads.</param>
public sealed record ApiKeyAccessOption(ApiKeyAccess Value, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>One app's key in Settings › Gateway access.</summary>
public sealed partial class ApiKeyRow(ApiKeyInfo info, string details, GatewayAccessSectionViewModel section)
    : ObservableObject
{
    /// <summary>The key, as the gateway lists it.</summary>
    public ApiKeyInfo Info { get; } = info;

    /// <summary>Its name.</summary>
    public string Name => Info.Name;

    /// <summary>What it allows, when and where it was last used, and whether it's connected.</summary>
    public string Details { get; } = details;

    /// <summary>Revoke was pressed once while its app was connected: the next press revokes.</summary>
    internal bool Confirming { get; set; }

    /// <summary>Revokes it.</summary>
    [RelayCommand]
    private Task RevokeAsync() => section.RevokeAsync(this);
}
