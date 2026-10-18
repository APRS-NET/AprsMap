// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AprsMap.Contracts;

namespace AprsMap.Core;

/// <summary>
/// One source's recent traffic for the Source console (docs/features/F1.28-source-console.md, behaviors 4, 7): the
/// last <see cref="Capacity"/> entries, and every watcher's own queue of new ones. Entries arrive on APRS.NET's threads.
/// </summary>
internal sealed class TrafficLog
{
    /// <summary>How many entries are kept (decision 1).</summary>
    public const int Capacity = 500;

    private readonly Lock _lock = new();
    private readonly Queue<TrafficEntry> _entries = new();
    private readonly List<Channel<TrafficEntry>> _watchers = [];

    /// <summary>How many are watching now.</summary>
    public int Watchers
    {
        get
        {
            lock (_lock)
            {
                return _watchers.Count;
            }
        }
    }

    /// <summary>Keeps <paramref name="entry"/> and hands it to every watcher; a slow watcher loses its oldest.</summary>
    public void Add(TrafficEntry entry)
    {
        lock (_lock)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }
            foreach (var watcher in _watchers)
            {
                watcher.Writer.TryWrite(entry);
            }
        }
    }

    /// <summary>The kept entries, then new ones as they come, until cancelled.</summary>
    /// <param name="started">Called once the watcher is counted, before the first entry.</param>
    /// <param name="ended">Called once it's no longer counted.</param>
    public async IAsyncEnumerable<TrafficEntry> WatchAsync(
        Action started,
        Action ended,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var channel = Channel.CreateBounded<TrafficEntry>(
            new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }
        );
        TrafficEntry[] kept;
        lock (_lock)
        {
            kept = [.. _entries];
            _watchers.Add(channel);
        }
        started();
        try
        {
            foreach (var entry in kept)
            {
                yield return entry;
            }
            await foreach (var entry in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return entry;
            }
        }
        finally
        {
            lock (_lock)
            {
                _watchers.Remove(channel);
            }
            ended();
        }
    }
}
