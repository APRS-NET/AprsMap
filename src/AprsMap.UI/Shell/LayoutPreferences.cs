// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Platform;

namespace AprsMap.UI.Shell;

/// <summary>
/// Saves the viewer's layout through their <see cref="IPreferenceStore"/> (behavior 8): <c>layout.json</c> on the
/// desktop, browser storage on the web. Saving waits for a pause in changes, as appearance does.
/// </summary>
public sealed class LayoutPreferences(IPreferenceStore store, TimeProvider time) : IDisposable
{
    /// <summary>The key the layout is stored under.</summary>
    public const string Key = "layout";

    /// <summary>How long to wait after the last change before writing.</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private ITimer? _timer;
    private Func<string>? _pending;

    /// <summary>The stored text, or null when there is none or the store can't be read (never throws).</summary>
    public async Task<string?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await store.LoadAsync(Key, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Moves an unreadable layout aside (behavior 10), so it can be looked at rather than lost.</summary>
    public async Task SetAsideAsync()
    {
        try
        {
            await store.SetAsideAsync(Key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    /// <summary>Saves once changes pause for <see cref="SaveDelay"/>; <paramref name="write"/> runs then, on the timer.</summary>
    public void Schedule(Func<string> write)
    {
        lock (_gate)
        {
            _pending = write;
            _timer ??= time.CreateTimer(
                _ => _ = FlushAsync(),
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan
            );
            _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Saves whatever is waiting now (on exit, and for tests).</summary>
    public async Task FlushAsync()
    {
        Func<string>? write;
        lock (_gate)
        {
            write = _pending;
            _pending = null;
        }
        if (write is not null)
        {
            await SaveAsync(write());
        }
    }

    /// <summary>Saves <paramref name="text"/> now.</summary>
    public async Task SaveAsync(string text)
    {
        try
        {
            await store.SaveAsync(Key, text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nowhere to save (private browsing): the layout still applies for this session.
        }
    }

    /// <summary>Drops a save that is waiting, for Reset the app (F0.10), which deletes the stored value instead.</summary>
    public void Discard()
    {
        lock (_gate)
        {
            _pending = null;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _timer?.Dispose();
}
