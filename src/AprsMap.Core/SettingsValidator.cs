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
            if (
                source.IsSerial
                && (
                    string.IsNullOrWhiteSpace(source.SerialPort) || source.Baud is <= 0 || source.TncPort is < 0 or > 15
                )
            )
            {
                throw Fail(
                    EngineErrorCodes.KissSerialSourceInvalid,
                    "A KISS over serial source needs a port, a speed above 0 and a TNC port from 0 to 15."
                );
            }
        }

        foreach (var source in settings.SourceList)
        {
            if (source.Transmit && !source.CanTransmit)
            {
                throw Fail(EngineErrorCodes.SourceReceiveOnly, $"Source '{source.Name}' can only receive.");
            }
            if (source.Kind == SourceKind.KissSerial && source.TncType is { Length: > 0 })
            {
                if (source.TncType == SourceSettings.CustomTncType && string.IsNullOrWhiteSpace(source.EnterScript))
                {
                    throw Fail(
                        EngineErrorCodes.TncScriptInvalid,
                        $"Source '{source.Name}': a custom TNC needs a script."
                    );
                }
                try
                {
                    _ = AprsIsPacketSourceFactory.SetupOf(source);
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException)
                {
                    throw Fail(EngineErrorCodes.TncScriptInvalid, $"Source '{source.Name}': {ex.Message}");
                }
            }
        }

        foreach (var source in settings.SourceList.Where(s => s.Kind != SourceKind.AprsIs && s.TxPath is not null))
        {
            if (!IsTxPath(source.EffectiveTxPath))
            {
                throw Fail(
                    EngineErrorCodes.SourceTxPathInvalid,
                    $"Source '{source.Name}': a TX path is up to 8 digipeater aliases or callsigns, separated by commas."
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
            if (profile.Comment is { } comment && !IsSendableComment(comment))
            {
                throw Fail(
                    EngineErrorCodes.ProfileCommentInvalid,
                    $"Profile '{profile.Name}': a comment has at most {StationProfile.MaxCommentLength} characters of "
                        + "printable ASCII.",
                    profile.Name
                );
            }
            ValidateBeacons(profile);
            if (profile.Symbol is { Length: > 0 } symbol && !IsSymbol(symbol))
            {
                throw Fail(
                    EngineErrorCodes.ProfileSymbolInvalid,
                    $"Profile '{profile.Name}': a symbol is a table or overlay character and a symbol code.",
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

    /// <summary>Whether <paramref name="comment"/> fits a position report (F2.1 behavior 2).</summary>
    internal static bool IsSendableComment(string comment) =>
        comment.Length <= StationProfile.MaxCommentLength && comment.All(c => c is >= ' ' and <= '~');

    /// <summary>The beacons' settings (docs/features/F2.2-beacons.md, behaviors 1, 3).</summary>
    private static void ValidateBeacons(StationProfile profile)
    {
        var position = profile.EffectivePositionBeacon;
        var status = profile.EffectiveStatusBeacon;
        if (!IsInterval(position.IntervalMinutes) || !IsInterval(status.IntervalMinutes))
        {
            throw Fail(
                EngineErrorCodes.ProfileBeaconIntervalInvalid,
                $"Profile '{profile.Name}': a beacon interval is {BeaconSettings.MinIntervalMinutes} minutes to 24 hours.",
                profile.Name
            );
        }
        if (!IsSendableStatus(status.Text))
        {
            throw Fail(
                EngineErrorCodes.ProfileStatusInvalid,
                $"Profile '{profile.Name}': a status has at most {StatusBeaconSettings.MaxTextLength} characters of "
                    + "printable ASCII.",
                profile.Name
            );
        }
        if (position.Phg is { } phg && (position.Compressed || !IsPhg(phg)))
        {
            throw Fail(
                EngineErrorCodes.ProfilePhgInvalid,
                $"Profile '{profile.Name}': PHG takes the values the format carries, and only with an uncompressed "
                    + "position.",
                profile.Name
            );
        }
    }

    private static bool IsInterval(int minutes) =>
        minutes is >= BeaconSettings.MinIntervalMinutes and <= BeaconSettings.MaxIntervalMinutes;

    /// <summary>Whether <paramref name="text"/> fits a status report without a timestamp (F2.2 behavior 3).</summary>
    internal static bool IsSendableStatus(string text) =>
        text.Length <= StatusBeaconSettings.MaxTextLength && text.All(c => c is >= ' ' and <= '~');

    /// <summary>
    /// Whether APRS.NET sends <paramref name="phg"/> exactly: it rounds what the format can't carry, so the value is
    /// written and read back, and must come back the same.
    /// </summary>
    internal static bool IsPhg(PhgSettings phg)
    {
        try
        {
            var chosen = Beaconer.PowerHeightGain(phg);
            var packet = PacketAddress
                .Parse("N0CALL", "APZ000", [])
                .Create(
                    new PositionReport
                    {
                        Position = new AprsPosition(0, 0),
                        Symbol = new AprsSymbol('/', '-'),
                        PowerHeightGain = chosen,
                    }
                );
            return AprsPacket.Parse(packet.ToString()).Payload is PositionedPayload { PowerHeightGain: { } back }
                && back == chosen;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Whether <paramref name="path"/> is a path APRS.NET can send on RF (F2.1 behavior 5.2).</summary>
    internal static bool IsTxPath(string path)
    {
        try
        {
            var entries = path.Split(',', StringSplitOptions.TrimEntries);
            _ = PacketAddress.Parse("N0CALL", "APZ000", entries);
            return entries.Length <= 8;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Whether <paramref name="symbol"/> is a table or overlay character and a code APRS.NET accepts.</summary>
    internal static bool IsSymbol(string symbol) =>
        symbol.Length == 2 && AprsSymbol.TryCreate(symbol[0], symbol[1], out _);

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
