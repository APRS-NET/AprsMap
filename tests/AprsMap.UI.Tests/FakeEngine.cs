// SPDX-License-Identifier: AGPL-3.0-only
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AprsMap.Contracts;

namespace AprsMap.UI.Tests;

internal sealed class FakeEngine : IEngineService
{
    private readonly Channel<PacketView> _packets = Channel.CreateUnbounded<PacketView>();

    public EngineStatus Status { get; set; } = new(ConnectionState.Connected, "T2TEST", 0, "0.0.0");

    public void Push(PacketView packet) => _packets.Writer.TryWrite(packet);

    public Task<EngineStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(Status);

    public async IAsyncEnumerable<PacketView> WatchPacketsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        await foreach (var packet in _packets.Reader.ReadAllAsync(cancellationToken))
        {
            yield return packet;
        }
    }

    public static PacketView Packet(string source) =>
        new(DateTimeOffset.UnixEpoch, source, "Status", "Status", $"{source}>APRS:>hi");
}
