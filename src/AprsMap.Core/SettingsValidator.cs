// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Packets;

namespace AprsMap.Core;

/// <summary>Checks settings before the engine stores or uses them.</summary>
internal static class SettingsValidator
{
    /// <summary>
    /// Throws <see cref="EngineArgumentException"/> when <paramref name="settings"/> is unusable, with a code the UI
    /// translates (docs/features/F0.9-localization.md) and an English message.
    /// </summary>
    public static void Validate(AppSettings settings, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Profiles is null || settings.Units is null)
        {
            throw Fail(EngineErrorCodes.SettingsIncomplete, "Settings are incomplete.");
        }

        // The source list (F1.25): ids unique, names given, an APRS-IS source with a server.
        var sources = new Dictionary<string, SourceSettings>(StringComparer.Ordinal);
        foreach (var source in settings.SourceList)
        {
            if (
                string.IsNullOrWhiteSpace(source.Id)
                || string.IsNullOrWhiteSpace(source.Name)
                || !sources.TryAdd(source.Id, source)
            )
            {
                throw Fail(EngineErrorCodes.SourceInvalid, "Every source needs its own id and a name.");
            }
            if (
                source.Kind == SourceKind.KissTcp
                && (
                    string.IsNullOrWhiteSpace(source.Host)
                    || source.Port is < 1 or > 65535
                    || source.TncPort is < 0 or > 15
                )
            )
            {
                throw Fail(
                    EngineErrorCodes.KissSourceInvalid,
                    "A KISS source needs a host, a port from 1 to 65535 and a TNC port from 0 to 15."
                );
            }
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in settings.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || !ids.Add(profile.Id))
            {
                throw Fail(EngineErrorCodes.ProfileIdDuplicate, "Every profile needs its own id.");
            }
            if (string.IsNullOrWhiteSpace(profile.Name))
            {
                throw Fail(EngineErrorCodes.ProfileNameMissing, "Every profile needs a name.");
            }
            if (!names.Add(profile.Name.Trim()))
            {
                throw Fail(
                    EngineErrorCodes.ProfileNameDuplicate,
                    $"There is more than one profile named '{profile.Name}'.",
                    profile.Name
                );
            }
            if (profile.Latitude is < -90 or > 90 || profile.Longitude is < -180 or > 180)
            {
                throw Fail(
                    EngineErrorCodes.ProfilePositionOffGlobe,
                    $"Profile '{profile.Name}': the location is off the globe.",
                    profile.Name
                );
            }
            if (profile.Latitude.HasValue != profile.Longitude.HasValue)
            {
                throw Fail(
                    EngineErrorCodes.ProfilePositionIncomplete,
                    $"Profile '{profile.Name}': a location needs both latitude and longitude.",
                    profile.Name
                );
            }

            if (profile.SourceIds.FirstOrDefault(id => !sources.ContainsKey(id)) is { } unknown)
            {
                throw Fail(
                    EngineErrorCodes.SourceUnknown,
                    $"Profile '{profile.Name}' uses a source that isn't in the list.",
                    profile.Name
                );
            }
            var aprsIs = profile.SourceIds.Select(id => sources[id]).Where(s => s.Kind == SourceKind.AprsIs).ToList();
            if (aprsIs.Count > 1)
            {
                throw Fail(
                    EngineErrorCodes.ProfileTwoAprsIs,
                    $"Profile '{profile.Name}' uses more than one APRS-IS source; one login at a time.",
                    profile.Name
                );
            }
            if (aprsIs.Count == 0)
            {
                continue; // nothing to log in to: allowed (F1.25 behavior 6)
            }

            try
            {
                AprsIsPacketSourceFactory.ClientOptions(
                    profile,
                    aprsIs[0].Server ?? string.Empty,
                    FilterComposer.Compose(profile, aprsIs[0].Filter, []),
                    time
                );
            }
            catch (AprsValidationException ex)
            {
                throw new EngineArgumentException(AprsErrors.From(ex, profile.Name), ex);
            }
            catch (ArgumentException ex)
            {
                // A check APRS.NET doesn't code (yet): still refused, with its English text.
                throw Fail(EngineErrorCodes.Invalid, $"Profile '{profile.Name}': {ex.Message}", profile.Name, ex);
            }
        }

        if (
            settings.Aging is { } aging
            && (
                aging.StaleAfterMinutes < 1
                || aging.RemoveAfterMinutes is { } remove && remove <= aging.StaleAfterMinutes
                || aging.TrackPoints is < 2 or > StationAging.MaxTrackPoints
                || aging.TrackMinutes < 1
            )
        )
        {
            throw Fail(
                EngineErrorCodes.StationAgingInvalid,
                "Stations go stale after a minute or more, and are removed only after they're stale; tracks keep 2 to "
                    + $"{StationAging.MaxTrackPoints} positions for a minute or more."
            );
        }

        var active = settings.ActiveProfileId;
        if (settings.Profiles.Count == 0 ? active is not null : !ids.Contains(active ?? string.Empty))
        {
            throw Fail(EngineErrorCodes.ActiveProfileMissing, "The active profile must be one of the profiles.");
        }
    }

    private static EngineArgumentException Fail(
        string code,
        string message,
        string? profile = null,
        Exception? inner = null
    ) =>
        new(
            new EngineError(
                code,
                message,
                profile is null ? null : new Dictionary<string, string> { [EngineErrorArgs.Profile] = profile }
            ),
            inner
        );
}
