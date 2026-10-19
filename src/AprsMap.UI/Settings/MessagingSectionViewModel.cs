// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Settings;

/// <summary>
/// Settings › Messaging (docs/features/F2.13-ping.md): the active profile's ping responder, off by default, its
/// phrases, who may ping and how often it answers.
/// </summary>
public sealed partial class MessagingSectionViewModel(IEngineService engine, Localizer text, Action<AppSettings> saved)
    : ObservableObject
{
    private AppSettings _settings = AppSettings.Empty;

    /// <summary>"Ping for NZ0P-10", or null without a profile.</summary>
    [ObservableProperty]
    public partial string? Heading { get; private set; }

    /// <summary>Answer pings (behavior 1).</summary>
    [ObservableProperty]
    public partial bool On { get; set; }

    /// <summary>The ping phrase as typed; empty for the default.</summary>
    [ObservableProperty]
    public partial string PingPhrase { get; set; } = string.Empty;

    /// <summary>The pong phrase as typed; empty for the default.</summary>
    [ObservableProperty]
    public partial string PongPhrase { get; set; } = string.Empty;

    /// <summary>The default ping phrase, from the product's name, shown when none is typed.</summary>
    public static string DefaultPing => PingSettings.Off.EffectivePing;

    /// <summary>The default pong phrase.</summary>
    public static string DefaultPong => PingSettings.Off.EffectivePong;

    /// <summary>Anyone may ping (behavior 6.1).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsCallsigns))]
    public partial bool WhoAnyone { get; set; } = true;

    /// <summary>The profile's callsign, any SSID (behavior 6.2).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsCallsigns))]
    public partial bool WhoMyCallsign { get; set; }

    /// <summary>The callsigns listed (behavior 6.3).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsCallsigns))]
    public partial bool WhoCallsigns { get; set; }

    /// <summary>Whether the callsign list shows.</summary>
    public bool ShowsCallsigns => WhoCallsigns;

    /// <summary>The callsigns that may ping, one a line.</summary>
    [ObservableProperty]
    public partial string Callsigns { get; set; } = string.Empty;

    /// <summary>At most one pong in this many seconds, as typed (behavior 7).</summary>
    [ObservableProperty]
    public partial string IntervalSeconds { get; set; } = string.Empty;

    /// <summary>"Saved", once saved; null otherwise.</summary>
    [ObservableProperty]
    public partial string? SavedText { get; private set; }

    /// <summary>Why it couldn't be saved, in words; null when it was.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Shows the active profile's ping responder.</summary>
    internal void Show(AppSettings settings)
    {
        _settings = settings;
        var profile = settings.GetActiveProfile();
        Heading = profile is null
            ? null
            : text.Format("Ping.Heading", new Dictionary<string, object?> { ["profile"] = profile.Callsign });
        var ping = profile?.EffectivePing ?? PingSettings.Off;
        On = ping.On;
        PingPhrase = ping.Ping ?? string.Empty;
        PongPhrase = ping.Pong ?? string.Empty;
        WhoAnyone = ping.Who == PingWho.Anyone;
        WhoMyCallsign = ping.Who == PingWho.MyCallsign;
        WhoCallsigns = ping.Who == PingWho.Callsigns;
        Callsigns = string.Join(Environment.NewLine, ping.CallsignList);
        IntervalSeconds = ping.IntervalSeconds.ToString(CultureInfo.InvariantCulture);
        SavedText = null;
    }

    /// <summary>Rewords the heading after a language change.</summary>
    internal void Retranslate()
    {
        var profile = _settings.GetActiveProfile();
        Heading = profile is null
            ? null
            : text.Format("Ping.Heading", new Dictionary<string, object?> { ["profile"] = profile.Callsign });
    }

    /// <summary>Saves the active profile's ping responder; the engine checks the phrases and the interval.</summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        Error = SavedText = null;
        if (_settings.GetActiveProfile() is not { } profile)
        {
            return;
        }
        if (!int.TryParse(IntervalSeconds.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var interval))
        {
            Error = text["Error.settings.ping.interval-too-short"];
            return;
        }
        var ping = new PingSettings(
            On,
            PingPhrase.Trim().Length > 0 ? PingPhrase.Trim() : null,
            PongPhrase.Trim().Length > 0 ? PongPhrase.Trim() : null,
            WhoMyCallsign ? PingWho.MyCallsign
                : WhoCallsigns ? PingWho.Callsigns
                : PingWho.Anyone,
            [
                .. Callsigns
                    .Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(c => c.ToUpperInvariant()),
            ],
            interval
        );
        var settings = _settings with
        {
            Profiles = [.. _settings.Profiles.Select(p => p.Id == profile.Id ? p with { Ping = ping } : p)],
        };
        try
        {
            await engine.SaveSettingsAsync(settings);
            var stored = await engine.GetSettingsAsync();
            Show(stored);
            saved(stored);
            SavedText = text["Ping.Saved"];
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error = text.Describe(ex);
        }
    }
}
