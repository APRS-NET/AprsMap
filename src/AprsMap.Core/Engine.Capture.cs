// SPDX-License-Identifier: MIT
using AprsMap.Contracts;

namespace AprsMap.Core;

/// <summary>Packet capture's service calls (docs/features/F1.27-packet-capture.md).</summary>
public sealed partial class Engine
{
    private readonly PacketCapture? _capture;

    /// <summary>Takes up the settings' capture switch and limits.</summary>
    private void UseCapture(AppSettings settings)
    {
        if (_capture is not null)
        {
            _capture.Settings = settings.EffectiveCapture;
        }
    }

    /// <inheritdoc />
    public Task<CaptureView> GetCaptureAsync(CaptureQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _capture?.ReadAsync(query, cancellationToken) ?? Task.FromResult(new CaptureView([], [], []));
    }

    /// <inheritdoc />
    public Task<CaptureExportResult> ExportCaptureAsync(
        CaptureExport request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        return _capture?.ExportAsync(request, Brand.DisplayName, Brand.Version, AprsNetVersion, cancellationToken)
            ?? Task.FromResult(new CaptureExportResult("capture.log", string.Empty, string.Empty, 0));
    }

    /// <inheritdoc />
    public Task ClearCaptureAsync(CancellationToken cancellationToken = default) =>
        _capture?.ClearAsync(cancellationToken) ?? Task.CompletedTask;

    /// <summary>Waits for capture's queue to be written; for tests.</summary>
    internal Task FlushCaptureAsync(CancellationToken cancellationToken) =>
        _capture?.FlushAsync(cancellationToken) ?? Task.CompletedTask;
}
