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
    public Task<IReadOnlyList<ApiKeyInfo>> GetApiKeysAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<ApiKeyInfo>>(NotHere());

    /// <inheritdoc />
    public Task<ApiKeyCreated> CreateApiKeyAsync(
        string name,
        ApiKeyAccess access,
        CancellationToken cancellationToken = default
    ) => Task.FromException<ApiKeyCreated>(NotHere());

    /// <inheritdoc />
    public Task RevokeApiKeyAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromException(NotHere());

    /// <inheritdoc />
    public Task<ApiKeyCheck> CheckApiKeyAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromException<ApiKeyCheck>(NotHere());

    private static Exception NotHere() =>
        EngineErrors.Create(EngineErrorCodes.ApiKeyNotHere, "Only a gateway has an API key.");
}
