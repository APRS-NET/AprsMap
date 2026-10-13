// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using AprsMap.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AprsMap.UI.ViewModels;

/// <summary>The main screen: connection status and the live packet list. Talks only to <see cref="IEngineService"/>.</summary>
public sealed partial class MainViewModel(IEngineService engine, TimeProvider time) : ObservableObject
{
    /// <summary>How many packets the list keeps.</summary>
    public const int MaxPackets = 200;

    /// <summary>The product name, from branding.</summary>
    public string Title { get; } = Brand.DisplayName;

    /// <summary>Newest first.</summary>
    public ObservableCollection<PacketView> Packets { get; } = [];

    [ObservableProperty]
    public partial string ConnectionText { get; set; } = "Starting…";

    [ObservableProperty]
    public partial long PacketCount { get; set; }

    /// <summary>
    /// Starts watching packets and polling status. Awaits on the caller's context, so call it from the UI thread and
    /// the collection is only touched there.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var watch = WatchAsync(cancellationToken);
        var poll = PollStatusAsync(cancellationToken);
        await Task.WhenAll(watch, poll);
    }

    /// <summary>Adds a packet to the top of the list.</summary>
    internal void Add(PacketView packet)
    {
        Packets.Insert(0, packet);
        while (Packets.Count > MaxPackets)
        {
            Packets.RemoveAt(Packets.Count - 1);
        }
    }

    internal void Show(EngineStatus status)
    {
        PacketCount = status.PacketsReceived;
        ConnectionText = status.Connection switch
        {
            ConnectionState.Connected => $"APRS-IS connected · {status.Server}",
            ConnectionState.Connecting => "APRS-IS connecting…",
            ConnectionState.Faulted => "APRS-IS connection failed · retrying",
            _ => "APRS-IS disconnected",
        };
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var packet in engine.WatchPacketsAsync(cancellationToken))
            {
                Add(packet);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task PollStatusAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), time);
        try
        {
            do
            {
                Show(await engine.GetStatusAsync(cancellationToken));
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) { }
    }
}
