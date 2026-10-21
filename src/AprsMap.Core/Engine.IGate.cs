// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.IGate;
using AprsNet.Packets;
using Microsoft.Extensions.Logging;

namespace AprsMap.Core;

/// <summary>
/// The receive-only IGate (docs/features/F3.4-rx-igate.md): what the profile's TNCs hear goes to APRS-IS under the
/// IGate rules (APRS.NET's <see cref="RfToInternetGate"/>), with a verified login and the Transmit switch on.
/// </summary>
public sealed partial class Engine
{
    private RfToInternetGate? _igate; // for the active callsign, under _gate
    private long _gated;
    private long _refused;

    /// <summary>
    /// A packet heard on <paramref name="sourceId"/>: one of the profile's own receivers' goes to APRS-IS when the
    /// profile is an IGate and APRS-IS can take it (behaviors 1–4).
    /// </summary>
    private void ConsiderGating(AprsPacket packet, string? sourceId)
    {
        if (!IsOwnReceiver(sourceId))
        {
            return; // only what this station's radios hear (behavior 2)
        }
        RfToInternetGate gate;
        IPacketSender? sender;
        lock (_gate)
        {
            var settings = _settings ?? AppSettings.Empty;
            if (
                settings.GetActiveProfile() is not { EffectiveIGate: true } profile
                || settings.AprsIsSourceOf(profile) is not { } aprsIs
                || IGateHeldLocked(settings, profile) is not null
                || !Callsign.TryParse(profile.Callsign, out var igate)
            )
            {
                return; // held: the status says why (behavior 3)
            }
            if (_igate?.IGate != igate)
            {
                _igate = new RfToInternetGate(igate, new RfToInternetGateOptions { TimeProvider = _time });
            }
            gate = _igate;
            sender = SenderFor(aprsIs);
        }
        var decision = gate.Gate(packet);
        if (!decision.IsGated || sender is null)
        {
            Interlocked.Increment(ref _refused);
            if (_transmitLog.IsEnabled(LogLevel.Debug))
            {
                var reason = decision.Refusal?.ToString() ?? "no connection";
                var from = decision.Heard.Source?.Value ?? "?";
                LogNotGated(_transmitLog, reason, from);
            }
            return;
        }
        _ = GateAsync(sender, decision.Packet!);
    }

    private async Task GateAsync(IPacketSender sender, AprsPacket packet)
    {
        try
        {
            await sender.SendAsync(packet, CancellationToken.None).ConfigureAwait(false);
            Interlocked.Increment(ref _gated);
            if (_transmitLog.IsEnabled(LogLevel.Debug))
            {
                var line = packet.ToString();
                LogGated(_transmitLog, line);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            Interlocked.Increment(ref _refused);
            LogGateFailed(_transmitLog, ex);
        }
    }

    /// <summary>
    /// Why the IGate can't gate now (behavior 3): no APRS-IS source, or what stops a send on it (Transmit off, a
    /// profile that may not send, a login that isn't verified). Call under <see cref="_gate"/>.
    /// </summary>
    private EngineError? IGateHeldLocked(AppSettings settings, StationProfile profile) =>
        settings.AprsIsSourceOf(profile) is { } aprsIs
            ? ((IMessengerHost)this).WhyNotOn(aprsIs.Id)
            : new EngineError(EngineErrorCodes.TransmitNoAprsIs, "The profile has no APRS-IS source to gate to.");

    /// <summary>The IGate's counts and hold for the status (behavior 6); null when the profile isn't an IGate.</summary>
    private IGateStatus? IGateStatusLocked()
    {
        var settings = _settings ?? AppSettings.Empty;
        return settings.GetActiveProfile() is { EffectiveIGate: true } profile
            ? new IGateStatus(
                Interlocked.Read(ref _gated),
                Interlocked.Read(ref _refused),
                IGateHeldLocked(settings, profile)
            )
            : null;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Gated to APRS-IS: {Line}")]
    private static partial void LogGated(ILogger logger, string line);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Not gated ({Reason}) from {Source}")]
    private static partial void LogNotGated(ILogger logger, string reason, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gating to APRS-IS failed")]
    private static partial void LogGateFailed(ILogger logger, Exception error);
}
