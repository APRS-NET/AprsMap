// SPDX-License-Identifier: MIT
using AprsMap.Contracts;

namespace AprsMap.Core;

/// <summary>
/// The gateway's API key (docs/features/F1.38-gateway-api-key.md) is the gateway's own: the desktop's in-process engine
/// has none, and says so.
/// </summary>
public sealed partial class Engine
{
    /// <inheritdoc />
    public Task<ApiKeyStatus> GetApiKeyStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<ApiKeyStatus>(NotHere());

    /// <inheritdoc />
    public Task<string> GenerateApiKeyAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<string>(NotHere());

    /// <inheritdoc />
    public Task<ApiKeyStatus> SaveApiKeyAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromException<ApiKeyStatus>(NotHere());

    /// <inheritdoc />
    public Task RemoveApiKeyAsync(CancellationToken cancellationToken = default) => Task.FromException(NotHere());

    private static Exception NotHere() =>
        EngineErrors.Create(EngineErrorCodes.ApiKeyNotHere, "Only a gateway has an API key.");
}
