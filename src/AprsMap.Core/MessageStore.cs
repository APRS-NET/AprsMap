// SPDX-License-Identifier: MIT
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AprsMap.Core;

/// <summary>Where each profile's conversations are kept (docs/features/F2.4-messaging.md, behavior 5).</summary>
public interface IMessageStore
{
    /// <summary>What's kept for <paramref name="profileId"/>, or null when nothing is.</summary>
    Task<MessageArchive?> LoadAsync(string profileId, CancellationToken cancellationToken);

    /// <summary>Keeps <paramref name="archive"/> for <paramref name="profileId"/>, replacing what was there.</summary>
    Task SaveAsync(string profileId, MessageArchive archive, CancellationToken cancellationToken);
}

/// <summary>Conversations held in memory only: for tests, and for an engine without a settings folder.</summary>
public sealed class InMemoryMessageStore : IMessageStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, MessageArchive> _archives = new(
        StringComparer.Ordinal
    );

    /// <inheritdoc />
    public Task<MessageArchive?> LoadAsync(string profileId, CancellationToken cancellationToken) =>
        Task.FromResult(_archives.GetValueOrDefault(profileId));

    /// <inheritdoc />
    public Task SaveAsync(string profileId, MessageArchive archive, CancellationToken cancellationToken)
    {
        _archives[profileId] = archive;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Each profile's conversations as <c>messages-{profile}.json</c> in the Data folder (F4.15 behavior 13), written through a
/// temporary file, one write at a time. A file that can't be read is set aside as <c>.invalid</c> and starts empty.
/// </summary>
public sealed class FileMessageStore(string folder) : IMessageStore, IDisposable
{
    private readonly SemaphoreSlim _write = new(1, 1);

    /// <summary>The file for <paramref name="profileId"/>.</summary>
    public string PathFor(string profileId) =>
        Path.Combine(folder, $"messages-{string.Concat(profileId.Where(char.IsAsciiLetterOrDigit))}.json");

    /// <inheritdoc />
    public async Task<MessageArchive?> LoadAsync(string profileId, CancellationToken cancellationToken)
    {
        var path = PathFor(profileId);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var stream = File.OpenRead(path);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer
                    .DeserializeAsync(stream, MessageJsonContext.Default.MessageArchive, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (JsonException)
        {
            File.Move(path, path + ".invalid", overwrite: true);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(string profileId, MessageArchive archive, CancellationToken cancellationToken)
    {
        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(folder);
            var path = PathFor(profileId);
            var temporary = path + ".tmp";
            var stream = File.Create(temporary);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer
                    .SerializeAsync(stream, archive, MessageJsonContext.Default.MessageArchive, cancellationToken)
                    .ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            _write.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _write.Dispose();
}

/// <summary>The message files' JSON: enum names rather than numbers.</summary>
[ExcludeFromCodeCoverage] // generated code
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(MessageArchive))]
internal sealed partial class MessageJsonContext : JsonSerializerContext;
