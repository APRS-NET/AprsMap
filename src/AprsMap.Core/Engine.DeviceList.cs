// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.DeviceId;
using Microsoft.Extensions.Logging;

namespace AprsMap.Core;

/// <summary>
/// The device list (docs/features/F4.16-device-list.md): the one built into APRS.NET, or a newer one downloaded from
/// the APRS Foundation or a mirror and kept as published; checked weekly (or daily, or never) and on Update now, never
/// while the Internet switch is off. Reading and fetching the list are APRS.NET's (G35); when, where it's kept and which
/// is used are the engine's.
/// </summary>
public sealed partial class Engine
{
    /// <summary>How long after start the first scheduled check waits, so start-up isn't slowed (behavior 4).</summary>
    internal static readonly TimeSpan FirstDeviceListCheck = TimeSpan.FromMinutes(3);

    /// <summary>The <see cref="IHttpClientFactory"/> client name for the device list.</summary>
    public const string DeviceListHttpClient = "devicelist";

    /// <summary>The downloaded list's file name, kept unchanged from how it was published (decision 7).</summary>
    internal const string DeviceListFile = "tocalls.json";

    private readonly SemaphoreSlim _checkingDevices = new(1, 1);
    private AprsDeviceList _devices = AprsDeviceList.BuiltIn; // under _gate
    private bool _devicesDownloaded; // under _gate
    private EngineError? _deviceListError; // under _gate
    private bool _deviceListChecking; // under _gate

    /// <summary>The HTTP client the device list is fetched with; tests give their own.</summary>
    internal HttpClient? DeviceListHttp { get; set; }

    private string? DeviceListPath =>
        string.IsNullOrWhiteSpace(_options.DeviceList.Folder)
            ? null
            : Path.Combine(_options.DeviceList.Folder, DeviceListFile);

    /// <summary>
    /// The address the list is fetched from (behavior 10): the desktop's setting when it has one, otherwise the
    /// configured one; null when downloading is off (an empty address).
    /// </summary>
    private Uri? DeviceListUrl(AppSettings settings)
    {
        var url = _options.DeviceList.UrlIsSetting
            ? settings.EffectiveDeviceList.Url ?? _options.DeviceList.Url
            : _options.DeviceList.Url;
        return string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ? null : uri;
    }

    /// <inheritdoc />
    public async Task<DeviceListStatus> GetDeviceListAsync(CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return DeviceListStatusLocked(settings);
        }
    }

    /// <inheritdoc />
    public async Task<DeviceListStatus> UpdateDeviceListAsync(CancellationToken cancellationToken = default)
    {
        await CheckDeviceListNowAsync(cancellationToken).ConfigureAwait(false);
        return await GetDeviceListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The list in use and the last check, for the status and Settings › Data. Call under <see cref="_gate"/>.</summary>
    private DeviceListStatus DeviceListStatusLocked(AppSettings settings)
    {
        var url = DeviceListUrl(settings);
        return new DeviceListStatus(
            _devicesDownloaded ? DeviceListOrigin.Downloaded : DeviceListOrigin.BuiltIn,
            _devices.GeneratedAt,
            _devices.Commit,
            _devices.TocallCount,
            settings.EffectiveDeviceList.LastChecked,
            _deviceListError,
            url is not null,
            url?.ToString(),
            _deviceListChecking,
            _options.DeviceList.UrlIsSetting
        );
    }

    /// <summary>
    /// On start (behavior 2): the downloaded list when it reads and is newer than the built-in one; otherwise it's
    /// deleted (an app update brought a newer built-in list, or it can't be read) and the built-in one is used.
    /// </summary>
    internal void LoadSavedDeviceList()
    {
        if (DeviceListPath is not { } path || !File.Exists(path))
        {
            return;
        }
        AprsDeviceList? loaded;
        string? why;
        try
        {
            using var file = File.OpenRead(path);
            if (!AprsDeviceList.TryParse(file, out loaded, out why))
            {
                loaded = null;
            }
        }
        catch (IOException ex)
        {
            (loaded, why) = (null, ex.Message);
        }
        if (loaded is not null && loaded.IsNewerThan(AprsDeviceList.BuiltIn))
        {
            UseDevices(loaded, downloaded: true);
            return;
        }
        TryDelete(path);
        if (loaded is null)
        {
            lock (_gate)
            {
                _deviceListError = new EngineError(
                    EngineErrorCodes.DeviceListUnreadable,
                    $"The downloaded device list can't be read: {why}"
                );
            }
        }
    }

    private void UseDevices(AprsDeviceList list, bool downloaded)
    {
        lock (_gate)
        {
            _devices = list;
            _devicesDownloaded = downloaded;
        }
        var changed = _stations.UseDeviceList(list); // stations already heard named again (behavior 3)
        if (_deviceListLog.IsEnabled(LogLevel.Information))
        {
            var generated = list.GeneratedAt?.ToString("u", System.Globalization.CultureInfo.InvariantCulture) ?? "?";
            LogDeviceListInUse(_deviceListLog, downloaded ? "downloaded" : "built-in", generated, changed);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Checks once now (behaviors 5–8): one anonymous request with the kept copy's ETag; a newer list that reads is kept
    /// as published and used at once; anything else leaves the list in use and says why. The time and ETag are kept
    /// with the settings. Nothing is fetched with downloading off or the Internet switch off (behavior 7).
    /// </summary>
    internal async Task CheckDeviceListNowAsync(CancellationToken cancellationToken)
    {
        await _checkingDevices.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (
                DeviceListUrl(settings) is not { } url
                || !settings.EffectiveToggles.Internet
                || (DeviceListHttp ?? _httpClients?.CreateClient(DeviceListHttpClient)) is not { } http
            )
            {
                return;
            }
            lock (_gate)
            {
                _deviceListChecking = true;
            }
            var stored = settings.EffectiveDeviceList;
            AprsDeviceList current;
            bool downloaded;
            lock (_gate)
            {
                (current, downloaded) = (_devices, _devicesDownloaded);
            }
            // The ETag stands for the kept file: without it, ask for the whole list.
            var etag = downloaded ? stored.ETag : null;
            var fetch = await new AprsDeviceListSource(http, url)
                .FetchAsync(etag, cancellationToken)
                .ConfigureAwait(false);
            EngineError? error = null;
            switch (fetch.Outcome)
            {
                case AprsDeviceListFetchOutcome.Fetched when fetch.List is { } list && list.IsNewerThan(current):
                    if (DeviceListPath is { } path)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        var temporary = path + ".new";
                        await File.WriteAllBytesAsync(temporary, fetch.Content.ToArray(), cancellationToken)
                            .ConfigureAwait(false);
                        File.Move(temporary, path, overwrite: true);
                    }
                    UseDevices(list, downloaded: true);
                    etag = fetch.ETag;
                    break;
                case AprsDeviceListFetchOutcome.Fetched:
                    error = new EngineError(
                        EngineErrorCodes.DeviceListNotNewer,
                        "The list downloaded is no newer than the one in use."
                    );
                    break;
                case AprsDeviceListFetchOutcome.Unchanged:
                    break;
                case AprsDeviceListFetchOutcome.Unreadable:
                    error = new EngineError(
                        EngineErrorCodes.DeviceListUnreadable,
                        fetch.Error ?? "The list can't be read."
                    );
                    break;
                case AprsDeviceListFetchOutcome.UnexpectedAnswer:
                    error = new EngineError(
                        EngineErrorCodes.DeviceListUnexpectedAnswer,
                        fetch.Error ?? $"The server answered {(int?)fetch.StatusCode}."
                    );
                    break;
                default:
                    error = new EngineError(
                        EngineErrorCodes.DeviceListUnreachable,
                        fetch.Error ?? "The server couldn't be reached."
                    );
                    break;
            }
            if (error is not null && _deviceListLog.IsEnabled(LogLevel.Information))
            {
                var message = error.Message;
                LogDeviceListCheck(_deviceListLog, message);
            }
            var latest = await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
            await SaveAsync(
                    latest with
                    {
                        DeviceList = latest.EffectiveDeviceList with { LastChecked = _time.GetUtcNow(), ETag = etag },
                    },
                    trusted: true,
                    cancellationToken
                )
                .ConfigureAwait(false);
            lock (_gate)
            {
                _deviceListError = error;
            }
        }
        finally
        {
            lock (_gate)
            {
                _deviceListChecking = false;
            }
            _checkingDevices.Release();
        }
    }

    /// <summary>
    /// The schedule (behavior 4): a few minutes after start, or an interval after the last check when that's later,
    /// then every interval; with the schedule off, or downloading off, it only looks again hourly in case that changes.
    /// While the Internet switch is off, a due check waits for it.
    /// </summary>
    internal async Task RunDeviceListAsync(CancellationToken cancellationToken)
    {
        LoadSavedDeviceList();
        try
        {
            var wait = FirstDeviceListCheck;
            while (true)
            {
                var settings = await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
                if (settings.EffectiveDeviceList is { LastChecked: { } last, Interval: { } every })
                {
                    var due = last + every - _time.GetUtcNow();
                    if (due > wait)
                    {
                        wait = due;
                    }
                }
                await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
                settings = await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
                var scheduled = settings.EffectiveDeviceList.Interval;
                var ready = scheduled is not null && DeviceListUrl(settings) is not null;
                if (ready && settings.EffectiveToggles.Internet)
                {
                    await CheckDeviceListNowAsync(cancellationToken).ConfigureAwait(false);
                    wait = scheduled!.Value;
                }
                else
                {
                    wait = TimeSpan.FromHours(1);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Device list in use: {Origin}, generated {Generated}; {Changed} stations named again"
    )]
    private static partial void LogDeviceListInUse(ILogger logger, string origin, string generated, int changed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device list check: {Message}")]
    private static partial void LogDeviceListCheck(ILogger logger, string message);
}
