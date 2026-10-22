// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Updates;

/// <summary>A channel choice; a null value follows the running build's own (F4.15 behavior 5).</summary>
/// <param name="Value">The channel, or null for the build's.</param>
/// <param name="Label">How it reads.</param>
public sealed record UpdateChannelOption(UpdateChannel? Value, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
/// Settings › Data › Updates (docs/features/F4.15-updates.md, behaviors 5, 6, 9.3, 10): Check for updates, the channel,
/// the last check with Check now, and a skipped version with Offer it again. Where updates are this viewer's: the
/// desktop's user, or a gateway's operator.
/// </summary>
public sealed partial class UpdateSettingsViewModel : ObservableObject
{
    private readonly IEngineService _engine;
    private readonly Localizer _text;
    private readonly UpdatesViewModel _updates;
    private readonly TimeZoneInfo _zone;
    private readonly Action<AppSettings> _saved;
    private AppSettings _settings = AppSettings.Empty;
    private bool _showing;

    internal UpdateSettingsViewModel(
        IEngineService engine,
        Localizer text,
        UpdatesViewModel updates,
        TimeZoneInfo zone,
        Action<AppSettings> saved
    )
    {
        _engine = engine;
        _text = text;
        _updates = updates;
        _zone = zone;
        _saved = saved;
        ChannelOptions =
        [
            new(null, text["Updates.Channel.Build"]),
            new(UpdateChannel.PreReleases, text["Updates.Channel.PreReleases"]),
            new(UpdateChannel.Stable, text["Updates.Channel.Stable"]),
        ];
        Channel = ChannelOptions[0];
        updates.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UpdatesViewModel.Status))
            {
                StatusText = _updates.Summary(_zone);
            }
        };
        StatusText = updates.Summary(zone);
    }

    /// <summary>Check for updates: on start and every two hours (behavior 1); saved as it's switched.</summary>
    [ObservableProperty]
    public partial bool CheckOn { get; set; } = true;

    /// <summary>The build's own, Pre-releases or Stable.</summary>
    public IReadOnlyList<UpdateChannelOption> ChannelOptions { get; }

    /// <summary>The channel; saved as it's chosen.</summary>
    [ObservableProperty]
    public partial UpdateChannelOption Channel { get; set; }

    /// <summary>"Skipped: 0.5.0-alpha.5"; null when no version is skipped (behavior 9.3).</summary>
    [ObservableProperty]
    public partial string? SkippedText { get; private set; }

    /// <summary>"Pre-releases · checked 21/10/2026 14:02 · up to date", as About says it (behavior 10).</summary>
    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    /// <summary>Why saving failed; null otherwise.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Shows <paramref name="settings"/>' update settings.</summary>
    internal void Show(AppSettings settings)
    {
        _settings = settings;
        _showing = true;
        try
        {
            var updates = settings.EffectiveUpdates;
            CheckOn = updates.Check;
            Channel = ChannelOptions.First(o => o.Value == updates.Channel);
            SkippedText = updates.SkippedVersion is { } skipped
                ? _text.Format("Updates.Skipped", new Dictionary<string, object?> { ["version"] = skipped })
                : null;
        }
        finally
        {
            _showing = false;
        }
    }

    partial void OnCheckOnChanged(bool value)
    {
        if (!_showing && value != _settings.EffectiveUpdates.Check)
        {
            _ = SaveAsync(
                _settings with
                {
                    Updates = _settings.EffectiveUpdates with { Check = value },
                },
                recheck: value
            );
        }
    }

    partial void OnChannelChanged(UpdateChannelOption value)
    {
        if (!_showing && value.Value != _settings.EffectiveUpdates.Channel)
        {
            _ = SaveAsync(
                _settings with
                {
                    Updates = _settings.EffectiveUpdates with { Channel = value.Value },
                },
                recheck: true
            );
        }
    }

    /// <summary>Offer it again: forgets the skipped version and checks, so it's offered if it's still the newest.</summary>
    [RelayCommand]
    private Task OfferAgainAsync() =>
        SaveAsync(
            _settings with
            {
                Updates = _settings.EffectiveUpdates with { SkippedVersion = null },
            },
            recheck: true
        );

    /// <summary>Check now, here as in About (behavior 10).</summary>
    [RelayCommand]
    private Task CheckNowAsync() => _updates.CheckAsync();

    private async Task SaveAsync(AppSettings settings, bool recheck)
    {
        Error = null;
        try
        {
            await _engine.SaveSettingsAsync(settings);
            _saved(settings);
            Show(settings);
            if (recheck && settings.EffectiveUpdates.Check)
            {
                await _updates.RefreshAsync();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = _text.Describe(ex);
        }
    }
}
