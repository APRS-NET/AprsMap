// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json;
using System.Text.Json.Serialization;
using AprsMap.Server;
using AprsMap.UI.Platform;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;

namespace AprsMap.Desktop;

/// <summary>
/// The desktop's web viewer (docs/features/F1.35-desktop-web-viewer.md): its settings kept with the user's
/// preferences, and the shared server (<see cref="WebViewer"/>) started, restarted and stopped to match them, around the
/// desktop's own engine. The browser app comes with the desktop app, in <c>browser</c> beside the program.
/// </summary>
internal sealed partial class DesktopWebViewer(
    IServiceProvider host,
    IPreferenceStore preferences,
    string webRoot,
    ILogger<DesktopWebViewer> logger
) : IWebViewerHost, IAsyncDisposable
{
    /// <summary>The preference the settings are kept under.</summary>
    internal const string PreferenceKey = "web-viewer";

    private readonly SemaphoreSlim _applying = new(1, 1);
    private WebViewer? _viewer;
    private WebViewerProblem _problem;
    private string? _detail;

    /// <inheritdoc />
    public bool Available { get; } = File.Exists(Path.Combine(webRoot, "index.html"));

    /// <inheritdoc />
    public WebViewerSettings Settings { get; private set; } = new();

    /// <inheritdoc />
    public WebViewerState State =>
        _viewer is { } viewer ? new WebViewerState(true, viewer.Addresses, viewer.ViewerCount)
        : _problem == WebViewerProblem.None ? WebViewerState.Off
        : new WebViewerState(false, [], 0, _problem, _detail);

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>Reads the settings kept, and starts sharing when it was on (behavior 2).</summary>
    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        if (await preferences.LoadAsync(PreferenceKey, cancellationToken).ConfigureAwait(false) is { } stored)
        {
            try
            {
                Settings = JsonSerializer.Deserialize(stored, WebViewerJson.Default.WebViewerSettings) ?? new();
            }
            catch (JsonException)
            {
                await preferences.SetAsideAsync(PreferenceKey, cancellationToken).ConfigureAwait(false);
            }
        }
        await MatchAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ApplyAsync(WebViewerSettings settings, CancellationToken cancellationToken = default)
    {
        Settings = settings;
        await preferences
            .SaveAsync(
                PreferenceKey,
                JsonSerializer.Serialize(settings, WebViewerJson.Default.WebViewerSettings),
                cancellationToken
            )
            .ConfigureAwait(false);
        await MatchAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops what's running, then starts on the settings when they say on and the browser app is here.</summary>
    private async Task MatchAsync(CancellationToken cancellationToken)
    {
        await _applying.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_viewer is { } running)
            {
                _viewer = null;
                await running.DisposeAsync().ConfigureAwait(false);
            }
            (_problem, _detail) = (WebViewerProblem.None, null);
            if (Settings.On && Available)
            {
                try
                {
                    _viewer = await WebViewer
                        .StartAsync(
                            new WebViewerOptions(
                                Settings.Port,
                                Settings.LocalNetwork,
                                webRoot,
                                Settings.Location switch
                                {
                                    ViewerLocation.Grid => HomePrivacy.Grid,
                                    ViewerLocation.Hidden => HomePrivacy.Hidden,
                                    _ => HomePrivacy.Exact,
                                }
                            ),
                            host,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    LogSharing(logger, Settings.Port, Settings.LocalNetwork);
                }
                catch (Exception ex)
                    when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
                {
                    var inUse = ex is AddressInUseException || ex.InnerException is AddressInUseException;
                    (_problem, _detail) = (
                        inUse ? WebViewerProblem.PortInUse : WebViewerProblem.CantListen,
                        ex.Message
                    );
                    LogCantShare(logger, Settings.Port, ex);
                }
            }
        }
        finally
        {
            _applying.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops sharing as the app closes.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_viewer is { } running)
        {
            _viewer = null;
            await running.DisposeAsync().ConfigureAwait(false);
        }
        _applying.Dispose();
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Sharing with browsers on port {Port} (local network: {LocalNetwork})"
    )]
    private static partial void LogSharing(ILogger logger, int port, bool localNetwork);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't share with browsers on port {Port}")]
    private static partial void LogCantShare(ILogger logger, int port, Exception error);
}

/// <summary>The web viewer's settings as kept, trimming-safe.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(WebViewerSettings))]
internal sealed partial class WebViewerJson : JsonSerializerContext;
