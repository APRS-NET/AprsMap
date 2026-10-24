// SPDX-License-Identifier: AGPL-3.0-only
using System.Diagnostics.Metrics;
using System.Threading.RateLimiting;
using AprsMap.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace AprsMap.Server;

/// <summary>
/// The browsers connected to the gateway (docs/features/F1.14-gateway.md, behaviors 3 and 11): how many, from which
/// address, and whether one more may join. Counted by hub connection, so a browser is one viewer however many streams
/// it runs.
/// </summary>
internal sealed partial class Viewers : IDisposable
{
    /// <summary>The gateway's meter, for <c>aprsmap.gateway.viewers</c>.</summary>
    public const string MeterName = "AprsMap.Gateway";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _byConnection = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _byAddress = new(StringComparer.Ordinal);
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly ILogger<Viewers> _logger;
    private readonly Meter _meter = new(MeterName);

    public Viewers(IOptionsMonitor<GatewayOptions> options, ILogger<Viewers> logger)
    {
        _options = options;
        _logger = logger;
        _meter.CreateObservableGauge(
            "aprsmap.gateway.viewers",
            () => Count,
            unit: "{viewer}",
            description: "Browsers connected to the gateway."
        );
    }

    /// <summary>How many browsers are connected.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byConnection.Count;
            }
        }
    }

    /// <summary>Whether a browser from <paramref name="address"/> may connect now; a refusal is logged with the address.</summary>
    public bool CanJoin(string address)
    {
        bool full;
        lock (_gate)
        {
            full = IsFull(address);
        }
        if (full)
        {
            LogRefused(address);
        }
        return !full;
    }

    /// <summary>Counts a connected browser; false when it got in over a limit after all (two joining at once).</summary>
    public bool Join(string connectionId, string address)
    {
        lock (_gate)
        {
            if (IsFull(address))
            {
                return false;
            }
            _byConnection[connectionId] = address;
            _byAddress[address] = _byAddress.GetValueOrDefault(address) + 1;
        }
        LogJoined();
        return true;
    }

    /// <summary>Forgets a browser that left.</summary>
    public void Leave(string connectionId)
    {
        lock (_gate)
        {
            if (!_byConnection.Remove(connectionId, out var address))
            {
                return;
            }
            if (--_byAddress[address] == 0)
            {
                _byAddress.Remove(address);
            }
        }
        LogLeft();
    }

    public void Dispose() => _meter.Dispose();

    /// <summary>The address limits count by: the viewer's, after trusted forwarded headers (F1.14 behavior 2).</summary>
    public static string AddressOf(HttpContext? context) =>
        context?.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    // Under the lock.
    private bool IsFull(string address)
    {
        var options = _options.CurrentValue;
        return _byConnection.Count >= options.MaxViewers
            || _byAddress.GetValueOrDefault(address) >= options.MaxViewersPerAddress;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A viewer connected")]
    private partial void LogJoined();

    [LoggerMessage(Level = LogLevel.Information, Message = "A viewer disconnected")]
    private partial void LogLeft();

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Refused a viewer from {Address}: the gateway is at its viewer limit"
    )]
    private partial void LogRefused(string address);
}

/// <summary>
/// Checks a new browser at the hub's negotiate request, before any connection is made: an app on another contract
/// version is refused with <see cref="EngineErrorCodes.GatewayOutdated"/> in a 409 (F1.14 behavior 8), and when the
/// gateway is full, with <see cref="EngineErrorCodes.GatewayBusy"/> in a 503 (behavior 3).
/// </summary>
internal static class ViewerGate
{
    private static readonly string Version = EngineHub.ContractVersion.ToString(
        System.Globalization.CultureInfo.InvariantCulture
    );

    public static void UseViewerGate(this WebApplication app) =>
        app.Use(
            async (context, next) =>
            {
                if (
                    context.Request.Path.StartsWithSegments(EngineHub.Path + "/negotiate")
                    && context.Request.Query[EngineHub.ContractQuery] != Version
                )
                {
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    await context.Response.WriteAsync(
                        EngineHub.EncodeError(
                            new EngineError(EngineErrorCodes.GatewayOutdated, EngineHub.OutdatedMessage)
                        )
                    );
                    return;
                }
                if (
                    context.Request.Path.StartsWithSegments(EngineHub.Path + "/negotiate")
                    && !context.RequestServices.GetRequiredService<Viewers>().CanJoin(Viewers.AddressOf(context))
                )
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.Headers.RetryAfter = "30";
                    await context.Response.WriteAsync(
                        EngineHub.EncodeError(new EngineError(EngineErrorCodes.GatewayBusy, EngineHub.BusyMessage))
                    );
                    return;
                }
                await next(context);
            }
        );
}

/// <summary>
/// Counts each hub connection as a viewer and holds it to a call rate (F1.14 behaviors 3 and 4): a token bucket per
/// connection, refilled at <see cref="GatewayOptions.CallsPerSecond"/> up to <see cref="GatewayOptions.CallBurst"/>.
/// A call over it fails with <see cref="EngineErrorCodes.GatewayRateLimited"/>; the browser backs off and retries.
/// </summary>
internal sealed class ViewerFilter(Viewers viewers, IOptionsMonitor<GatewayOptions> options, Core.Engine engine)
    : IHubFilter
{
    private const string LimiterKey = "aprsmap.limiter";

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var hub = context.Context;
        if (!viewers.Join(hub.ConnectionId, Viewers.AddressOf(hub.GetHttpContext())))
        {
            hub.Abort(); // got past the gate as the last place went
            return;
        }
        var limits = options.CurrentValue;
        hub.Items[LimiterKey] = new TokenBucketRateLimiter(
            new TokenBucketRateLimiterOptions
            {
                TokenLimit = Math.Max(1, limits.CallBurst),
                TokensPerPeriod = Math.Max(1, limits.CallsPerSecond),
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }
        );
        await next(context);
    }

    public async Task OnDisconnectedAsync(
        HubLifetimeContext context,
        Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next
    )
    {
        viewers.Leave(context.Context.ConnectionId);
        engine.SetMapView(context.Context.ConnectionId, null); // its map no longer counts for the filter
        if (context.Context.Items.TryGetValue(LimiterKey, out var limiter) && limiter is IDisposable disposable)
        {
            disposable.Dispose();
        }
        await next(context, exception);
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next
    )
    {
        if (invocationContext.Context.Items.TryGetValue(LimiterKey, out var item) && item is RateLimiter limiter)
        {
            using var lease = limiter.AttemptAcquire();
            if (!lease.IsAcquired)
            {
                throw new HubException(
                    EngineHub.EncodeError(
                        new EngineError(EngineErrorCodes.GatewayRateLimited, EngineHub.RateLimitedMessage)
                    )
                );
            }
        }
        return await next(invocationContext);
    }
}
