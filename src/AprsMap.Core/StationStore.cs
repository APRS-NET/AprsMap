// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.Packets;

namespace AprsMap.Core;

/// <summary>
/// The latest position of every station, object and item heard, and the changes each map watcher has not collected
/// yet. A watcher's pending changes hold one entry per name, so a slow watcher costs at most one copy of the map, never
/// a backlog of packets.
/// </summary>
// Aging, expiry, ownership and duplicate handling arrive with F1.3 (station store).
internal sealed class StationStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, StationState> _stations = new(StringComparer.Ordinal);
    private readonly List<StationWatcher> _watchers = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _stations.Count;
            }
        }
    }

    /// <summary>
    /// Records the position in <paramref name="packet"/> (which has a header), if it has one; a killed object or item
    /// is removed.
    /// </summary>
    public void Apply(AprsPacket packet, DateTimeOffset receivedAt)
    {
        if (packet.Payload is not PositionedPayload { IsPositionUnknown: false } positioned)
        {
            return;
        }

        var (name, live) = positioned switch
        {
            ObjectReport report => (report.Name, report.IsLive),
            ItemReport report => (report.Name, report.IsLive),
            _ => (packet.Source!.Value, true),
        };

        lock (_gate)
        {
            if (!live)
            {
                if (_stations.Remove(name))
                {
                    foreach (var watcher in _watchers)
                    {
                        watcher.Remove(name);
                    }
                }
                return;
            }

            var state = new StationState(
                name,
                receivedAt,
                positioned.Position.Latitude,
                positioned.Position.Longitude,
                positioned.Symbol.ToString()
            );
            _stations[name] = state;
            foreach (var watcher in _watchers)
            {
                watcher.Change(state);
            }
        }
    }

    /// <summary>Forgets every station, telling each watcher they're gone (F0.10).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (var name in _stations.Keys)
            {
                foreach (var watcher in _watchers)
                {
                    watcher.Remove(name);
                }
            }
            _stations.Clear();
        }
    }

    /// <summary>Starts collecting changes for a new watcher and returns everything known so far.</summary>
    public StationWatcher Subscribe(out StationState[] snapshot)
    {
        var watcher = new StationWatcher();
        lock (_gate)
        {
            snapshot = [.. _stations.Values];
            _watchers.Add(watcher);
        }
        return watcher;
    }

    public void Unsubscribe(StationWatcher watcher)
    {
        lock (_gate)
        {
            _watchers.Remove(watcher);
        }
    }

    /// <summary>The watcher's changes since its last call, or <see langword="null"/> when nothing changed.</summary>
    public StationBatch? Drain(StationWatcher watcher)
    {
        lock (_gate)
        {
            return watcher.Drain();
        }
    }

    /// <summary>One watcher's uncollected changes. Only touched under the store's lock.</summary>
    internal sealed class StationWatcher
    {
        private readonly Dictionary<string, StationState> _changed = new(StringComparer.Ordinal);
        private readonly HashSet<string> _removed = new(StringComparer.Ordinal);

        public void Change(StationState state)
        {
            _removed.Remove(state.Name);
            _changed[state.Name] = state;
        }

        public void Remove(string name)
        {
            _changed.Remove(name);
            _removed.Add(name);
        }

        public StationBatch? Drain()
        {
            if (_changed.Count == 0 && _removed.Count == 0)
            {
                return null;
            }
            var batch = new StationBatch([.. _changed.Values], [.. _removed]);
            _changed.Clear();
            _removed.Clear();
            return batch;
        }
    }
}
