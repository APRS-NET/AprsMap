// SPDX-License-Identifier: MIT
using System.Text.Json;
using System.Text.Json.Serialization;
using AprsMap.Contracts;

namespace AprsMap.Core;

/// <summary>
/// Update and restart on the Linux service gateway (docs/features/F1.40-gateway-service-updates.md, behaviors 4, 5, 7):
/// the engine only writes the request, <c>update-request.json</c> with the version, to its data folder; the updater,
/// run as root by systemd, does the rest and reports in <c>update-status.json</c>, which the update status carries.
/// </summary>
public sealed partial class Engine
{
    /// <summary>How this engine is installed, for an app connected to it (#453).</summary>
    internal EngineInstall Install =>
        _options.Updates.Kind switch
        {
            InstallKind.Archive or InstallKind.Installed => EngineInstall.Desktop,
            InstallKind.Service => EngineInstall.Service,
            // Microsoft's .NET images set this; the gateway's container is built on them.
            _ when Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" =>
                EngineInstall.Container,
            _ => EngineInstall.Other,
        };

    /// <summary>The request the updater picks up.</summary>
    public const string UpdateRequestFile = "update-request.json";

    /// <summary>Where the updater reports how far it has got.</summary>
    public const string UpdateStatusFile = "update-status.json";

    /// <summary>A request the updater hasn't picked up in this long is dropped (behavior 7).</summary>
    internal static readonly TimeSpan StaleRequest = TimeSpan.FromMinutes(15);

    /// <summary>How often a running update's status is read again, for the notice.</summary>
    internal static readonly TimeSpan InstallPoll = TimeSpan.FromSeconds(2);

    // The last update finished (done or failed), read once and kept until the next start: shown once (behavior 5).
    private ServiceInstall? _installFinished;
    private readonly CancellationTokenSource _stopping = new();

    private string UpdateFile(string name) => Path.Combine(SettingsLocation.DataFolder(_options), name);

    /// <inheritdoc />
    public async Task InstallUpdateAsync(string version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (_options.Updates.Kind != InstallKind.Service)
        {
            throw NotAService();
        }
        var status = await GetUpdateStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.Update?.Version != version || status.Install is { Running: true })
        {
            throw EngineErrors.Create(
                EngineErrorCodes.UpdateNotOffered,
                $"{version} isn't the update offered, or an update is already under way."
            );
        }
        var request = UpdateFile(UpdateRequestFile);
        Directory.CreateDirectory(Path.GetDirectoryName(request)!);
        File.Delete(UpdateFile(UpdateStatusFile)); // the last one's result is no longer news
        lock (_gate)
        {
            _installFinished = null;
        }
        var temporary = request + ".tmp";
        await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(new UpdateRequest(version), ServiceUpdateJson.Default.UpdateRequest),
                cancellationToken
            )
            .ConfigureAwait(false);
        File.Move(temporary, request, overwrite: true); // the path unit sees it whole
        PublishUpdate(await GetUpdateStatusAsync(cancellationToken).ConfigureAwait(false));
        _ = WatchInstallAsync(_stopping.Token); // ends when the update does, or with the engine
    }

    /// <inheritdoc />
    /// <remarks>The gateway restarts itself (F1.40 behavior 2); the engine has no host to stop.</remarks>
    public Task RestartGatewayAsync(CancellationToken cancellationToken = default) => Task.FromException(NotAService());

    private static Exception NotAService() =>
        EngineErrors.Create(
            EngineErrorCodes.GatewayNotService,
            "Only the Linux service gateway updates and restarts itself from here."
        );

    /// <summary>Publishes the update's progress until it's done, failed, or this engine stops.</summary>
    private async Task WatchInstallAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(InstallPoll, _time, cancellationToken).ConfigureAwait(false);
                var status = await GetUpdateStatusAsync(cancellationToken).ConfigureAwait(false);
                PublishUpdate(status);
                if (status.Install is not { Running: true })
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    /// <summary>
    /// Where the service's update stands: the updater's report, a request not yet picked up, or the last result, kept
    /// once read and its file removed so it's shown once; null for none or elsewhere.
    /// </summary>
    private ServiceInstall? ServiceInstallNow()
    {
        if (_options.Updates.Kind != InstallKind.Service)
        {
            return null;
        }
        lock (_gate)
        {
            if (_installFinished is { } finished)
            {
                return finished;
            }
        }
        var reported = ReadReport(UpdateFile(UpdateStatusFile));
        if (reported is { Running: false })
        {
            lock (_gate)
            {
                _installFinished = reported;
            }
            TryDelete(UpdateFile(UpdateStatusFile));
            return reported;
        }
        if (reported is not null)
        {
            return reported;
        }
        var request = UpdateFile(UpdateRequestFile);
        if (ReadRequest(request) is not { } version)
        {
            return null;
        }
        if (_time.GetUtcNow() - File.GetLastWriteTimeUtc(request) <= StaleRequest)
        {
            return new ServiceInstall(version, ServiceInstallStep.Requested);
        }
        // Never picked up: the updater isn't installed or isn't running (behavior 7).
        TryDelete(request);
        var stale = new ServiceInstall(
            version,
            ServiceInstallStep.Failed,
            $"The updater didn't start: check that {Brand.Slug}-gateway-update.path is enabled."
        );
        lock (_gate)
        {
            _installFinished = stale;
        }
        return stale;
    }

    private static ServiceInstall? ReadReport(string path)
    {
        try
        {
            if (
                !File.Exists(path)
                || JsonSerializer.Deserialize(File.ReadAllText(path), ServiceUpdateJson.Default.UpdateReport)
                    is not { Version: { } version, Step: { } step } report
                || !Enum.TryParse<ServiceInstallStep>(step, ignoreCase: true, out var parsed)
            )
            {
                return null;
            }
            return new ServiceInstall(version, parsed, string.IsNullOrWhiteSpace(report.Reason) ? null : report.Reason);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null; // half written: the next read has it
        }
    }

    private static string? ReadRequest(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), ServiceUpdateJson.Default.UpdateRequest)?.Version
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary><c>update-request.json</c>: the version only, so a request can't name a URL, a file or a command.</summary>
internal sealed record UpdateRequest(string Version);

/// <summary><c>update-status.json</c>, as the updater writes it: the version, the step in lower case, and why it failed.</summary>
internal sealed record UpdateReport(string? Version, string? Step, string? Reason);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(UpdateRequest))]
[JsonSerializable(typeof(UpdateReport))]
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage] // generated code
internal sealed partial class ServiceUpdateJson : JsonSerializerContext;
