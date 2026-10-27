// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;

namespace AprsMap.Gateway;

/// <summary>
/// The first-run station in the TOML file (#429): <c>callsign</c>, <c>passcode</c>, <c>server</c> and <c>filter</c> are
/// used only when there are no settings yet; after that the stored station wins and the admin web app changes it. So
/// that an edit to them isn't silently ignored, each one the file sets that differs from the stored station is logged
/// at start, saying where to change it. Nothing is changed, and a passcode is never logged.
/// </summary>
internal static partial class TomlStationCheck
{
    /// <summary>The keys checked, as the file sets them (<c>[engine] callsign</c> is <c>Engine:Callsign</c>).</summary>
    private static readonly string[] Keys = ["Engine:Callsign", "Engine:Passcode", "Engine:Server", "Engine:Filter"];

    /// <summary>Whether the file sets any of <see cref="Keys"/>, so there's anything to check.</summary>
    internal static bool Applies(IReadOnlySet<string> inFile) => Keys.Any(inFile.Contains);

    /// <summary>
    /// What differs: for each of <see cref="Keys"/> in <paramref name="inFile"/>, its value in force (from
    /// <paramref name="configuration"/>, so an environment variable that wins is what's compared) against the active
    /// profile and its APRS-IS source.
    /// </summary>
    internal static IEnumerable<(string Key, string? Configured, string? Stored)> Differences(
        IReadOnlySet<string> inFile,
        IConfiguration configuration,
        AppSettings settings
    )
    {
        if (settings.GetActiveProfile() is not { } profile)
        {
            yield break; // no station yet: the next start's first run uses the file
        }
        var aprsIs = settings.AprsIsSourceOf(profile);
        foreach (var key in Keys.Where(inFile.Contains))
        {
            var configured = configuration[key]?.Trim();
            var (stored, same) = key switch
            {
                "Engine:Callsign" => (
                    profile.Callsign,
                    string.Equals(configured, profile.Callsign, StringComparison.OrdinalIgnoreCase)
                ),
                "Engine:Passcode" => (
                    null, // never logged
                    int.TryParse(configured, System.Globalization.CultureInfo.InvariantCulture, out var passcode)
                        && passcode == profile.Passcode
                ),
                "Engine:Server" => (
                    aprsIs?.Server,
                    string.Equals(configured ?? "", aprsIs?.Server ?? "", StringComparison.OrdinalIgnoreCase) // "" is none
                ),
                _ => (aprsIs?.Filter, string.Equals(configured ?? "", aprsIs?.Filter ?? "", StringComparison.Ordinal)),
            };
            if (!same)
            {
                yield return (key, key == "Engine:Passcode" ? null : configured, stored);
            }
        }
    }

    /// <summary>Logs each difference, once the engine has its settings.</summary>
    public static async Task WarnAsync(
        IReadOnlySet<string> inFile,
        IConfiguration configuration,
        IEngineService engine,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        AppSettings settings;
        try
        {
            settings = await engine.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        foreach (var (key, configured, stored) in Differences(inFile, configuration, settings))
        {
            var name = TomlName(key);
            if (key == "Engine:Passcode")
            {
                LogPasscodeIgnored(logger, name);
            }
            else
            {
                LogIgnored(logger, name, configured ?? "", stored ?? "(none)");
            }
        }
    }

    /// <summary><c>Engine:Callsign</c> as the file writes it: <c>[engine] callsign</c>.</summary>
    private static string TomlName(string key) => "[engine] " + key["Engine:".Length..].ToLowerInvariant();

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Setting} = \"{Configured}\" in the configuration is ignored: the station's stored value is {Stored}. Change it in the admin web app (Settings › Profiles or Sources)"
    )]
    private static partial void LogIgnored(ILogger logger, string setting, string configured, string stored);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{Setting} in the configuration is ignored: it differs from the station's stored passcode. Change it in the admin web app (Settings › Profiles)"
    )]
    private static partial void LogPasscodeIgnored(ILogger logger, string setting);
}
