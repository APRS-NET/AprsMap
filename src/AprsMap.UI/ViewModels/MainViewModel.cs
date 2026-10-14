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

    /// <summary>The connection text while the engine can't be reached (for the browser app, the gateway).</summary>
    public const string EngineUnreachableText = "Engine unreachable · retrying";

    /// <summary>How long to wait before asking the engine again after a call or stream fails.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, StationState> _stations = new(StringComparer.Ordinal);

    /// <summary>The product name, from branding.</summary>
    public string Title { get; } = Brand.DisplayName;

    /// <summary>Newest first.</summary>
    public ObservableCollection<PacketView> Packets { get; } = [];

    [ObservableProperty]
    public partial string ConnectionText { get; set; } = "Starting…";

    [ObservableProperty]
    public partial long PacketCount { get; set; }

    [ObservableProperty]
    public partial int StationCount { get; set; }

    /// <summary>The latest error talking to the engine (for example, the gateway is unreachable), or null.</summary>
    [ObservableProperty]
    public partial string? ErrorText { get; set; }

    /// <summary>
    /// Starts watching packets and stations and polling status, until cancelled. A failure (the gateway restarted, say)
    /// shows in <see cref="ErrorText"/>; failed calls and failed or ended streams are retried. Awaits on the caller's
    /// context, so call it from the UI thread and the collections are only touched there.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var packets = KeepAsync(WatchPacketsAsync, cancellationToken);
        var stations = KeepAsync(WatchStationsAsync, cancellationToken);
        var status = KeepAsync(PollStatusAsync, cancellationToken);
        await Task.WhenAll(packets, stations, status);
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

    /// <summary>Applies one batch from the station stream.</summary>
    internal void Apply(StationBatch batch)
    {
        foreach (var station in batch.Changed)
        {
            _stations[station.Name] = station;
        }
        foreach (var name in batch.Removed)
        {
            _stations.Remove(name);
        }
        StationCount = _stations.Count;
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

    /// <summary>Runs <paramref name="run"/> until cancelled, retrying after failures and after the stream ends.</summary>
    private async Task KeepAsync(Func<CancellationToken, Task> run, CancellationToken cancellationToken)
    {
        // Leaves only through cancellation: of the run, or of the wait before the next one.
        while (true)
        {
            try
            {
                await run(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Any failure talking to the engine is shown and retried, never left unobserved.
                ErrorText = ex.Message;
            }

            try
            {
                await Task.Delay(RetryDelay, time, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task WatchPacketsAsync(CancellationToken cancellationToken)
    {
        // The engine replays its recent packets on every subscription, so a resubscription replaces the list. Keep
        // the old one on screen until the replay starts.
        var first = true;
        await foreach (var packet in engine.WatchPacketsAsync(cancellationToken))
        {
            if (first)
            {
                Packets.Clear();
                first = false;
            }
            Add(packet);
        }
    }

    private async Task WatchStationsAsync(CancellationToken cancellationToken)
    {
        // The first batches are a full snapshot, so a resubscription starts from scratch once it arrives.
        var first = true;
        await foreach (var batch in engine.WatchStationsAsync(cancellationToken))
        {
            if (first)
            {
                _stations.Clear();
                first = false;
            }
            Apply(batch);
        }
    }

    private async Task PollStatusAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), time);
        do
        {
            EngineStatus status;
            try
            {
                status = await engine.GetStatusAsync(cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The last APRS-IS state shown is no longer known.
                ConnectionText = EngineUnreachableText;
                throw;
            }
            Show(status);
            ErrorText = null;
        } while (await timer.WaitForNextTickAsync(cancellationToken));
    }
}
