// SPDX-License-Identifier: AGPL-3.0-only
using System.Net.Http.Headers;
using AprsMap.Client.Remote;
using AprsMap.Contracts;
using AprsMap.UI.Platform;
using Microsoft.Extensions.Logging;

namespace AprsMap.Desktop;

/// <summary>
/// The desktop app as a gateway's client (docs/features/F1.39-desktop-gateway-client.md, behaviors 2, 6): the gateway's
/// hub, tiles and <c>/auth</c>, each with the API key when there is one. A gateway that can't be reached at start is
/// tried again with backoff while the app shows "Reconnecting to the gateway…", as the browser app does after a loss.
/// </summary>
internal sealed partial class GatewayConnection : IAsyncDisposable
{
    /// <summary>How long it waits between attempts to connect at start, at most.</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _stop = new();
    private readonly HttpClient _http;
    private Task _connecting = Task.CompletedTask;

    public GatewayConnection(Uri gateway, string? apiKey)
    {
        Gateway = gateway.AbsolutePath.EndsWith('/') ? gateway : new Uri(gateway + "/");
        Engine = RemoteEngineService.Create(Gateway, apiKey);
        _http = new HttpClient { BaseAddress = Gateway };
        if (!string.IsNullOrEmpty(apiKey))
        {
            // F1.38 behavior 5: as a header, never in an address.
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(ApiKeys.Scheme, apiKey);
        }
        Tiles = new GatewayTileSource(_http);
        OperatorSession = new GatewayOperatorSession(_http, passwordSignIn: false);
    }

    /// <summary>The gateway's address.</summary>
    public Uri Gateway { get; }

    /// <summary>The gateway's engine.</summary>
    public RemoteEngineService Engine { get; }

    /// <summary>The gateway's map tiles.</summary>
    public ITileSource Tiles { get; }

    /// <summary>Who this app is on the gateway: the operator with the key, otherwise a viewer.</summary>
    public IOperatorSession OperatorSession { get; }

    /// <summary>Connects in the background, trying again until it's connected or the app closes.</summary>
    public void Start(ILogger logger) => _connecting = ConnectAsync(logger, _stop.Token);

    private async Task ConnectAsync(ILogger logger, CancellationToken cancellationToken)
    {
        var wait = TimeSpan.FromSeconds(2);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Engine.StartAsync(cancellationToken).ConfigureAwait(false);
                LogConnected(logger, Gateway);
                return; // from now on the hub reconnects by itself
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IHasEngineError)
            {
                LogNotReached(logger, Gateway, ex.Message, wait.TotalSeconds);
            }
            try
            {
                await Task.Delay(wait, TimeProvider.System, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, MaxWait.Ticks));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _connecting.ConfigureAwait(false);
        await Engine.DisposeAsync().ConfigureAwait(false);
        _http.Dispose();
        _stop.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to the gateway at {Gateway}")]
    private static partial void LogConnected(ILogger logger, Uri gateway);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The gateway at {Gateway} can't be reached ({Reason}); trying again in {Seconds} s"
    )]
    private static partial void LogNotReached(ILogger logger, Uri gateway, string reason, double seconds);
}
