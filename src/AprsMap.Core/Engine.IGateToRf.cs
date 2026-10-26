// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.IGate;
using AprsNet.Packets;
using Microsoft.Extensions.Logging;

namespace AprsMap.Core;

/// <summary>
/// The two-way IGate (docs/features/F3.4-two-way-igate.md): who is heard on the profile's TNCs, and messages from APRS-IS
/// for those stations sent on RF by APRS.NET's <see cref="InternetToRfGate"/>, one gate a TNC, held by every transmit rule.
/// </summary>
public sealed partial class Engine
{
    // Who the TNCs hear, and the port number each TNC records as. Thread-safe; the port map under _gate.
    private readonly HeardStations _heard;
    private readonly Dictionary<string, int> _tncPorts = new(StringComparer.Ordinal);

    // Each TNC's gate, with the address it was made for. Under _gate.
    private readonly Dictionary<string, (string Key, InternetToRfGate Gate)> _rfGates = new(StringComparer.Ordinal);
    private long _gatedToRf;
    private long _refusedToRf;

    /// <summary>Records a packet heard on <paramref name="sourceId"/>, one of the profile's TNCs (behavior 2).</summary>
    private void RecordHeard(AprsPacket packet, string sourceId)
    {
        if (packet.Source is not { } from || IsOwnCall(from.Value))
        {
            return; // the station's own packets, heard back, don't make it local
        }
        int port;
        lock (_gate)
        {
            if (!_tncPorts.TryGetValue(sourceId, out port))
            {
                _tncPorts[sourceId] = port = _tncPorts.Count;
            }
        }
        try
        {
            _heard.Record(packet, port);
        }
        catch (ArgumentException) { } // a source AX.25 can't carry isn't heard on RF
    }

    private bool IsOwnCall(string callsign) =>
        (_settings ?? AppSettings.Empty).GetActiveProfile()?.Callsign is { } own
        && own.Equals(callsign, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A packet from APRS-IS: sent on RF when the profile's IGate passes messages and the rules allow it (behaviors 1,
    /// 3–6); a message goes out on the TNC that heard its addressee, a position owed after one on any TNC that owes it.
    /// </summary>
    private void ConsiderGatingToRf(AprsPacket packet, string? sourceId)
    {
        List<(InternetToRfGate Gate, IPacketSender Sender)> gates = [];
        lock (_gate)
        {
            var settings = _settings ?? AppSettings.Empty;
            if (
                sourceId is null
                || settings.GetActiveProfile() is not { EffectiveIGateToRf: true } profile
                || settings.AprsIsSourceOf(profile) is not { } aprsIs
                || aprsIs.Id != sourceId
                || IGateHeldLocked(settings, profile) is not null
            )
            {
                return; // not from APRS-IS, or held: the status says why
            }
            int? addresseePort =
                packet.Payload is MessagePayload { Addressee: var to }
                && Callsign.TryParse(to.Trim(), out var addressee)
                && _heard.Find(addressee) is { } heard
                    ? heard.Port
                    : null;
            foreach (var (tncId, port) in _tncPorts)
            {
                if (
                    (addresseePort is { } wanted && wanted != port)
                    || settings.SourceList.FirstOrDefault(s => s.Id == tncId) is not { } tnc
                    || !profile.Uses(tnc)
                    || ((IMessengerHost)this).WhyNotOn(tncId) is not null
                    || _senders.GetValueOrDefault(tncId) is not { } sender
                )
                {
                    continue;
                }
                var key = $"{profile.Callsign}|{tnc.EffectiveTxPath}";
                if (!_rfGates.TryGetValue(tncId, out var made) || made.Key != key)
                {
                    try
                    {
                        var address = PacketAddress.Parse(
                            profile.Callsign,
                            Brand.Tocall,
                            tnc.EffectiveTxPath.Split(',', StringSplitOptions.TrimEntries)
                        );
                        made = (
                            key,
                            new InternetToRfGate(address, _heard, new InternetToRfGateOptions { TimeProvider = _time })
                        );
                        _rfGates[tncId] = made;
                    }
                    catch (ArgumentException)
                    {
                        continue; // a callsign or path AX.25 can't carry: held by validation elsewhere
                    }
                }
                gates.Add((made.Gate, sender));
            }
        }
        var isMessage = packet.Payload is MessagePayload;
        foreach (var (gate, sender) in gates)
        {
            var decision = gate.Gate(packet);
            if (decision.IsGated)
            {
                _ = GateToRfAsync(sender, decision.Packet!);
                return; // once
            }
            if (isMessage && _transmitLog.IsEnabled(LogLevel.Debug))
            {
                var reason = decision.Refusal?.ToString() ?? "?";
                var from = packet.Source?.Value ?? "?";
                LogNotGatedToRf(_transmitLog, reason, from);
            }
        }
        if (isMessage)
        {
            Interlocked.Increment(ref _refusedToRf); // not for a local station, rate limited, or a copy
        }
    }

    private async Task GateToRfAsync(IPacketSender sender, AprsPacket packet)
    {
        try
        {
            await sender.SendAsync(packet, CancellationToken.None).ConfigureAwait(false);
            Interlocked.Increment(ref _gatedToRf);
            if (_transmitLog.IsEnabled(LogLevel.Debug))
            {
                var line = packet.ToString();
                LogGatedToRf(_transmitLog, line);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            Interlocked.Increment(ref _refusedToRf);
            LogGateFailed(_transmitLog, ex);
        }
    }

    /// <summary>The stations the TNCs have heard, most recent first (behavior 7), for the dashboard.</summary>
    private (IReadOnlyList<DashboardHeard> Recent, int Total) HeardOnRf()
    {
        var stations = _heard.Stations;
        return (
            [
                .. stations
                    .OrderByDescending(s => s.LastHeard)
                    .Take(10)
                    .Select(s => new DashboardHeard(s.Station.Value, s.Hops, s.LastHeard)),
            ],
            stations.Count
        );
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Gated to RF: {Line}")]
    private static partial void LogGatedToRf(ILogger logger, string line);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Not gated to RF ({Reason}) from {Source}")]
    private static partial void LogNotGatedToRf(ILogger logger, string reason, string source);
}
