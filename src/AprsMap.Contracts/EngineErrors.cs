// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>
/// Something the engine reports to a user, as a code the UI translates rather than a sentence
/// (docs/features/F0.9-localization.md, behaviors 11–15). On the web the engine runs on the gateway and can't know
/// each viewer's language.
/// </summary>
/// <param name="Code">Stable identifier, one of <see cref="EngineErrorCodes"/> or <c>aprs.*</c> for APRS.NET's codes.</param>
/// <param name="Message">English, for logs and for a UI that doesn't know <paramref name="Code"/>.</param>
/// <param name="Args">Values for the translated text's placeholders, keyed by name (<see cref="EngineErrorArgs"/>).</param>
public sealed record EngineError(string Code, string Message, IReadOnlyDictionary<string, string>? Args = null)
{
    /// <summary>The value of <paramref name="name"/>, or null when it isn't set.</summary>
    public string? Arg(string name) => Args is not null && Args.TryGetValue(name, out var value) ? value : null;
}

/// <summary>The engine's error codes. Codes are never renamed; a UI shows the English message for codes it doesn't know.</summary>
public static class EngineErrorCodes
{
    /// <summary>Prefix of the codes mapped from APRS.NET's <c>AprsValidationErrorCode</c>, in kebab case.</summary>
    public const string AprsPrefix = "aprs.";

    /// <summary>
    /// Prefix of a packet's parse issues (F1.8 behavior 3): APRS.NET's <c>ParseIssueCode</c> in kebab case, for
    /// example <c>aprs.parse.invalid-latitude</c>.
    /// </summary>
    public const string AprsParsePrefix = "aprs.parse.";

    /// <summary>Two sources share an id, or a source has no name.</summary>
    public const string SourceInvalid = "settings.source.invalid";

    /// <summary>A profile uses a source that isn't in the list.</summary>
    public const string SourceUnknown = "settings.profile.source-unknown";

    /// <summary>A source of a kind not built yet (KISS before 0.4.0, say) can't be opened.</summary>
    public const string SourceKindNotBuilt = "source.kind-not-built";

    /// <summary>A profile uses more than one APRS-IS source (F1.25 behavior 3).</summary>
    public const string ProfileTwoAprsIs = "settings.profile.two-aprs-is";

    /// <summary>Settings were sent without profiles or units.</summary>
    public const string SettingsIncomplete = "settings.incomplete";

    /// <summary>Two profiles share an id, or one has none.</summary>
    public const string ProfileIdDuplicate = "settings.profile.id-duplicate";

    /// <summary>A profile has no name.</summary>
    public const string ProfileNameMissing = "settings.profile.name-missing";

    /// <summary>Two profiles have the same name (<see cref="EngineErrorArgs.Profile"/>).</summary>
    public const string ProfileNameDuplicate = "settings.profile.name-duplicate";

    /// <summary>A profile's location is off the globe.</summary>
    public const string ProfilePositionOffGlobe = "settings.profile.position-off-globe";

    /// <summary>A profile's comment is too long or has characters other than printable ASCII (F2.1 behavior 2).</summary>
    public const string ProfileCommentInvalid = "settings.profile.comment-invalid";

    /// <summary>A TNC's TX path isn't a list of digipeater aliases or callsigns APRS.NET can send (F2.1 behavior 5.2).</summary>
    public const string SourceTxPathInvalid = "settings.source.tx-path-invalid";

    /// <summary>A beacon's interval is outside 10 minutes to 24 hours (docs/features/F2.2-beacons.md, behaviors 1, 3).</summary>
    public const string ProfileBeaconIntervalInvalid = "settings.profile.beacon-interval-invalid";

    /// <summary>The status beacon's text is longer than 62 characters or not printable ASCII (F2.2 behavior 3).</summary>
    public const string ProfileStatusInvalid = "settings.profile.status-invalid";

    /// <summary>The PHG isn't values the format carries, or comes with a compressed position (F2.2 behavior 1.4).</summary>
    public const string ProfilePhgInvalid = "settings.profile.phg-invalid";

    /// <summary>A profile's symbol isn't a table or overlay character and a symbol code (F2.1 behavior 1).</summary>
    public const string ProfileSymbolInvalid = "settings.profile.symbol-invalid";

    /// <summary>A profile has a latitude without a longitude, or the other way round.</summary>
    public const string ProfilePositionIncomplete = "settings.profile.position-incomplete";

    /// <summary>The station aging times are out of range: stale after 1 minute or more, removal after the stale time.</summary>
    public const string StationAgingInvalid = "settings.stations.aging-invalid";

    /// <summary>The active profile isn't one of the profiles.</summary>
    public const string ActiveProfileMissing = "settings.active-profile-missing";

    /// <summary>APRS.NET rejected a value for a reason this version doesn't map (<see cref="EngineErrorArgs.Value"/>).</summary>
    public const string Invalid = "settings.invalid";

    /// <summary>A Maidenhead locator was left empty.</summary>
    public const string LocatorEmpty = "maidenhead.empty";

    /// <summary>A Maidenhead locator has valid characters but not 4, 6, 8 or 10 of them (<see cref="EngineErrorArgs.Value"/>).</summary>
    public const string LocatorWrongLength = "maidenhead.wrong-length";

    /// <summary>
    /// A Maidenhead locator character is out of range for its position (<see cref="EngineErrorArgs.Value"/>,
    /// <see cref="EngineErrorArgs.Index"/>, <see cref="EngineErrorArgs.Character"/>, <see cref="EngineErrorArgs.Part"/>).
    /// </summary>
    public const string LocatorInvalidCharacter = "maidenhead.invalid-character";

    /// <summary>A position is off the globe.</summary>
    public const string PositionOffGlobe = "position.off-globe";

    /// <summary>A gateway's settings can't be changed from the browser.</summary>
    public const string SettingsReadOnly = "gateway.settings-read-only";

    /// <summary>Only the gateway's operator may do this (address lookup, the passcode calculator).</summary>
    public const string OperatorOnly = "gateway.operator-only";

    /// <summary>A sign-in's name and password don't match an operator's; the same for either (F2.9 behavior 7).</summary>
    public const string AuthWrongCredentials = "auth.wrong-credentials";

    /// <summary>
    /// Too many failed sign-ins from this address or for this name: wait <see cref="EngineErrorArgs.Minutes"/> (F2.9
    /// behavior 9).
    /// </summary>
    public const string AuthTooManyTries = "auth.too-many-tries";

    /// <summary>Sign-in is offered over HTTPS only, or from the gateway's own host (F2.9 behavior 8.1).</summary>
    public const string AuthNeedsHttps = "auth.needs-https";

    /// <summary>This gateway offers no sign-in here: no operator is set up, or this isn't its operator port (behavior 8).</summary>
    public const string AuthNotOfferedHere = "auth.not-offered-here";

    /// <summary>The gateway has as many viewers as it allows, in all or from this address (F1.14 behavior 3).</summary>
    public const string GatewayBusy = "gateway.busy";

    /// <summary>This viewer made too many calls in a short time (F1.14 behavior 4).</summary>
    public const string GatewayRateLimited = "gateway.rate-limited";

    /// <summary>The app and the gateway speak different contract versions: the page needs reloading (F1.14 behavior 8).</summary>
    public const string GatewayOutdated = "gateway.outdated";

    /// <summary>Address lookup is turned off.</summary>
    public const string GeocodingDisabled = "geocoding.disabled";

    /// <summary>The lookup server refused requests (HTTP 429 or 403); lookups pause for a minute.</summary>
    public const string GeocodingRefused = "geocoding.refused";

    /// <summary>A newer lookup replaced this one while it waited.</summary>
    public const string GeocodingSuperseded = "geocoding.superseded";

    /// <summary>The station's Internet switch is off (F1.15), so nothing is looked up.</summary>
    public const string GeocodingOffline = "geocoding.offline";

    /// <summary>The lookup server couldn't be reached.</summary>
    public const string GeocodingUnreachable = "geocoding.unreachable";

    /// <summary>The lookup server answered with an HTTP error (<see cref="EngineErrorArgs.Status"/>).</summary>
    public const string GeocodingHttpError = "geocoding.http-error";

    /// <summary>The lookup server's reply couldn't be read.</summary>
    public const string GeocodingUnreadable = "geocoding.unreadable";

    /// <summary>The APRS-IS server couldn't be reached (DNS, refused, timed out, or no login answer).</summary>
    public const string AprsIsUnreachable = "aprs-is.unreachable";

    /// <summary>The APRS-IS server refused the login (<see cref="EngineErrorArgs.Reason"/>: what it said).</summary>
    public const string AprsIsLoginRefused = "aprs-is.login-refused";

    /// <summary>The APRS-IS server closed the connection.</summary>
    public const string AprsIsServerClosed = "aprs-is.server-closed";

    /// <summary>The network link to the APRS-IS server failed.</summary>
    public const string AprsIsLinkFailed = "aprs-is.link-failed";

    /// <summary>The APRS-IS server sent nothing, not even a keepalive, for too long.</summary>
    public const string AprsIsIdle = "aprs-is.idle";

    /// <summary>
    /// The APRS-IS server answered the login with KISS frames: it's a KISS TNC, such as Direwolf's port 8001, to add as a
    /// TNC source. Not retried, as a refused login isn't.
    /// </summary>
    public const string AprsIsKissTnc = "aprs-is.kiss-tnc";

    /// <summary>A KISS TNC couldn't be reached (refused, timed out, no such host).</summary>
    public const string KissUnreachable = "kiss.unreachable";

    /// <summary>A KISS TNC closed the connection.</summary>
    public const string KissClosed = "kiss.closed";

    /// <summary>The link to a KISS TNC failed, or it went silent and was given up as gone.</summary>
    public const string KissLinkFailed = "kiss.link-failed";

    /// <summary>
    /// A KISS TNC is connected but sends text lines, not KISS frames: it isn't in KISS mode. A warning, shown while the
    /// source stays connected.
    /// </summary>
    public const string KissTextNotKiss = "kiss.text-not-kiss";

    /// <summary>A KISS TNC is connected but has sent nothing for a while: it may be switched off. A warning.</summary>
    public const string KissQuiet = "kiss.quiet";

    /// <summary>
    /// The TNC didn't switch into KISS when its port opened (docs/features/F3.2-built-in-tncs.md, behavior 6); the
    /// <see cref="EngineErrorArgs.Reason"/> says why, with the type's hint.
    /// </summary>
    public const string KissSetupFailed = "kiss.setup-failed";

    /// <summary>A captured packet APRS.NET read but whose data type it doesn't support (F1.27 behavior 1.2).</summary>
    public const string CaptureUnsupported = "capture.unsupported";

    /// <summary>A captured packet whose data type APRS.NET doesn't know (F1.27 behavior 1.2).</summary>
    public const string CaptureUnknownType = "capture.unknown-type";

    /// <summary>The prefix of a captured packet's unrecognised part (F1.27 behavior 1.3), such as <c>aprs.unrecognized.unknown-path-entry</c>.</summary>
    public const string AprsUnrecognizedPrefix = "aprs.unrecognized.";

    /// <summary>The capture files couldn't be written or read (F1.27).</summary>
    public const string CaptureFailed = "capture.write-failed";

    /// <summary>The console's source isn't one that's open, or can't take commands (F1.28, F3.2 behavior 8).</summary>
    public const string ConsoleSourceUnknown = "console.source-unknown";

    /// <summary>A typed command outside manual mode (F3.2 behavior 9).</summary>
    public const string KissNotManual = "kiss.not-manual";

    /// <summary>Bytes on the command line that aren't hex (F3.2 behavior 10).</summary>
    public const string KissBytesInvalid = "kiss.bytes-invalid";

    /// <summary>A serial source's TNC type isn't known, or a custom one's scripts can't be read (F3.2 behavior 4).</summary>
    public const string TncScriptInvalid = "settings.source.tnc-script-invalid";

    /// <summary>The log file's folder can't be written (docs/features/F1.32-logging.md, behavior 8).</summary>
    public const string LoggingFolderNotWritable = "settings.logging.folder-not-writable";

    /// <summary>A log file limit is out of its range (F1.32 behaviors 9–11), named in <see cref="EngineErrorArgs.Field"/>.</summary>
    public const string LoggingLimitInvalid = "settings.logging.limit-invalid";

    /// <summary>An OTLP target's name, endpoint or header isn't usable (F1.32 behavior 13).</summary>
    public const string LoggingTargetInvalid = "settings.logging.target-invalid";

    /// <summary>A source that can't send was set TX enabled; no longer raised: such a kind is RX only (F2.1, 2026-10-18).</summary>
    public const string SourceReceiveOnly = "settings.source.receive-only";

    /// <summary>A KISS over serial source's port, speed or TNC port isn't usable.</summary>
    public const string KissSerialSourceInvalid = "settings.source.serial-invalid";

    /// <summary>A KISS source's host, port or TNC port isn't usable.</summary>
    public const string KissSourceInvalid = "settings.source.kiss-invalid";

    /// <summary>The Transmit switch is off (docs/features/F2.1-own-station.md, behavior 11).</summary>
    public const string TransmitOff = "transmit.off";

    /// <summary>The profile had Disable TX ticked; no longer raised since Disable TX went (F2.1, 2026-10-18).</summary>
    public const string TransmitProfileDisabled = "transmit.profile-disabled";

    /// <summary>The profile's licence isn't confirmed, or it listens with a listener ID (F2.1 behavior 4.1).</summary>
    public const string TransmitNotLicensed = "transmit.not-licensed";

    /// <summary>The source wasn't TX enabled; no longer raised since RX only replaced it (F2.1, 2026-10-18).</summary>
    public const string TransmitSourceDisabled = "transmit.source-disabled";

    /// <summary>The source is RX only (F2.1 behavior 5), named in <see cref="EngineErrorArgs.Source"/>.</summary>
    public const string TransmitSourceRxOnly = "transmit.source-rx-only";

    /// <summary>
    /// The source is switched off on its status pill (F2.1 behavior 5.1), named in <see cref="EngineErrorArgs.Source"/>.
    /// </summary>
    public const string TransmitSourceOff = "transmit.source-off";

    /// <summary>None of the profile's sources may send: it has none, or they're all RX only (F2.1 behavior 6.1).</summary>
    public const string TransmitNoSource = "transmit.no-source";

    /// <summary>
    /// Beacons wait for a position sent by hand since what's sent last changed (docs/features/F2.2-beacons.md, behavior 5).
    /// </summary>
    public const string TransmitPositionUnconfirmed = "transmit.position-unconfirmed";

    /// <summary>The engine was started receive-only (F2.1 behavior 5.1).</summary>
    public const string TransmitReceiveOnly = "transmit.receive-only";

    /// <summary>The callsign is a placeholder such as N0CALL (F2.1 behavior 5.2).</summary>
    public const string TransmitPlaceholderCallsign = "transmit.placeholder-callsign";

    /// <summary>The active profile has no APRS-IS source, or APRS-IS is switched off (F2.1 behavior 5.3).</summary>
    public const string TransmitNoAprsIs = "transmit.no-aprs-is";

    /// <summary>APRS-IS isn't connected (F2.1 behavior 5.4).</summary>
    public const string TransmitNotConnected = "transmit.not-connected";

    /// <summary>The APRS-IS server didn't verify the login: no passcode, or not the callsign's (F2.1 behavior 5.4).</summary>
    public const string TransmitNotVerified = "transmit.not-verified";

    /// <summary>The active profile has no location to send (F2.1 behavior 11).</summary>
    public const string TransmitNoPosition = "transmit.no-position";

    /// <summary>The connection failed while sending (F2.1 behavior 11).</summary>
    public const string TransmitFailed = "transmit.failed";

    /// <summary>A message's text has characters that can't be sent: <c>|</c>, <c>~</c>, <c>{</c> or non-ASCII (F2.4 behavior 6).</summary>
    public const string MessageTextInvalid = "messages.text-invalid";

    /// <summary>A message is empty or over 300 characters (F2.4 behavior 6).</summary>
    public const string MessageTooLong = "messages.too-long";

    /// <summary>A message's addressee isn't a callsign it can go to (F2.4 behavior 3).</summary>
    public const string MessageCallsignInvalid = "messages.callsign-invalid";

    /// <summary>A bulletin's id isn't a digit (bulletin) or a letter (announcement) (F2.5 behavior 5).</summary>
    public const string BulletinIdInvalid = "bulletins.id-invalid";

    /// <summary>A bulletin's text is empty, over 67 characters or has characters that can't be sent (F2.5 behavior 5).</summary>
    public const string BulletinTextInvalid = "bulletins.text-invalid";

    /// <summary>A bulletin or ANSRVR group name can't be sent (F2.5 behaviors 5, 8).</summary>
    public const string BulletinGroupInvalid = "bulletins.group-invalid";

    /// <summary>The release list couldn't be reached: no network, or GitHub down (F4.15 behavior 7).</summary>
    public const string UpdatesUnreachable = "updates.unreachable";

    /// <summary>GitHub's limit for anonymous requests was reached; the next check is at the usual time.</summary>
    public const string UpdatesRateLimited = "updates.rate-limited";

    /// <summary>The release list's answer wasn't what was expected.</summary>
    public const string UpdatesUnexpectedAnswer = "updates.unexpected-answer";

    /// <summary>The kind of failure a code stands for, which decides the exception type that carries it.</summary>
    public static EngineErrorKind KindOf(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return code.StartsWith("gateway.", StringComparison.Ordinal) ? EngineErrorKind.Access
            : code.StartsWith("auth.", StringComparison.Ordinal) ? EngineErrorKind.Access
            : code.StartsWith("geocoding.", StringComparison.Ordinal) ? EngineErrorKind.Operation
            : code.StartsWith("aprs-is.", StringComparison.Ordinal) ? EngineErrorKind.Operation
            : code.StartsWith("transmit.", StringComparison.Ordinal) ? EngineErrorKind.Operation
            : code.StartsWith("updates.", StringComparison.Ordinal) ? EngineErrorKind.Operation
            : code == KissNotManual ? EngineErrorKind.Operation
            : EngineErrorKind.Argument;
    }
}

/// <summary>Names of <see cref="EngineError.Args"/> entries.</summary>
public static class EngineErrorArgs
{
    /// <summary>The name of the profile the error is about.</summary>
    public const string Profile = "profile";

    /// <summary>The name of the source a <c>transmit.*</c> refusal is about (F2.1 behavior 7a).</summary>
    public const string Source = "source";

    /// <summary>The callsign a <c>transmit.not-verified</c> refusal is about (F2.1 behavior 7a).</summary>
    public const string Callsign = "callsign";

    /// <summary>The rejected value as typed (a callsign, server or filter).</summary>
    public const string Value = "value";

    /// <summary>The zero-based position of the offending character in <see cref="Value"/>.</summary>
    public const string Index = "index";

    /// <summary>The offending character.</summary>
    public const string Character = "character";

    /// <summary>Which field was rejected, as APRS.NET names it (for example <c>Callsign</c>).</summary>
    public const string Field = "field";

    /// <summary>An HTTP status code.</summary>
    public const string Status = "status";

    /// <summary>How many minutes to wait, as a whole number.</summary>
    public const string Minutes = "minutes";

    /// <summary>A server's own words, such as why it refused a login (not translated).</summary>
    public const string Reason = "reason";

    /// <summary>
    /// Which pair of a Maidenhead locator a character belongs to: <c>field</c>, <c>square</c>, <c>subsquare</c>,
    /// <c>extended-square</c> or <c>extended-subsquare</c>.
    /// </summary>
    public const string Part = "part";

    /// <summary>The zero-based position in a packet's raw text where a parse issue was found.</summary>
    public const string Offset = "offset";

    /// <summary>
    /// How serious a parse issue is: <see cref="SeverityWarning"/> (still decoded) or <see cref="SeverityError"/>
    /// (not decoded).
    /// </summary>
    public const string Severity = "severity";

    /// <summary>The <see cref="Severity"/> of an issue the packet was decoded despite.</summary>
    public const string SeverityWarning = "warning";

    /// <summary>The <see cref="Severity"/> of an issue that stopped the packet, or part of it, from being decoded.</summary>
    public const string SeverityError = "error";
}

/// <summary>Which exception type carries an <see cref="EngineError"/>.</summary>
public enum EngineErrorKind
{
    /// <summary>Invalid input: <see cref="EngineArgumentException"/>, an <see cref="ArgumentException"/>.</summary>
    Argument,

    /// <summary>A failed operation: <see cref="EngineOperationException"/>, an <see cref="InvalidOperationException"/>.</summary>
    Operation,

    /// <summary>Not allowed for this caller: <see cref="EngineAccessException"/>, an <see cref="UnauthorizedAccessException"/>.</summary>
    Access,
}

/// <summary>An exception that carries an <see cref="EngineError"/>.</summary>
public interface IHasEngineError
{
    /// <summary>What to tell the user.</summary>
    EngineError EngineError { get; }
}

/// <summary>Invalid input, with the <see cref="EngineError"/> to show.</summary>
public sealed class EngineArgumentException(EngineError error, Exception? innerException = null)
    : ArgumentException(error?.Message, innerException),
        IHasEngineError
{
    /// <inheritdoc />
    public EngineError EngineError { get; } = error ?? throw new ArgumentNullException(nameof(error));
}

/// <summary>A failed operation, with the <see cref="EngineError"/> to show.</summary>
public sealed class EngineOperationException(EngineError error, Exception? innerException = null)
    : InvalidOperationException(error?.Message, innerException),
        IHasEngineError
{
    /// <inheritdoc />
    public EngineError EngineError { get; } = error ?? throw new ArgumentNullException(nameof(error));
}

/// <summary>Not allowed for this caller, with the <see cref="EngineError"/> to show.</summary>
public sealed class EngineAccessException(EngineError error, Exception? innerException = null)
    : UnauthorizedAccessException(error?.Message, innerException),
        IHasEngineError
{
    /// <inheritdoc />
    public EngineError EngineError { get; } = error ?? throw new ArgumentNullException(nameof(error));
}

/// <summary>Creates the exception for an <see cref="EngineError"/>, the same way in-process and over the hub.</summary>
public static class EngineErrors
{
    /// <summary>The exception type <see cref="EngineErrorCodes.KindOf"/> picks for <paramref name="error"/>.</summary>
    public static Exception ToException(EngineError error, Exception? innerException = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        return EngineErrorCodes.KindOf(error.Code) switch
        {
            EngineErrorKind.Access => new EngineAccessException(error, innerException),
            EngineErrorKind.Operation => new EngineOperationException(error, innerException),
            _ => new EngineArgumentException(error, innerException),
        };
    }

    /// <summary>Shorthand for <see cref="ToException"/> with a new error.</summary>
    public static Exception Create(
        string code,
        string message,
        IReadOnlyDictionary<string, string>? args = null,
        Exception? innerException = null
    ) => ToException(new EngineError(code, message, args), innerException);
}
