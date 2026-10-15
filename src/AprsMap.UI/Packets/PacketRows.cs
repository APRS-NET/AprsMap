// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AprsMap.UI.Packets;

/// <summary>
/// The packet list's rows, newest first. A large batch (the history the engine sends first) goes in with one reset
/// rather than an insert per packet, which a 5,000-row list would feel.
/// </summary>
public sealed class PacketRows : ObservableCollection<PacketRow>
{
    /// <summary>Above this many rows a batch replaces the list with one reset; below it, rows are inserted one by one.</summary>
    internal const int ResetThreshold = 64;

    /// <summary>Puts <paramref name="newest"/> (oldest first) at the top, and drops rows past <paramref name="max"/>.</summary>
    internal void AddNewest(IReadOnlyList<PacketRow> newest, int max)
    {
        if (newest.Count < ResetThreshold)
        {
            foreach (var row in newest)
            {
                Insert(0, row);
            }
            while (Count > max)
            {
                RemoveAt(Count - 1);
            }
            return;
        }
        CheckReentrancy();
        var rows = new List<PacketRow>(Math.Min(max, Count + newest.Count));
        for (var i = newest.Count - 1; i >= 0 && rows.Count < max; i--)
        {
            rows.Add(newest[i]);
        }
        for (var i = 0; i < Count && rows.Count < max; i++)
        {
            rows.Add(Items[i]);
        }
        Items.Clear();
        foreach (var row in rows)
        {
            Items.Add(row);
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
