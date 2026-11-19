// SPDX-License-Identifier: AGPL-3.0-only
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using ZedMap.Contracts;

namespace ZedMap.Server;

/// <summary>The stored key: its hash and when it was saved (the single key of F1.38 before per-app keys).</summary>
internal sealed record ApiKeyRecord(string Hash, DateTimeOffset SetAt);

/// <summary>
/// One app's stored key (docs/features/F1.38-gateway-api-key.md, behaviors 1, 6, 9): its hash, never the key. A key the
/// gateway made is 32 random bytes, so a SHA-256 of it can't be reversed or guessed; the one key kept from before
/// per-app keys has the slow password hash it was saved with (<see cref="Legacy"/>).
/// </summary>
internal sealed record StoredApiKey(
    string Id,
    string Name,
    ApiKeyAccess Access,
    string Hash,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt = null,
    string? LastUsedFrom = null,
    bool Legacy = false
);

/// <summary>The keys file.</summary>
internal sealed record StoredApiKeys(IReadOnlyList<StoredApiKey> Keys);

/// <summary>A key a request was let in with: its id (which its connection remembers), name and access.</summary>
internal sealed record AcceptedKey(string Id, string Name, ApiKeyAccess Access);

/// <summary>
/// The gateway's API keys, one per app (docs/features/F1.38-gateway-api-key.md): kept as hashes in
/// <c>api-keys.json</c> in the data folder, readable by the gateway's user only. Revoking one closes its connections
/// and leaves the others working (behavior 1). The single key of an earlier version is kept as "Existing key".
/// </summary>
internal sealed class ApiKeyStore(string folder, TimeProvider time)
{
    /// <summary>The file's name in the data folder.</summary>
    public const string FileName = "api-keys.json";

    /// <summary>The single key's file, from before per-app keys: read once, then renamed.</summary>
    public const string LegacyFileName = "api-key.json";

    /// <summary>The name the earlier version's key is kept under.</summary>
    public const string LegacyName = "Existing key";

    /// <summary>How often a key's last use is written to disk, at most; it's kept in memory meanwhile.</summary>
    internal static readonly TimeSpan LastUsedWrite = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (string KeyId, HubCallerContext Context)> _connections = new(
        StringComparer.Ordinal
    );
    private List<StoredApiKey> _keys = [];
    private bool _loaded;

    private string FilePath => Path.Combine(folder, FileName);

    /// <summary>Whether there's any key, so apps can be let in at all.</summary>
    public bool Any
    {
        get
        {
            lock (_gate)
            {
                Load();
                return _keys.Count > 0;
            }
        }
    }

    /// <summary>The keys as the operator sees them, with how many connections each has other than <paramref name="except"/>.</summary>
    public IReadOnlyList<ApiKeyInfo> List(string? except)
    {
        lock (_gate)
        {
            Load();
            return
            [
                .. _keys.Select(k => new ApiKeyInfo(
                    k.Id,
                    k.Name,
                    k.Access,
                    k.CreatedAt,
                    k.LastUsedAt,
                    k.LastUsedFrom,
                    _connections.Count(c => c.Value.KeyId == k.Id && c.Key != except)
                )),
            ];
        }
    }

    /// <summary>Whether the key with <paramref name="id"/> is still there: a connection let in by it goes on.</summary>
    public bool Has(string id)
    {
        lock (_gate)
        {
            Load();
            return _keys.Any(k => k.Id == id);
        }
    }

    /// <summary>A new random key: 32 bytes as 43 URL-safe characters (behavior 1).</summary>
    public static string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Whether <paramref name="key"/> can be a key: long enough, printable ASCII without spaces.</summary>
    public static bool IsValid(string? key) =>
        key is { Length: >= ApiKeys.MinLength and <= ApiKeys.MaxLength } && key.All(c => c is > ' ' and <= '~');

    /// <summary>A new key for one app, saved as its hash; the key is returned this once (behaviors 1, 6).</summary>
    /// <exception cref="ArgumentException">No name, a name too long or used, or too many keys.</exception>
    public ApiKeyCreated Create(string name, ApiKeyAccess access)
    {
        ArgumentNullException.ThrowIfNull(name);
        name = name.Trim();
        lock (_gate)
        {
            Load();
            if (name.Length == 0 || name.Length > ApiKeys.MaxNameLength || name.Any(char.IsControl))
            {
                throw NameError($"Give the key a name of up to {ApiKeys.MaxNameLength} characters.");
            }
            if (_keys.Any(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw NameError($"There's already a key named {name}.");
            }
            if (_keys.Count >= ApiKeys.MaxKeys)
            {
                throw NameError($"The gateway keeps at most {ApiKeys.MaxKeys} keys. Revoke one first.");
            }
            var key = Generate();
            var stored = new StoredApiKey(Guid.NewGuid().ToString("N"), name, access, FastHash(key), time.GetUtcNow());
            _keys = [.. _keys, stored];
            Write();
            return new ApiKeyCreated(Info(stored), key);
        }
    }

    /// <summary>Revokes one key: its connections close; the others go on. Its name, or null if there was none.</summary>
    public string? Revoke(string id)
    {
        List<HubCallerContext> close;
        string? name;
        lock (_gate)
        {
            Load();
            name = _keys.Find(k => k.Id == id)?.Name;
            if (name is null)
            {
                return null;
            }
            _keys = [.. _keys.Where(k => k.Id != id)];
            Write();
            close = [.. _connections.Where(c => c.Value.KeyId == id).Select(c => c.Value.Context)];
            foreach (var context in close)
            {
                _connections.Remove(context.ConnectionId);
            }
        }
        foreach (var context in close)
        {
            context.Abort(); // the app connects again, as a viewer
        }
        return name;
    }

    /// <summary>
    /// The key <paramref name="key"/> is, or null; records its use from <paramref name="address"/> (behavior 9). A
    /// wrong key costs the same whichever keys there are, so which exist can't be timed.
    /// </summary>
    public AcceptedKey? Check(string key, string? address = null)
    {
        var hash = FastHash(key);
        lock (_gate)
        {
            Load();
            StoredApiKey? found = null;
            foreach (var stored in _keys)
            {
                var match = stored.Legacy
                    ? PasswordHashing.Verify(key, stored.Hash)
                    : CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(hash),
                        Encoding.ASCII.GetBytes(stored.Hash)
                    );
                if (match && found is null)
                {
                    found = stored;
                }
            }
            if (found is null)
            {
                return null;
            }
            if (address is not null)
            {
                Used(found, address);
            }
            return new AcceptedKey(found.Id, found.Name, found.Access);
        }
    }

    // Under _gate. Written at most every few minutes for the same address, so a busy app doesn't wear the disk.
    private void Used(StoredApiKey stored, string address)
    {
        var now = time.GetUtcNow();
        var write = stored.LastUsedFrom != address || stored.LastUsedAt is not { } last || now - last >= LastUsedWrite;
        var used = stored with { LastUsedAt = now, LastUsedFrom = address };
        _keys = [.. _keys.Select(k => k.Id == stored.Id ? used : k)];
        if (write)
        {
            Write();
        }
    }

    /// <summary>A hub connection let in by key <paramref name="keyId"/>: it closes when that key is revoked.</summary>
    public void Attach(HubCallerContext context, string keyId)
    {
        lock (_gate)
        {
            _connections[context.ConnectionId] = (keyId, context);
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

    private static ApiKeyInfo Info(StoredApiKey k) =>
        new(k.Id, k.Name, k.Access, k.CreatedAt, k.LastUsedAt, k.LastUsedFrom);

    private static Exception NameError(string message) => EngineErrors.Create(EngineErrorCodes.ApiKeyName, message);

    private static string FastHash(string key) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    // Under _gate: the keys file, or the earlier version's single key moved into it once.
    private void Load()
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        try
        {
            if (File.Exists(FilePath))
            {
                _keys =
                [
                    .. JsonSerializer.Deserialize(File.ReadAllText(FilePath), ApiKeyJson.Default.StoredApiKeys)?.Keys
                        ?? [],
                ];
                return;
            }
            var legacy = Path.Combine(folder, LegacyFileName);
            if (
                File.Exists(legacy)
                && JsonSerializer.Deserialize(File.ReadAllText(legacy), ApiKeyJson.Default.ApiKeyRecord) is { } record
            )
            {
                // Kept, so the apps using it keep working until they've moved to keys of their own (behavior 10).
                _keys =
                [
                    new StoredApiKey(
                        "existing",
                        LegacyName,
                        ApiKeyAccess.Operator,
                        record.Hash,
                        record.SetAt,
                        Legacy: true
                    ),
                ];
                Write();
                File.Move(legacy, legacy + ".moved", overwrite: true);
            }
        }
        catch (JsonException)
        {
            _keys = []; // unreadable: no API access until a key is created again
        }
    }

    // Under _gate.
    private void Write()
    {
        Directory.CreateDirectory(folder);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(new StoredApiKeys(_keys), ApiKeyJson.Default.StoredApiKeys)
        );
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(temporary, FilePath, overwrite: true);
    }
}

/// <summary>What a request's <c>Authorization: Bearer</c> key comes to (F1.38 behaviors 4, 5, 7).</summary>
internal enum ApiKeyOutcome
{
    /// <summary>No key sent, or not where keys count (the viewer port): as without one.</summary>
    None,

    /// <summary>One of the keys: the request is that app, with what its key allows.</summary>
    Accepted,

    /// <summary>A key that isn't the saved one, or over plain HTTP from another machine.</summary>
    Refused,

    /// <summary>Not checked: the address is waiting after too many wrong keys or passwords (#450).</summary>
    Waiting,
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
    /// <summary>The name a connection let in by a key has, where operators are named (behavior 8): "API key: laptop".</summary>
    public static string OperatorName(string keyName) => "API key: " + keyName;

    /// <summary>The key in <paramref name="context"/>'s <c>Authorization</c> header, if it sends one as a bearer.</summary>
    public static string? KeyIn(HttpContext context) =>
        AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var header)
        && string.Equals(header.Scheme, ApiKeys.Scheme, StringComparison.OrdinalIgnoreCase)
        && header.Parameter is { Length: > 0 } key
            ? key
            : null;

    /// <summary>
    /// What <paramref name="context"/>'s key comes to, with the key when it's accepted and how long the address must
    /// wait when it's waiting.
    /// </summary>
    public (ApiKeyOutcome Outcome, AcceptedKey? Key, TimeSpan? Wait) Check(HttpContext context)
    {
        if (KeyIn(context) is not { } key || !OperatorSignIn.IsOperatorPort(context, options.CurrentValue))
        {
            return (ApiKeyOutcome.None, null, null); // the viewer port ignores it (behavior 4)
        }
        var address = Viewers.AddressOf(context);
        if (!OperatorSignIn.IsSecureEnough(context, options.CurrentValue))
        {
            audit.ApiKeyRefused(address, "plain HTTP from another machine");
            return (ApiKeyOutcome.Refused, null, null);
        }
        if (limiter.Wait(address) is { } wait)
        {
            audit.ApiKeyRefused(address, "too many tries");
            return (ApiKeyOutcome.Waiting, null, wait);
        }
        if (store.Check(key, address) is { } accepted)
        {
            audit.ApiKeyAccepted(address, accepted.Name);
            return (ApiKeyOutcome.Accepted, accepted, null);
        }
        limiter.Failed(address, null, Fingerprint(key));
        audit.ApiKeyRefused(address, "wrong key");
        return (ApiKeyOutcome.Refused, null, null);
    }

    /// <summary>Whole minutes left of <paramref name="wait"/>, as a refusal states it.</summary>
    public static int Minutes(TimeSpan wait) => (int)Math.Ceiling(wait.TotalMinutes);

    // Only to tell a key seen before from a new one, in memory; never stored or logged.
    private static string Fingerprint(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ApiKeyRecord))]
[JsonSerializable(typeof(StoredApiKeys))]
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage] // generated code
internal sealed partial class ApiKeyJson : JsonSerializerContext;
