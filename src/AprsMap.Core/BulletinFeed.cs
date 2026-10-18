// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AprsMap.Contracts;
using AprsNet.Messaging;
using AprsNet.Packets;

namespace AprsMap.Core;

/// <summary>
/// The bulletins heard (docs/features/F2.5-bulletins.md, behaviors 1–3), on APRS.NET's <see cref="BulletinBoard"/>,
/// which keeps the newest version of each and removes one not heard for its expiry. Every watcher gets the board's
/// items, then each change as the board raises it.
/// </summary>
internal sealed class BulletinFeed : IDisposable
{
    private readonly BulletinBoard _board;
    private readonly Lock _lock = new();
    private readonly List<Channel<BulletinView>> _watchers = [];

    public BulletinFeed(TimeProvider time)
    {
        _board = new BulletinBoard(new BulletinBoardOptions { TimeProvider = time });
        _board.Changed += OnChanged;
    }

    /// <summary>Hands a received packet to the board; anything that isn't a bulletin is ignored there.</summary>
    public void Receive(AprsPacket packet, string? sourceId) => _board.Receive(packet, sourceId);

    private void OnChanged(object? sender, BulletinChangedEventArgs e)
    {
        var view = ViewOf(e.Bulletin) with
        {
            Updated = e.Change == BulletinChange.Replaced,
            Removed = e.Change == BulletinChange.Removed,
        };
        lock (_lock)
        {
            foreach (var watcher in _watchers)
            {
                watcher.Writer.TryWrite(view);
            }
        }
    }

    /// <summary>The board's items now, then each change, until cancelled.</summary>
    public async IAsyncEnumerable<BulletinView> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<BulletinView>(
            new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }
        );
        IReadOnlyList<Bulletin> now;
        lock (_lock)
        {
            _watchers.Add(channel);
            now = _board.Bulletins;
        }
        try
        {
            foreach (var bulletin in now)
            {
                yield return ViewOf(bulletin);
            }
            await foreach (var view in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return view;
            }
        }
        finally
        {
            lock (_lock)
            {
                _watchers.Remove(channel);
            }
        }
    }

    private static BulletinView ViewOf(Bulletin b) =>
        new(
            $"{b.From}|{b.Kind}|{b.Addressee}|{b.Id}",
            b.Kind switch
            {
                MessageKind.Announcement => BulletinKind.Announcement,
                MessageKind.NwsBulletin => BulletinKind.Nws,
                _ => BulletinKind.Bulletin,
            },
            b.From.Value,
            b.Addressee,
            b.Id,
            b.Group,
            b.Text,
            b.FirstHeard,
            b.LastHeard,
            b.Source
        );

    public void Dispose()
    {
        _board.Changed -= OnChanged;
        _board.Dispose();
    }
}
