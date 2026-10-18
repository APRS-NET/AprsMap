// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Runtime.CompilerServices;
using AprsMap.Contracts;
using AprsNet.Kiss;

namespace AprsMap.Core;

/// <summary>
/// The Source console (docs/features/F1.28-source-console.md) and typing to a TNC
/// (docs/features/F3.2-built-in-tncs.md, behaviors 8–11).
/// </summary>
public sealed partial class Engine
{
    /// <summary>How long manual mode lasts with nothing sent (F3.2 decision 4).</summary>
    internal static readonly TimeSpan ManualIdle = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, TrafficLog> _traffic = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ITrafficSource> _trafficSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ITncConsole> _consoles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ManualLease> _manual = new(StringComparer.Ordinal);

    /// <summary>A source's traffic log. Call under <see cref="_gate"/>.</summary>
    private TrafficLog LogOf(string sourceId)
    {
        if (!_traffic.TryGetValue(sourceId, out var log))
        {
            _traffic[sourceId] = log = new TrafficLog();
        }
        return log;
    }

    /// <summary>
    /// Starts recording <paramref name="connection"/>'s traffic as <paramref name="sourceId"/>'s: always for a TNC, and
    /// for APRS-IS only while someone watches (F1.28 behavior 7).
    /// </summary>
    private void AttachTraffic(string sourceId, IPacketSource connection, bool always)
    {
        if (connection is not ITrafficSource traffic)
        {
            return;
        }
        TrafficLog log;
        lock (_gate)
        {
            log = LogOf(sourceId);
            _trafficSources[sourceId] = traffic;
            if (connection is ITncConsole console)
            {
                _consoles[sourceId] = console;
            }
        }
        if (always || log.Watchers > 0)
        {
            traffic.WatchTraffic(e => log.Add(e with { SourceId = sourceId }));
        }
    }

    /// <summary>Stops recording when the connection closes, and ends its manual mode.</summary>
    private void DetachTraffic(string sourceId, IPacketSource connection)
    {
        ManualLease? lease = null;
        lock (_gate)
        {
            if (_trafficSources.GetValueOrDefault(sourceId) == connection as ITrafficSource)
            {
                _trafficSources.Remove(sourceId);
            }
            if (_consoles.GetValueOrDefault(sourceId) == connection as ITncConsole)
            {
                _consoles.Remove(sourceId);
                _manual.Remove(sourceId, out lease);
            }
        }
        lease?.Dispose();
        (connection as ITrafficSource)?.WatchTraffic(null);
    }

    /// <summary>Whether <paramref name="sourceId"/> is in manual mode. Call under <see cref="_gate"/>.</summary>
    private bool IsManual(string sourceId) => _manual.ContainsKey(sourceId);

    /// <inheritdoc />
    public async IAsyncEnumerable<TrafficEntry> WatchSourceTrafficAsync(
        string sourceId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        TrafficLog log;
        lock (_gate)
        {
            if (_settings?.SourceList.FirstOrDefault(s => s.Id == sourceId) is null)
            {
                throw EngineErrors.ToException(
                    new EngineError(EngineErrorCodes.ConsoleSourceUnknown, $"There's no source '{sourceId}'.")
                );
            }
            log = LogOf(sourceId);
        }
        // APRS-IS is recorded only while watched: the first watcher starts it, the last one stops it (behavior 7).
        void Started()
        {
            ITrafficSource? source;
            lock (_gate)
            {
                source = _trafficSources.GetValueOrDefault(sourceId);
            }
            source?.WatchTraffic(e => log.Add(e with { SourceId = sourceId }));
        }
        void Ended()
        {
            ITrafficSource? source;
            bool isAprsIs;
            lock (_gate)
            {
                source = _trafficSources.GetValueOrDefault(sourceId);
                isAprsIs = _settings?.SourceList.FirstOrDefault(s => s.Id == sourceId)?.Kind == SourceKind.AprsIs;
            }
            if (isAprsIs && log.Watchers == 0)
            {
                source?.WatchTraffic(null);
            }
        }
        await foreach (var entry in log.WatchAsync(Started, Ended, cancellationToken).ConfigureAwait(false))
        {
            yield return entry;
        }
    }

    /// <inheritdoc />
    public Task SetTncManualAsync(string sourceId, bool manual, CancellationToken cancellationToken = default)
    {
        ManualLease? ended = null;
        lock (_gate)
        {
            var console = ConsoleOf(sourceId);
            if (manual && !_manual.ContainsKey(sourceId))
            {
                _manual[sourceId] = new ManualLease(console.BeginManual(), _time, () => EndManual(sourceId));
            }
            else if (!manual)
            {
                _manual.Remove(sourceId, out ended);
            }
            else
            {
                _manual[sourceId].Touch();
            }
        }
        ended?.Dispose();
        return Task.CompletedTask;
    }

    private void EndManual(string sourceId)
    {
        ManualLease? ended;
        lock (_gate)
        {
            _manual.Remove(sourceId, out ended);
        }
        ended?.Dispose();
    }

    /// <inheritdoc />
    public async Task SendTncCommandAsync(
        string sourceId,
        TncCommand command,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        ITncConsole console;
        bool manual;
        EngineError? block;
        lock (_gate)
        {
            console = ConsoleOf(sourceId);
            manual = _manual.TryGetValue(sourceId, out var lease);
            lease?.Touch();
            block = _settings is { } settings ? ProfileBlock(settings) : null;
        }
        switch (command.Kind)
        {
            case TncCommandKind.EnterKiss or TncCommandKind.LeaveKiss:
                // Switching modes sends nothing on the air: allowed for anyone who may use the console (F3.2 behavior 7).
                var script =
                    command.Kind == TncCommandKind.EnterKiss
                        ? console.Setup?.EnterKiss
                            ?? throw EngineErrors.ToException(
                                new EngineError(
                                    EngineErrorCodes.ConsoleSourceUnknown,
                                    "This TNC has no type, so there's no script into KISS."
                                )
                            )
                        : console.Setup?.LeaveKiss ?? new TncScript([TncScriptStep.Send(TncSetup.KissExitFrame.Span)]);
                var temporary = manual ? null : console.BeginManual();
                try
                {
                    await console.RunScriptAsync(script, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    temporary?.Dispose();
                }
                break;
            default:
                if (!manual)
                {
                    throw EngineErrors.ToException(
                        new EngineError(
                            EngineErrorCodes.KissNotManual,
                            "Open the command line before typing to the TNC."
                        )
                    );
                }
                // A typed command can key the radio (F3.2 behavior 11).
                if (block is not null)
                {
                    throw EngineErrors.ToException(block);
                }
                if (command.Kind == TncCommandKind.Bytes)
                {
                    await console
                        .SendRawAsync(HexBytes(command.Text ?? string.Empty), cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await console.SendLineAsync(command.Text ?? string.Empty, cancellationToken).ConfigureAwait(false);
                }
                break;
        }
    }

    /// <summary>The console of an open serial KISS source. Call under <see cref="_gate"/>.</summary>
    private ITncConsole ConsoleOf(string sourceId) =>
        _settings?.SourceList.FirstOrDefault(s => s.Id == sourceId) is { Kind: SourceKind.KissSerial }
        && _consoles.TryGetValue(sourceId, out var console)
            ? console
            : throw EngineErrors.ToException(
                new EngineError(
                    EngineErrorCodes.ConsoleSourceUnknown,
                    "Typing to a TNC needs a KISS over serial source that's open."
                )
            );

    /// <summary>"C0 FF C0" or "C0FFC0" as bytes.</summary>
    internal static byte[] HexBytes(string text)
    {
        var digits = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        if (digits.Length == 0 || digits.Length % 2 != 0)
        {
            throw EngineErrors.ToException(
                new EngineError(EngineErrorCodes.KissBytesInvalid, "Bytes are pairs of hex digits, such as C0 FF C0.")
            );
        }
        var bytes = new byte[digits.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (
                !byte.TryParse(
                    digits.AsSpan(i * 2, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out bytes[i]
                )
            )
            {
                throw EngineErrors.ToException(
                    new EngineError(
                        EngineErrorCodes.KissBytesInvalid,
                        "Bytes are pairs of hex digits, such as C0 FF C0."
                    )
                );
            }
        }
        return bytes;
    }

    /// <summary>Manual mode on one source: APRS.NET's lease, ended after <see cref="ManualIdle"/> with nothing sent.</summary>
    private sealed class ManualLease : IDisposable
    {
        private readonly IDisposable _lease;
        private readonly ITimer _idle;

        public ManualLease(IDisposable lease, TimeProvider time, Action expired)
        {
            _lease = lease;
            _idle = time.CreateTimer(_ => expired(), null, ManualIdle, Timeout.InfiniteTimeSpan);
        }

        public void Touch() => _idle.Change(ManualIdle, Timeout.InfiniteTimeSpan);

        public void Dispose()
        {
            _idle.Dispose();
            _lease.Dispose();
        }
    }
}
