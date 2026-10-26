// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using AprsMap.Contracts;
using AprsNet.Ax25;
using AprsNet.Packets;
using Microsoft.Extensions.Logging;

namespace AprsMap.Core;

/// <summary>
/// The digipeater (docs/features/F3.5-digipeater.md): each KISS TNC set to digipeat repeats what it hears, by APRS.NET's
/// rules, on the port it heard on, only while the station may transmit there.
/// </summary>
public sealed partial class Engine
{
    // Each digipeating source's digipeater, with the settings it was made for. Under _gate.
    private readonly Dictionary<string, (string Key, Digipeater Digipeater)> _digipeaters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _digipeated = new(StringComparer.Ordinal);

    private int DigipeatedOn(string sourceId) => _digipeated.GetValueOrDefault(sourceId);

    /// <summary>
    /// Repeats <paramref name="packet"/>, heard on <paramref name="sourceId"/>, when that TNC digipeats and the station
    /// may transmit on it (behaviors 2–4); true when it's sent.
    /// </summary>
    private bool ConsiderDigipeat(AprsPacket packet, string? sourceId)
    {
        if (sourceId is null || !IsOwnReceiver(sourceId) || !((IMessengerHost)this).CanSendOn(sourceId))
        {
            return false; // only what this station's TNCs hear, and only while it may send there (behavior 3)
        }
        Digipeater digipeater;
        IFrameSender? sender;
        lock (_gate)
        {
            var settings = _settings ?? AppSettings.Empty;
            if (
                settings.SourceList.FirstOrDefault(s => s.Id == sourceId)
                    is not { EffectiveDigipeat: not DigipeatMode.Off } source
                || settings.GetActiveProfile() is not { } profile
                || !Ax25Address.TryParse(profile.Callsign, out var call)
            )
            {
                return false;
            }
            var key = $"{call}|{source.EffectiveDigipeat}|{string.Join(',', source.DigipeatAliasList)}";
            if (!_digipeaters.TryGetValue(sourceId, out var made) || made.Key != key)
            {
                made = (
                    key,
                    new Digipeater(
                        call,
                        new DigipeaterOptions
                        {
                            FillInOnly = source.EffectiveDigipeat == DigipeatMode.FillIn,
                            Aliases = source.DigipeatAliasList,
                            TimeProvider = _time,
                        }
                    )
                );
                _digipeaters[sourceId] = made;
            }
            digipeater = made.Digipeater;
            sender = _senders.GetValueOrDefault(sourceId) as IFrameSender;
        }
        if (sender is null)
        {
            return false;
        }
        Ax25Frame heard;
        try
        {
            heard = Ax25Frame.FromAprsPacket(packet);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            return false; // not a frame AX.25 can carry
        }
        var decision = digipeater.Digipeat(heard);
        if (!decision.IsRepeated)
        {
            if (_transmitLog.IsEnabled(LogLevel.Debug))
            {
                var reason = decision.Refusal?.ToString() ?? "?";
                var from = packet.Source?.Value ?? "?";
                LogNotDigipeated(_transmitLog, reason, from);
            }
            return false;
        }
        _ = DigipeatAsync(sender, sourceId, decision.Frame!);
        return true;
    }

    private async Task DigipeatAsync(IFrameSender sender, string sourceId, Ax25Frame frame)
    {
        try
        {
            await sender.SendFrameAsync(frame, CancellationToken.None).ConfigureAwait(false);
            _digipeated.AddOrUpdate(sourceId, 1, (_, n) => n + 1);
            EngineTelemetry.Digipeated.Add(1);
            if (_transmitLog.IsEnabled(LogLevel.Debug))
            {
                var line = frame.ToAprsPacket().ToString();
                LogDigipeated(_transmitLog, line);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            LogDigipeatFailed(_transmitLog, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Digipeated {Packet}")]
    private static partial void LogDigipeated(ILogger logger, string packet);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Not digipeated ({Reason}): from {Source}")]
    private static partial void LogNotDigipeated(ILogger logger, string reason, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A frame to digipeat couldn't be sent")]
    private static partial void LogDigipeatFailed(ILogger logger, Exception error);
}

/// <summary>A source that sends AX.25 frames with their paths as they are: a KISS TNC, for the digipeater (F3.5).</summary>
internal interface IFrameSender
{
    /// <summary>Sends <paramref name="frame"/> as it is.</summary>
    ValueTask SendFrameAsync(Ax25Frame frame, CancellationToken cancellationToken);
}
