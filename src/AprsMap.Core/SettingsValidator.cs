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

        if (settings.History is { IsValid: false })
        {
            throw Fail(
                EngineErrorCodes.HistoryInvalid,
                $"History is kept for 0 to {HistorySettings.MaxDays} days, within {HistorySettings.MinMegabytes} to {HistorySettings.MaxMegabytesLimit} MB."
            ); // F1.41 behavior 2
        }

        // The source list (F1.25): ids unique, names given, an APRS-IS source with a server.
        var sources = new Dictionary<string, SourceSettings>(StringComparer.Ordinal);
        foreach (var source in settings.SourceList)
        {
            ValidateDigipeat(source); // F3.5
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
            if (
                source.Kind == SourceKind.Gps
                && (
                    source.GpsLink is not { } link
                    || (
                        link == GpsLink.Serial
                            ? string.IsNullOrWhiteSpace(source.SerialPort) || source.Baud is <= 0
                            : string.IsNullOrWhiteSpace(source.Host) || source.Port is < 1 or > 65535
                    )
                )
            )
            {
                throw Fail(
                    EngineErrorCodes.GpsSourceInvalid,
                    "A GPS needs its serial port and a speed above 0, or a host and a port from 1 to 65535."
                );
            }
        }

        foreach (var source in settings.SourceList)
        {
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
            ValidatePing(profile);
            ValidateAutoReply(profile);
            ValidateObjects(profile);
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
            ValidateStationLists(profile);
            // One GPS a profile (F3.6 decision 2): a GPS source and a radio whose GPS is On would be two; a radio on Auto
            // steps aside (docs/features/F3.13-tnc-gps.md, behavior 4).
            if (
                profile.SourceIds.Count(id => sources[id].Kind == SourceKind.Gps)
                    + (
                        profile.SourceIds.Any(id => sources[id].Kind == SourceKind.Gps)
                            ? profile.SourceIds.Count(id => sources[id].EffectiveTncGps == TncGps.On)
                            : 0
                    )
                > 1
            )
            {
                throw Fail(
                    EngineErrorCodes.ProfileTwoGps,
                    $"Profile '{profile.Name}' uses more than one GPS; the station is in one place.",
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

        if (settings.Logging is { } logging)
        {
            ValidateLogging(logging);
        }
    }

    /// <summary>
    /// The profile's objects and items (docs/features/F2.7-objects-and-items.md, behavior 2): names that fit their kind
    /// and are unique, places on the globe, symbols that aren't the emergency symbol, comments a report carries, and
    /// intervals in range.
    /// </summary>
    private static void ValidateObjects(StationProfile profile)
    {
        var objects = profile.ObjectList;
        if (objects.Count > OwnObject.MaxPerProfile)
        {
            throw Fail(
                EngineErrorCodes.ObjectInvalid,
                $"Profile '{profile.Name}': at most {OwnObject.MaxPerProfile} objects.",
                profile.Name
            );
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var placed in objects)
        {
            if (!IsObjectName(placed.Name, placed.Kind))
            {
                throw Fail(
                    EngineErrorCodes.ObjectNameInvalid,
                    $"Profile '{profile.Name}': an object's name has 1 to 9 characters, an item's 3 to 9 without ! or "
                        + "_, plain ASCII with no space at either end.",
                    profile.Name
                );
            }
            if (!names.Add(placed.Name))
            {
                throw Fail(
                    EngineErrorCodes.ObjectNameTaken,
                    $"Profile '{profile.Name}': two objects are named '{placed.Name}'.",
                    profile.Name
                );
            }
            if (
                placed.Latitude is < -90 or > 90 or double.NaN
                || placed.Longitude is < -180 or > 180 or double.NaN
                || !IsSymbol(placed.Symbol)
                || placed.Symbol == EmergencySymbol
                || placed.Comment.Length > OwnObject.MaxCommentLength
                || placed.Comment.Any(c => c is < ' ' or >= '~' or '|')
                || placed.IntervalMinutes is < BeaconSettings.MinIntervalMinutes or > BeaconSettings.MaxIntervalMinutes
            )
            {
                throw Fail(
                    EngineErrorCodes.ObjectInvalid,
                    $"Profile '{profile.Name}': object '{placed.Name}' needs a place on the globe, a symbol other than "
                        + $"the emergency symbol, a comment of at most {OwnObject.MaxCommentLength} plain characters "
                        + "and an interval of 10 minutes to 24 hours.",
                    profile.Name
                );
            }
        }
    }

    /// <summary>The emergency symbol, which the app never sends (docs/features/F2.12-mic-e-status.md, decision 3).</summary>
    internal const string EmergencySymbol = "\\!";

    /// <summary>An object's name is 1 to 9 characters, an item's 3 to 9 without ! or _ (APRS101 chapter 11).</summary>
    internal static bool IsObjectName(string name, OwnObjectKind kind) =>
        name.Length is >= 1 and <= 9
        && name.All(c => c is >= ' ' and <= '~')
        && name[0] != ' '
        && name[^1] != ' '
        && (kind == OwnObjectKind.Object || (name.Length >= 3 && !name.Contains('!') && !name.Contains('_')));

    /// <summary>
    /// The away message (docs/features/F2.10-auto-reply.md): its text one message of plain characters (behavior 2),
    /// and its times in range (behaviors 4–6).
    /// </summary>
    private static void ValidateAutoReply(StationProfile profile)
    {
        if (profile.AutoReply is not { } reply)
        {
            return;
        }
        if (
            reply.Text is { } text
            && (text.Trim().Length is 0 or > 67 || text.Any(c => c is < ' ' or > '~' or '|' or '{'))
        )
        {
            throw Fail(
                EngineErrorCodes.AutoReplyTextInvalid,
                $"Profile '{profile.Name}': an auto-reply has 1 to 67 plain characters, without |, ~ or {{.",
                profile.Name
            );
        }
        if (
            reply.AwayMinutes is < 0 or > AutoReplySettings.MaxMinutes
            || reply.OncePerMinutes is < 1 or > AutoReplySettings.MaxMinutes
            || reply.DelaySeconds is < 0 or > AutoReplySettings.MaxDelaySeconds
        )
        {
            throw Fail(
                EngineErrorCodes.AutoReplyLimitInvalid,
                $"Profile '{profile.Name}': auto-reply waits 0 to 1440 minutes away, once per 1 to 1440 minutes, "
                    + "after 0 to 600 seconds.",
                profile.Name
            );
        }
    }

    /// <summary>
    /// The ping responder (docs/features/F2.13-ping.md, behaviors 3, 6, 7): phrases a message can carry, distinct, the
    /// pong not starting with the ping (or two stations would answer each other); the interval; the callsigns.
    /// </summary>
    private static void ValidatePing(StationProfile profile)
    {
        if (profile.Ping is not { } ping)
        {
            return;
        }
        foreach (var phrase in new[] { ping.Ping, ping.Pong })
        {
            if (
                phrase is not null
                && (
                    phrase.Trim().Length is 0 or > PingSettings.MaxPhraseLength
                    || phrase.Any(c => c is < ' ' or > '~' or '|' or '{')
                )
            )
            {
                throw Fail(
                    EngineErrorCodes.PingPhraseInvalid,
                    $"Profile '{profile.Name}': a ping or pong phrase has 1 to {PingSettings.MaxPhraseLength} plain "
                        + "characters, without |, ~ or {.",
                    profile.Name
                );
            }
        }
        if (ping.EffectivePong.StartsWith(ping.EffectivePing, StringComparison.OrdinalIgnoreCase))
        {
            throw Fail(
                EngineErrorCodes.PingPhrasesSame,
                $"Profile '{profile.Name}': the pong phrase can't be or start with the ping phrase.",
                profile.Name
            );
        }
        if (ping.IntervalSeconds is < PingSettings.MinIntervalSeconds or > PingSettings.MaxIntervalSeconds)
        {
            throw Fail(
                EngineErrorCodes.PingIntervalTooShort,
                $"Profile '{profile.Name}': at most one pong every {PingSettings.MinIntervalSeconds} seconds, "
                    + "and at least one a day.",
                profile.Name
            );
        }
        foreach (var entry in ping.CallsignList)
        {
            var call = entry.Trim().TrimEnd('*');
            if (
                call.Length == 0
                || !Callsign.TryParse(call, out _)
                    && !(entry.Trim().EndsWith('*') && call.All(char.IsAsciiLetterOrDigit))
            )
            {
                throw Fail(
                    EngineErrorCodes.PingCallsignInvalid,
                    $"Profile '{profile.Name}': '{entry}' isn't a callsign that may ping.",
                    profile.Name
                );
            }
        }
    }

    /// <summary>The log file's limits and the OTLP targets (docs/features/F1.32-logging.md, behaviors 9–13).</summary>
    private static void ValidateLogging(LoggingSettings logging)
    {
        var file = logging.EffectiveFile;
        CheckRange(file.MaxFileMegabytes, LogFileSettings.FileMegabytesRange, "maxFileMegabytes");
        CheckRange(file.KeepDays, LogFileSettings.KeepDaysRange, "keepDays");
        CheckRange(file.TotalMegabytes, LogFileSettings.TotalMegabytesRange, "totalMegabytes");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in logging.TargetList)
        {
            if (string.IsNullOrWhiteSpace(target.Id) || !ids.Add(target.Id) || string.IsNullOrWhiteSpace(target.Name))
            {
                throw TargetInvalid(target.Name, "Every OTLP target needs its own id and a name.");
            }
            if (
                !Uri.TryCreate(target.Endpoint, UriKind.Absolute, out var endpoint)
                || endpoint.Scheme is not ("http" or "https")
            )
            {
                throw TargetInvalid(target.Name, $"OTLP target '{target.Name}' needs an http or https address.");
            }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in target.HeaderList)
            {
                // An HTTP header name: a token of letters, digits and !#$%&'*+-.^_`|~ (RFC 9110).
                if (
                    string.IsNullOrEmpty(header.Name)
                    || !header.Name.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c))
                    || !names.Add(header.Name)
                    || header.Value?.Any(c => c is '\r' or '\n') == true
                )
                {
                    throw TargetInvalid(target.Name, $"OTLP target '{target.Name}' has a header that can't be sent.");
                }
            }
        }

        static void CheckRange(int value, (int Min, int Max) range, string field)
        {
            if (value < range.Min || value > range.Max)
            {
                throw new EngineArgumentException(
                    new EngineError(
                        EngineErrorCodes.LoggingLimitInvalid,
                        $"The log file's {field} must be from {range.Min} to {range.Max}.",
                        new Dictionary<string, string>
                        {
                            [EngineErrorArgs.Field] = field,
                            ["min"] = range.Min.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["max"] = range.Max.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        }
                    )
                );
            }
        }

        static EngineArgumentException TargetInvalid(string name, string message) =>
            new(
                new EngineError(
                    EngineErrorCodes.LoggingTargetInvalid,
                    message,
                    new Dictionary<string, string> { ["target"] = name ?? string.Empty }
                )
            );
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
        if (!IsSendableStatus(status.Text, status.Add))
        {
            throw Fail(
                EngineErrorCodes.ProfileStatusInvalid,
                $"Profile '{profile.Name}': a status has at most {StatusBeaconSettings.MaxTextLength} characters of "
                    + "printable ASCII.",
                profile.Name
            );
        }
        if (
            status.SavedTextList.Count > StatusBeaconSettings.MaxSavedTexts
            || status.SavedTextList.Any(t => t.Trim().Length == 0 || !IsSendableStatus(t))
        )
        {
            throw Fail(
                EngineErrorCodes.ProfileSavedStatusInvalid,
                $"Profile '{profile.Name}': at most {StatusBeaconSettings.MaxSavedTexts} saved status texts, each one "
                    + "a status can carry.",
                profile.Name
            );
        }
        if (position.Moving is { } moving)
        {
            try
            {
                Beaconer.SmartSettings(moving).Validate();
            }
            catch (ArgumentException ex)
            {
                throw Fail(
                    EngineErrorCodes.ProfileMovingInvalid,
                    $"Profile '{profile.Name}': the moving schedule can't be used: {ex.Message}",
                    profile.Name,
                    ex
                );
            }
        }
        if (position.MicE is not null && (position.Compressed || position.Phg is not null))
        {
            throw Fail(
                EngineErrorCodes.ProfileMicEInvalid,
                $"Profile '{profile.Name}': a Mic-E position isn't compressed and carries no PHG.",
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
    internal static bool IsSendableStatus(string text, StatusAddition add = StatusAddition.None) =>
        text.Length <= StatusBeaconSettings.MaxTextLengthWith(add) && text.All(c => c is >= ' ' and <= '~');

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

    /// <summary>
    /// A profile's station lists (docs/features/F1.33-station-lists.md, behaviors 1–3): each named, names not repeated,
    /// every line an entry, and no more than the limits (decision 6).
    /// </summary>
    private static void ValidateStationLists(StationProfile profile)
    {
        var lists = profile.StationListSet;
        if (lists.Count > StationList.MaxLists)
        {
            throw Fail(
                EngineErrorCodes.StationListTooMany,
                $"Profile '{profile.Name}' has more than {StationList.MaxLists} station lists.",
                profile.Name
            );
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in lists)
        {
            if (string.IsNullOrWhiteSpace(list.Name) || !names.Add(list.Name.Trim()))
            {
                throw new EngineArgumentException(
                    new EngineError(
                        EngineErrorCodes.StationListNameTaken,
                        $"Each station list needs a name of its own; '{list.Name}' is taken or empty.",
                        new Dictionary<string, string> { [EngineErrorArgs.List] = list.Name }
                    )
                );
            }
            var read = StationListText.Parse(list.Text);
            if (read.Errors.Count > 0)
            {
                var first = read.Errors[0];
                throw new EngineArgumentException(
                    new EngineError(
                        EngineErrorCodes.StationListEntryInvalid,
                        $"Line {first.Line} of '{list.Name}' isn't a callsign, a prefix with * or an object name: {first.Text}",
                        new Dictionary<string, string>
                        {
                            [EngineErrorArgs.List] = list.Name,
                            [EngineErrorArgs.Line] = first.Line.ToString(
                                System.Globalization.CultureInfo.InvariantCulture
                            ),
                            [EngineErrorArgs.Value] = first.Text.Trim(),
                        }
                    )
                );
            }
            if (read.Entries.Count > StationList.MaxEntries)
            {
                throw new EngineArgumentException(
                    new EngineError(
                        EngineErrorCodes.StationListTooMany,
                        $"'{list.Name}' has more than {StationList.MaxEntries} entries.",
                        new Dictionary<string, string> { [EngineErrorArgs.List] = list.Name }
                    )
                );
            }
        }
    }

    /// <summary>A TNC's digipeater aliases (docs/features/F3.5-digipeater.md, behavior 1): each an AX.25 call.</summary>
    private static void ValidateDigipeat(SourceSettings source)
    {
        foreach (var alias in source.DigipeatAliasList)
        {
            if (!AprsNet.Ax25.Ax25Address.TryParse(alias, out _))
            {
                throw new EngineArgumentException(
                    new EngineError(
                        EngineErrorCodes.DigipeatAliasInvalid,
                        $"'{alias}' can't be a digipeater alias: up to six letters and digits, and an SSID.",
                        new Dictionary<string, string>
                        {
                            [EngineErrorArgs.Source] = source.Name,
                            [EngineErrorArgs.Value] = alias,
                        }
                    )
                );
            }
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
