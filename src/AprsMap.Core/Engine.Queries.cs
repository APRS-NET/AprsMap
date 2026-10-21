// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Messaging;
using AprsNet.Packets;
using Microsoft.Extensions.Logging;

namespace AprsMap.Core;

/// <summary>
/// Answers APRS queries (docs/features/F2.8-queries.md) with APRS.NET's <see cref="QueryResponder"/>: the station's
/// position, status, objects and the stations heard direct, sent back on the source each query came from, under every
/// transmit rule.
/// </summary>
public sealed partial class Engine
{
    /// <summary>How far back the stations heard direct go in an <c>?APRSD</c> answer (behavior 3).</summary>
    public static readonly TimeSpan HeardDirectFor = TimeSpan.FromHours(1);

    private QueryResponder? _responder; // for the active callsign, under _gate

    /// <summary>
    /// A packet heard on <paramref name="sourceId"/>: a query to this station (or every station) is answered after
    /// the responder's wait, when the profile answers queries, its position has been confirmed and the source can send
    /// (behaviors 1, 2, 4).
    /// </summary>
    private void ConsiderQuery(AprsPacket packet, string? sourceId)
    {
        // Cheap first: only a query (a ? packet, or a message starting with ?) is worth the station's state.
        if (sourceId is null || packet.Payload is not (QueryReport or MessagePayload { Text: ['?', ..] }))
        {
            return;
        }
        StationProfile? profile;
        QueryResponder? responder;
        lock (_gate)
        {
            profile = _settings?.GetActiveProfile();
            if (
                profile is not { EffectiveAnswerQueries: true, PositionConfirmed: true }
                || !Callsign.TryParse(profile.Callsign, out var station)
            )
            {
                return; // nothing automatic before a position was sent by hand (F2.1 behavior 12)
            }
            if (_responder?.Station != station)
            {
                _responder = new QueryResponder(station, new QueryResponderOptions { TimeProvider = _time });
            }
            responder = _responder;
        }
        if (responder.Answer(packet, StateFor(profile)) is not { } answer)
        {
            return;
        }
        _ = AnswerAsync(answer, sourceId);
    }

    /// <summary>What the station answers with (behavior 3); what it doesn't have goes unanswered.</summary>
    private QueryState StateFor(StationProfile profile)
    {
        var now = _time.GetUtcNow();
        var here = WithGps(profile);
        return new QueryState
        {
            Position = here is { Latitude: not null, Longitude: not null }
                ? Beaconer.PositionReport(here, FreshFix(profile))
                : null,
            Status = profile.EffectiveStatusBeacon.Text.Trim().Length > 0 ? Beaconer.StatusReport(here, now) : null,
            Objects = [.. profile.ObjectList.Where(o => o.Live).Select(o => Beaconer.ObjectReport(o, now))],
            HeardDirect =
            [
                .. _stations
                    .HeardDirectSince(now - HeardDirectFor)
                    .Select(name => Callsign.TryParse(name, out var call) ? call : null)
                    .OfType<Callsign>(),
            ],
        };
    }

    /// <summary>Sends <paramref name="answer"/> back on <paramref name="sourceId"/> after its wait, if it still can.</summary>
    private async Task AnswerAsync(QueryAnswer answer, string sourceId)
    {
        try
        {
            if (answer.Delay > TimeSpan.Zero)
            {
                await Task.Delay(answer.Delay, _time).ConfigureAwait(false);
            }
            if (!((IMessengerHost)this).CanSendOn(sourceId))
            {
                LogQueryHeld(_transmitLog, answer.Kind, answer.Querier.Value, sourceId);
                return; // Transmit off, a source that can't send, a profile that only listens (behavior 4)
            }
            foreach (var payload in answer.Payloads)
            {
                await SendPayloadAsync(sourceId, payload, CancellationToken.None).ConfigureAwait(false);
            }
            LogQueryAnswered(_transmitLog, answer.Kind, answer.Querier.Value, sourceId, answer.Payloads.Count);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            LogQueryFailed(_transmitLog, ex, answer.Kind, answer.Querier.Value);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Answered {Kind} from {Querier} on {Source} ({Packets} packets)"
    )]
    private static partial void LogQueryAnswered(
        ILogger logger,
        QueryKind kind,
        string querier,
        string source,
        int packets
    );

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Not answering {Kind} from {Querier} on {Source}: it can't send"
    )]
    private static partial void LogQueryHeld(ILogger logger, QueryKind kind, string querier, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Answering {Kind} from {Querier} failed")]
    private static partial void LogQueryFailed(ILogger logger, Exception error, QueryKind kind, string querier);
}
