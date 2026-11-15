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
    private readonly bool _installs;

    /// <summary>
    /// Whether Restart automatically is offered: an installed desktop build, which puts an update in by restarting
    /// (F4.15 behavior 11b).
    /// </summary>
    public bool ShowsAutoRestart => _installs;

    /// <summary>What Install updates automatically does here: the service gateway's, or the desktop's.</summary>
    public string AutoInstallDescription =>
        _text[_installs ? "Updates.Auto.DesktopDescription" : "Updates.Auto.Description"];

    /// <summary>Restart automatically (F4.15 behavior 11b), off by default; saved as it's switched.</summary>
    [ObservableProperty]
    public partial bool AutoRestartOn { get; set; }

    /// <summary>When it restarts, the computer's local time; 03:00 by default.</summary>
    [ObservableProperty]
    public partial TimeSpan? RestartTime { get; set; } = UpdateSettings.DefaultRestartTime.ToTimeSpan();

    partial void OnAutoRestartOnChanged(bool value)
    {
        if (!_showing && value != _settings.EffectiveUpdates.AutoRestart)
        {
            _ = SaveAsync(
                _settings with
                {
                    Updates = _settings.EffectiveUpdates with { AutoRestart = value },
                },
                recheck: false
            );
        }
    }

    partial void OnRestartTimeChanged(TimeSpan? value)
    {
        if (_showing || value is not { } time)
        {
            return;
        }
        var text = TimeOnly.FromTimeSpan(time).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        if (
            text
            != _settings.EffectiveUpdates.EffectiveRestartTime.ToString(
                "HH:mm",
                System.Globalization.CultureInfo.InvariantCulture
            )
        )
        {
            _ = SaveAsync(
                _settings with
                {
                    Updates = _settings.EffectiveUpdates with { AutoRestartTime = text },
                },
                recheck: false
            );
        }
    }

    internal UpdateSettingsViewModel(
        IEngineService engine,
        Localizer text,
        UpdatesViewModel updates,
        TimeZoneInfo zone,
        Action<AppSettings> saved,
        bool installs = false
    )
    {
        _installs = installs;
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
                ShowAutoInstall();
            }
        };
        StatusText = updates.Summary(zone);
        ShowAutoInstall();
    }

    /// <summary>
    /// Whether Install updates automatically is offered: the engine installs updates itself, the Linux service gateway
    /// (docs/features/F1.40-gateway-service-updates.md, behavior 8).
    /// </summary>
    [ObservableProperty]
    public partial bool ShowsAutoInstall { get; private set; }

    /// <summary>Install updates automatically (F1.40 behavior 8), off by default; saved as it's switched.</summary>
    [ObservableProperty]
    public partial bool AutoInstallOn { get; set; }

    /// <summary>
    /// "Installs 0.9.0 automatically after 16/11/2026 02:00", or that a failed version won't be; null with nothing to
    /// say.
    /// </summary>
    [ObservableProperty]
    public partial string? AutoInstallText { get; private set; }

    private void ShowAutoInstall()
    {
        var status = _updates.Status;
        var updates = _settings.EffectiveUpdates;
        ShowsAutoInstall = _installs || status is { CanInstall: true };
        AutoInstallText =
            _installs || !ShowsAutoInstall || !updates.AutoInstall ? null
            : updates.AutoInstallFailed is { } failed && status?.Update?.Version == failed
                ? _text.Format("Updates.Auto.Skipped", new Dictionary<string, object?> { ["version"] = failed })
            : status is { AutoInstallAt: { } at, Update: { } update }
                ? _text.Format(
                    "Updates.Auto.When",
                    new Dictionary<string, object?>
                    {
                        ["version"] = update.Version,
                        ["when"] = TimeZoneInfo.ConvertTime(at, _zone).ToString("g", _text.Culture),
                    }
                )
            : null;
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
            AutoInstallOn = updates.AutoInstall;
            AutoRestartOn = updates.AutoRestart;
            RestartTime = updates.EffectiveRestartTime.ToTimeSpan();
            Channel = ChannelOptions.First(o => o.Value == updates.Channel);
            SkippedText = updates.SkippedVersion is { } skipped
                ? _text.Format("Updates.Skipped", new Dictionary<string, object?> { ["version"] = skipped })
                : null;
        }
        finally
        {
            _showing = false;
        }
        ShowAutoInstall();
    }

    partial void OnAutoInstallOnChanged(bool value)
    {
        if (!_showing && value != _settings.EffectiveUpdates.AutoInstall)
        {
            _ = SaveAsync(
                _settings with
                {
                    Updates = _settings.EffectiveUpdates with { AutoInstall = value },
                },
                recheck: true
            );
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
