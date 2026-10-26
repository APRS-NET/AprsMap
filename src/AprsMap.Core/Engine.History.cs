// SPDX-License-Identifier: MIT
using Microsoft.Extensions.Logging;

namespace AprsMap.Core;

/// <summary>The dashboard's and the GPS's history kept between runs (issue #386).</summary>
public sealed partial class Engine
{
    private IHistoryStore? _historyStore;
    private readonly SemaphoreSlim _historySaving = new(1, 1);

    private IHistoryStore? HistoryStore =>
        _historyStore ??= string.IsNullOrWhiteSpace(_options.HistoryFile)
            ? null
            : new FileHistoryStore(_options.HistoryFile, _logger);

    /// <summary>Takes up what was kept, if anything, under 24 hours old.</summary>
    private async Task LoadHistoryAsync(CancellationToken cancellationToken)
    {
        if (HistoryStore is not { } store)
        {
            return;
        }
        var snapshot = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return;
        }
        var now = _time.GetUtcNow();
        if (snapshot.Dashboard is { } dashboard)
        {
            _dashboard.Import(dashboard, now);
        }
        if (snapshot.Gps is { } gps)
        {
            _gpsHistory.Import(gps, now);
        }
        LogHistoryLoaded(_logger, snapshot.SavedAt);
    }

    /// <summary>Saves the history every <see cref="EngineOptions.HistorySaveInterval"/> while the engine runs.</summary>
    private async Task RunHistorySavesAsync(CancellationToken cancellationToken)
    {
        if (HistoryStore is null)
        {
            return;
        }
        using var timer = new PeriodicTimer(_options.HistorySaveInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await SaveHistoryAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Saves the history now; a save that fails is logged, and the next one tries again.</summary>
    internal async Task SaveHistoryAsync()
    {
        if (HistoryStore is not { } store)
        {
            return;
        }
        await _historySaving.WaitAsync().ConfigureAwait(false);
        try
        {
            var snapshot = new HistorySnapshot(
                HistorySnapshot.CurrentVersion,
                _time.GetUtcNow(),
                _dashboard.Export(),
                _gpsHistory.Export()
            );
            await store.SaveAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogHistoryNotSaved(_logger, ex);
        }
        finally
        {
            _historySaving.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dashboard history taken up from {SavedAt:u}")]
    private static partial void LogHistoryLoaded(ILogger logger, DateTimeOffset savedAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dashboard history couldn't be saved")]
    private static partial void LogHistoryNotSaved(ILogger logger, Exception error);
}
