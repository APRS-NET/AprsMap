// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Settings;

/// <summary>
/// Settings › Messaging (docs/features/F2.13-ping.md, F2.10-auto-reply.md): the active profile's ping responder and
/// away message, both off by default, with their phrases, who may ping and how often each answers.
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

    /// <summary>"Auto-reply for NZ0P-10", or null without a profile.</summary>
    [ObservableProperty]
    public partial string? AutoReplyHeading { get; private set; }

    /// <summary>Send the away message (F2.10 behavior 1).</summary>
    [ObservableProperty]
    public partial bool AutoReplyOn { get; set; }

    /// <summary>The away message as typed; empty for the default (behavior 2).</summary>
    [ObservableProperty]
    public partial string AutoReplyText { get; set; } = string.Empty;

    /// <summary>The default away message, shown when none is typed.</summary>
    public static string DefaultAutoReply => AutoReplySettings.DefaultText;

    /// <summary>Minutes without a message sent by hand, as typed (behavior 4).</summary>
    [ObservableProperty]
    public partial string AwayMinutes { get; set; } = string.Empty;

    /// <summary>Once per correspondent in this many minutes, as typed (behavior 5).</summary>
    [ObservableProperty]
    public partial string OncePerMinutes { get; set; } = string.Empty;

    /// <summary>Seconds to wait first, as typed (behavior 6).</summary>
    [ObservableProperty]
    public partial string DelaySeconds { get; set; } = string.Empty;

    /// <summary>Answer APRS queries (docs/features/F2.8-queries.md, behavior 1); on by default.</summary>
    [ObservableProperty]
    public partial bool AnswerQueries { get; set; } = true;

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
        var ping = profile?.EffectivePing ?? PingSettings.Off;
        On = ping.On;
        PingPhrase = ping.Ping ?? string.Empty;
        PongPhrase = ping.Pong ?? string.Empty;
        WhoAnyone = ping.Who == PingWho.Anyone;
        WhoMyCallsign = ping.Who == PingWho.MyCallsign;
        WhoCallsigns = ping.Who == PingWho.Callsigns;
        Callsigns = string.Join(Environment.NewLine, ping.CallsignList);
        IntervalSeconds = ping.IntervalSeconds.ToString(CultureInfo.InvariantCulture);
        AnswerQueries = profile?.EffectiveAnswerQueries ?? true;
        var reply = profile?.EffectiveAutoReply ?? AutoReplySettings.Off;
        AutoReplyOn = reply.On;
        AutoReplyText = reply.Text ?? string.Empty;
        AwayMinutes = reply.AwayMinutes.ToString(CultureInfo.InvariantCulture);
        OncePerMinutes = reply.OncePerMinutes.ToString(CultureInfo.InvariantCulture);
        DelaySeconds = reply.DelaySeconds.ToString(CultureInfo.InvariantCulture);
        Retranslate();
        SavedText = null;
    }

    /// <summary>Words the headings, and again after a language change.</summary>
    internal void Retranslate()
    {
        var profile = _settings.GetActiveProfile();
        Heading = profile is null
            ? null
            : text.Format("Ping.Heading", new Dictionary<string, object?> { ["profile"] = profile.Callsign });
        AutoReplyHeading = profile is null
            ? null
            : text.Format("AutoReply.Heading", new Dictionary<string, object?> { ["profile"] = profile.Callsign });
    }

    /// <summary>
    /// Saves the active profile's ping responder and away message; the engine checks the phrases, the text and the
    /// times.
    /// </summary>
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
        if (
            !TryNumber(AwayMinutes, out var away)
            || !TryNumber(OncePerMinutes, out var oncePer)
            || !TryNumber(DelaySeconds, out var delay)
        )
        {
            Error = text["Error.settings.auto-reply.limit-invalid"];
            return;
        }
        var reply = new AutoReplySettings(
            AutoReplyOn,
            AutoReplyText.Trim().Length > 0 ? AutoReplyText.Trim() : null,
            away,
            oncePer,
            delay
        );
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
            Profiles =
            [
                .. _settings.Profiles.Select(p =>
                    p.Id == profile.Id
                        ? p with
                        {
                            Ping = ping,
                            AutoReply = reply,
                            AnswerQueries = AnswerQueries ? null : false,
                        }
                        : p
                ),
            ],
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

    private static bool TryNumber(string typed, out int value) =>
        int.TryParse(typed.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
