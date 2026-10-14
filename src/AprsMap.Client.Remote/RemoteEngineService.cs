// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace AprsMap.Client.Remote;

/// <summary>
/// <see cref="IEngineService"/> backed by a gateway's SignalR hub. The UI cannot tell it apart from the in-process
/// engine, which is what keeps the desktop and web apps identical.
/// </summary>
public sealed class RemoteEngineService : IEngineService, IAsyncDisposable
{
    private readonly HubConnection _connection;

    /// <summary>Wraps an existing hub connection (useful for custom transports and tests).</summary>
    public RemoteEngineService(HubConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    /// <summary>Creates a client for the gateway at <paramref name="gatewayBaseUri"/>, reconnecting automatically.</summary>
    public static RemoteEngineService Create(Uri gatewayBaseUri)
    {
        ArgumentNullException.ThrowIfNull(gatewayBaseUri);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(gatewayBaseUri, EngineHub.Path))
            .WithAutomaticReconnect(new KeepTryingPolicy())
            // The browser app has no reflection-based JSON (WebAssembly publish turns it off).
            .AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, EngineJsonContext.Default))
            .Build();
        return new RemoteEngineService(connection);
    }

    /// <summary>The underlying connection state.</summary>
    public HubConnectionState State => _connection.State;

    /// <summary>Connects to the gateway. Call once before using the service.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default) => _connection.StartAsync(cancellationToken);

    /// <inheritdoc />
    public Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        _connection.InvokeAsync<EngineStatus>(EngineHub.GetStatus, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<PacketView> WatchPacketsAsync(CancellationToken cancellationToken = default) =>
        _connection.StreamAsync<PacketView>(EngineHub.WatchPackets, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<StationBatch> WatchStationsAsync(CancellationToken cancellationToken = default) =>
        _connection.StreamAsync<StationBatch>(EngineHub.WatchStations, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    /// <summary>
    /// Reconnects quickly at first, then every 30 seconds for as long as it takes. SignalR's default gives up after
    /// four attempts, which would strand a browser while its gateway restarts or a Pi reboots.
    /// </summary>
    internal sealed class KeepTryingPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] FirstDelays =
        [
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
        ];

        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            retryContext.PreviousRetryCount < FirstDelays.Length
                ? FirstDelays[retryContext.PreviousRetryCount]
                : TimeSpan.FromSeconds(30);
    }
}
