// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Settings;

/// <summary>
/// Settings › Gateway access (docs/features/F1.38-gateway-api-key.md, behaviors 1, 6): the gateway's API key, for apps
/// that control it. Generate, Copy, Save and Remove; a saved key is never shown again, only that one is set and since
/// when. The signed-in operator's, on a gateway only.
/// </summary>
public sealed partial class GatewayAccessSectionViewModel : ObservableObject
{
    private readonly IEngineService _engine;
    private readonly Localizer _text;
    private readonly TimeZoneInfo _zone;
    private ApiKeyStatus _status = ApiKeyStatus.None;

    internal GatewayAccessSectionViewModel(IEngineService engine, Localizer text, TimeZoneInfo zone)
    {
        _engine = engine;
        _text = text;
        _zone = zone;
        StatusText = text["GatewayAccess.None"];
    }

    /// <summary>Puts text on the clipboard; set by the view.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>The key being set: generated, typed or pasted; empty once it's saved.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(CopyCommand))]
    public partial string Key { get; set; } = string.Empty;

    /// <summary>"No API key: apps can only watch." or "An API key is set, since 26/10/2026 12:00."</summary>
    [ObservableProperty]
    public partial string StatusText { get; private set; }

    /// <summary>Whether a key is saved, so Remove is offered.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial bool IsSet { get; private set; }

    /// <summary>What went wrong, or that the key was saved and is to be copied now; null for nothing.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <summary>Whether <see cref="Message"/> is a problem, for its colour.</summary>
    [ObservableProperty]
    public partial bool HasProblem { get; private set; }

    /// <summary>Reads whether a key is set.</summary>
    internal async Task ShowAsync()
    {
        try
        {
            Show(await _engine.GetApiKeyStatusAsync());
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Problem(ex);
        }
    }

    private void Show(ApiKeyStatus status)
    {
        _status = status;
        IsSet = status.Set;
        StatusText =
            status is { Set: true, SetAt: { } at }
                ? _text.Format(
                    "GatewayAccess.Set",
                    new Dictionary<string, object?>
                    {
                        ["time"] = TimeZoneInfo.ConvertTime(at, _zone).ToString("g", _text.Culture),
                    }
                )
            : status.Set ? _text["GatewayAccess.SetNoTime"]
            : _text["GatewayAccess.None"];
    }

    /// <summary>A new random key in the field, to copy and then save.</summary>
    [RelayCommand]
    private async Task GenerateAsync()
    {
        try
        {
            Key = await _engine.GenerateApiKeyAsync();
            Message = null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Problem(ex);
        }
    }

    private bool HasKey => Key.Length > 0;

    /// <summary>Copies the key in the field: the only time it can be seen (behavior 6).</summary>
    [RelayCommand(CanExecute = nameof(HasKey))]
    private Task CopyAsync() => CopyToClipboard?.Invoke(Key) ?? Task.CompletedTask;

    /// <summary>Saves the key in the field; the old one stops working at once.</summary>
    [RelayCommand(CanExecute = nameof(HasKey))]
    private async Task SaveAsync()
    {
        try
        {
            Show(await _engine.SaveApiKeyAsync(Key.Trim()));
            Key = string.Empty;
            HasProblem = false;
            Message = _text["GatewayAccess.Saved"];
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            Problem(ex);
        }
    }

    /// <summary>Removes the key: apps can only watch.</summary>
    [RelayCommand(CanExecute = nameof(IsSet))]
    private async Task RemoveAsync()
    {
        try
        {
            await _engine.RemoveApiKeyAsync();
            Show(ApiKeyStatus.None);
            HasProblem = false;
            Message = _text["GatewayAccess.Removed"];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Problem(ex);
        }
    }

    private void Problem(Exception ex)
    {
        HasProblem = true;
        Message = _text.Describe(ex);
    }

    /// <summary>The texts again, in the new language.</summary>
    internal void Retranslate() => Show(_status);
}
