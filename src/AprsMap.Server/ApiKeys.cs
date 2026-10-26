// SPDX-License-Identifier: AGPL-3.0-only
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AprsMap.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace AprsMap.Server;

/// <summary>The stored key: its hash and when it was saved (docs/features/F1.38-gateway-api-key.md, behavior 6).</summary>
internal sealed record ApiKeyRecord(string Hash, DateTimeOffset SetAt);

/// <summary>
/// The gateway's API key (docs/features/F1.38-gateway-api-key.md): kept as a hash in <c>api-key.json</c> in the data
/// folder, readable by the gateway's user only, never the key itself. Connections let in by it are closed when it's
/// replaced or removed (behavior 1).
/// </summary>
internal sealed class ApiKeyStore(string folder, TimeProvider time)
{
    /// <summary>The file's name in the data folder.</summary>
    public const string FileName = "api-key.json";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, HubCallerContext> _connections = new(StringComparer.Ordinal);
    private ApiKeyRecord? _record;
    private bool _loaded;

    private string FilePath => Path.Combine(folder, FileName);

    /// <summary>Whether a key is saved, and when.</summary>
    public ApiKeyStatus Status => Current is { } record ? new ApiKeyStatus(true, record.SetAt) : ApiKeyStatus.None;

    /// <summary>The saved key's fingerprint, which connections let in by it remember; null with none.</summary>
    public string? Stamp => Current is { } record ? PasswordHashing.Stamp(record.Hash) : null;

    private ApiKeyRecord? Current
    {
        get
        {
            lock (_gate)
            {
                if (!_loaded)
                {
                    _record = Load(FilePath);
                    _loaded = true;
                }
                return _record;
            }
        }
    }

    /// <summary>A new random key: 32 bytes as 43 URL-safe characters (behavior 1).</summary>
    public static string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Whether <paramref name="key"/> can be a key: long enough, printable ASCII without spaces.</summary>
    public static bool IsValid(string? key) =>
        key is { Length: >= ApiKeys.MinLength and <= ApiKeys.MaxLength } && key.All(c => c is > ' ' and <= '~');

    /// <summary>Saves <paramref name="key"/>'s hash, replacing the old key and closing its connections.</summary>
    /// <exception cref="ArgumentException">It can't be a key (<see cref="EngineErrorCodes.ApiKeyTooShort"/>).</exception>
    public ApiKeyStatus Save(string key)
    {
        if (!IsValid(key))
        {
            throw EngineErrors.Create(
                EngineErrorCodes.ApiKeyTooShort,
                $"An API key needs at least {ApiKeys.MinLength} characters, printable ASCII without spaces."
            );
        }
        var record = new ApiKeyRecord(PasswordHashing.Hash(key), time.GetUtcNow());
        Directory.CreateDirectory(folder);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, ApiKeyJson.Default.ApiKeyRecord));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(temporary, FilePath, overwrite: true);
        Replace(record);
        return Status;
    }

    /// <summary>Removes the key: API access is off, and its connections close.</summary>
    public void Remove()
    {
        File.Delete(FilePath);
        Replace(null);
    }

    private void Replace(ApiKeyRecord? record)
    {
        List<HubCallerContext> close;
        lock (_gate)
        {
            _record = record;
            _loaded = true;
            close = [.. _connections.Values];
            _connections.Clear();
        }
        foreach (var context in close)
        {
            context.Abort(); // the app connects again, as a viewer unless it has the new key
        }
    }

    /// <summary>
    /// The saved key's <see cref="Stamp"/> when <paramref name="key"/> is it, otherwise null; costs the same with no key
    /// saved, so whether there is one can't be timed.
    /// </summary>
    public string? Check(string key)
    {
        var record = Current;
        return PasswordHashing.Verify(key, record?.Hash) && record is not null
            ? PasswordHashing.Stamp(record.Hash)
            : null;
    }

    /// <summary>A hub connection let in by the key: it closes when the key changes.</summary>
    public void Attach(HubCallerContext context)
    {
        lock (_gate)
        {
            _connections[context.ConnectionId] = context;
        }
    }

    /// <summary>A hub connection closed.</summary>
    public void Detach(string connectionId)
    {
        lock (_gate)
        {
            _connections.Remove(connectionId);
        }
    }

    private static ApiKeyRecord? Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), ApiKeyJson.Default.ApiKeyRecord)
                : null;
        }
        catch (JsonException)
        {
            return null; // unreadable: no API access until a key is saved again
        }
    }
}

/// <summary>What a request's <c>Authorization: Bearer</c> key comes to (F1.38 behaviors 4, 5, 7).</summary>
internal enum ApiKeyOutcome
{
    /// <summary>No key sent, or not where keys count (the viewer port): as without one.</summary>
    None,

    /// <summary>The saved key: the request is the operator "API key".</summary>
    Accepted,

    /// <summary>A key that isn't the saved one, over plain HTTP from another machine, or while waiting after guesses.</summary>
    Refused,
}

/// <summary>
/// Checks the key a request sends (docs/features/F1.38-gateway-api-key.md, behaviors 4, 5, 7): only on the operator
/// port, over HTTPS or from the gateway's own host; a wrong key counts as a failed sign-in from that address, and each
/// key accepted or refused is logged, never the key.
/// </summary>
internal sealed class ApiKeyGate(
    ApiKeyStore store,
    SignInLimiter limiter,
    SignInAudit audit,
    IOptionsMonitor<GatewayOptions> options
)
{
    /// <summary>The name a connection let in by the key has, where operators are named (behavior 8).</summary>
    public const string OperatorName = "API key";

    /// <summary>The key in <paramref name="context"/>'s <c>Authorization</c> header, if it sends one as a bearer.</summary>
    public static string? KeyIn(HttpContext context) =>
        AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var header)
        && string.Equals(header.Scheme, ApiKeys.Scheme, StringComparison.OrdinalIgnoreCase)
        && header.Parameter is { Length: > 0 } key
            ? key
            : null;

    /// <summary>What <paramref name="context"/>'s key comes to, with the key's stamp when it's accepted.</summary>
    public (ApiKeyOutcome Outcome, string? Stamp) Check(HttpContext context)
    {
        if (KeyIn(context) is not { } key || !OperatorSignIn.IsOperatorPort(context, options.CurrentValue))
        {
            return (ApiKeyOutcome.None, null); // the viewer port ignores it (behavior 4)
        }
        var address = Viewers.AddressOf(context);
        if (!OperatorSignIn.IsSecureEnough(context))
        {
            audit.ApiKeyRefused(address, "plain HTTP from another machine");
            return (ApiKeyOutcome.Refused, null);
        }
        if (limiter.Wait(address) is not null)
        {
            audit.ApiKeyRefused(address, "too many tries");
            return (ApiKeyOutcome.Refused, null);
        }
        if (store.Check(key) is { } stamp)
        {
            audit.ApiKeyAccepted(address);
            return (ApiKeyOutcome.Accepted, stamp);
        }
        limiter.Failed(address, null);
        audit.ApiKeyRefused(address, "wrong key");
        return (ApiKeyOutcome.Refused, null);
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(ApiKeyRecord))]
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage] // generated code
internal sealed partial class ApiKeyJson : JsonSerializerContext;
