// SPDX-License-Identifier: AGPL-3.0-only
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace AprsMap.Server;

/// <summary>An operator who may sign in (docs/features/F2.9-operator-sign-in.md, behavior 1): a name and a password hash.</summary>
internal sealed record OperatorRecord(string Name, string PasswordHash);

/// <summary>An operator set up in configuration: <c>Gateway__Operators__0__Name</c> and <c>…__PasswordHash</c>.</summary>
internal sealed class OperatorOptions
{
    /// <summary>The name to sign in with.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The hash <c>operator add</c> prints; never a plain password.</summary>
    public string PasswordHash { get; set; } = string.Empty;
}

/// <summary>
/// Passwords as salted PBKDF2-SHA256 hashes (behavior 2, decision 2), written <c>pbkdf2-sha256$iterations$salt$hash</c>,
/// compared in constant time. A password is never stored, logged or kept after it's checked.
/// </summary>
internal static class PasswordHashing
{
    /// <summary>The shortest password an operator may have.</summary>
    public const int MinLength = 12;

    /// <summary>PBKDF2-HMAC-SHA256 iterations for a new hash, as OWASP recommends.</summary>
    public const int Iterations = 600_000;

    private const string Scheme = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>Checked against when the name is unknown, so an unknown name costs the same as a wrong password.</summary>
    private static readonly Lazy<string> Decoy = new(() =>
        Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)))
    );

    /// <summary>A new hash of <paramref name="password"/> with a random salt.</summary>
    public static string Hash(string password, int iterations = Iterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Join(
            '$',
            Scheme,
            iterations.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash)
        );
    }

    /// <summary>Whether <paramref name="password"/> matches <paramref name="stored"/>; with no hash, checks a decoy and says no.</summary>
    public static bool Verify(string password, string? stored)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (stored is null || !TryParse(stored, out var iterations, out var salt, out var expected))
        {
            _ = TryParse(Decoy.Value, out iterations, out salt, out expected);
            Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
            return false;
        }
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Whether <paramref name="stored"/> is a hash this gateway can check.</summary>
    public static bool IsHash(string stored) => TryParse(stored, out _, out _, out _);

    /// <summary>
    /// A short fingerprint of a hash: sessions remember it, so changing or removing a password ends them (behavior 3).
    /// </summary>
    public static string Stamp(string hash) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hash)))[..16];

    private static bool TryParse(string stored, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = hash = [];
        var parts = stored.Split('$');
        if (
            parts is not [Scheme, var count, var saltText, var hashText]
            || !int.TryParse(count, System.Globalization.CultureInfo.InvariantCulture, out iterations)
            || iterations < 1
        )
        {
            return false;
        }
        try
        {
            salt = Convert.FromBase64String(saltText);
            hash = Convert.FromBase64String(hashText);
        }
        catch (FormatException)
        {
            return false;
        }
        return salt.Length > 0 && hash.Length > 0;
    }
}

/// <summary>
/// The operators (behavior 1): those in <c>operators.json</c> in the data folder, which <c>operator add</c> writes and the
/// gateway reads again when it changes, and those in configuration. Names compare without case.
/// </summary>
internal sealed class OperatorStore
{
    /// <summary>The file's name in the data folder.</summary>
    public const string FileName = "operators.json";

    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly Lock _gate = new();
    private DateTime _readAt = DateTime.MinValue;
    private List<OperatorRecord> _fromFile = [];

    public OperatorStore(string folder, IOptionsMonitor<GatewayOptions> options)
    {
        FilePath = Path.Combine(folder, FileName);
        _options = options;
    }

    /// <summary>Where the operators set up on the host are kept.</summary>
    public string FilePath { get; }

    /// <summary>Every operator now, the file's first.</summary>
    public IReadOnlyList<OperatorRecord> All
    {
        get
        {
            var fromConfig = _options
                .CurrentValue.Operators.Where(o =>
                    !string.IsNullOrWhiteSpace(o.Name) && PasswordHashing.IsHash(o.PasswordHash)
                )
                .Select(o => new OperatorRecord(o.Name.Trim(), o.PasswordHash));
            lock (_gate)
            {
                Refresh();
                return [.. _fromFile, .. fromConfig.Where(c => !_fromFile.Any(f => Same(f.Name, c.Name)))];
            }
        }
    }

    /// <summary>Whether any operator is set up, so sign-in decides the role (behavior 17).</summary>
    public bool Any => All.Count > 0;

    /// <summary>The operator called <paramref name="name"/>, or null.</summary>
    public OperatorRecord? Find(string name) => All.FirstOrDefault(o => Same(o.Name, name.Trim()));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the file again when it changed: <c>operator add</c> runs as another process.</summary>
    private void Refresh()
    {
        var written = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
        if (written == _readAt)
        {
            return;
        }
        _fromFile = Load(FilePath);
        _readAt = written;
    }

    /// <summary>The operators in <paramref name="path"/>; none when it's missing or can't be read.</summary>
    public static List<OperatorRecord> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ?
                [
                    .. (
                        JsonSerializer.Deserialize(File.ReadAllText(path), OperatorJson.Default.ListOperatorRecord)
                        ?? []
                    ).Where(o => !string.IsNullOrWhiteSpace(o.Name) && PasswordHashing.IsHash(o.PasswordHash)),
                ]
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Writes <paramref name="operators"/> to <paramref name="path"/> at once, readable by its owner only.</summary>
    public static void Save(string path, List<OperatorRecord> operators)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(operators, OperatorJson.Default.ListOperatorRecord));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(temporary, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(List<OperatorRecord>))]
internal sealed partial class OperatorJson : JsonSerializerContext;

/// <summary>
/// <c>aprsmap-gateway operator add|password|remove|list NAME</c> (behavior 1): sets operators up on the host. A password
/// is asked for twice without echo and only its hash is stored.
/// </summary>
internal static class OperatorCommand
{
    /// <summary>Runs the command in <paramref name="args"/> (after <c>operator</c>); the process's exit code.</summary>
    public static int Run(
        IReadOnlyList<string> args,
        string folder,
        Func<string, string?> askPassword,
        TextWriter output,
        int iterations = PasswordHashing.Iterations
    )
    {
        var path = Path.Combine(folder, OperatorStore.FileName);
        var operators = OperatorStore.Load(path);
        switch (args)
        {
            case ["list"]:
                foreach (var op in operators)
                {
                    output.WriteLine(op.Name);
                }
                if (operators.Count == 0)
                {
                    output.WriteLine("No operators are set up.");
                }
                return 0;
            case ["add", var name] when Find(operators, name) is not null:
                output.WriteLine($"There's already an operator called {name}; use 'operator password {name}'.");
                return 1;
            case ["password" or "remove", var name] when Find(operators, name) is null:
                output.WriteLine($"There's no operator called {name}.");
                return 1;
            case ["add" or "password", var name]:
                if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsWhiteSpace) || name.Length > 64)
                {
                    output.WriteLine("A name has 1 to 64 characters and no spaces.");
                    return 1;
                }
                var password = askPassword("Password: ");
                if (password is null || password.Length < PasswordHashing.MinLength)
                {
                    output.WriteLine($"A password has at least {PasswordHashing.MinLength} characters.");
                    return 1;
                }
                if (askPassword("Again: ") != password)
                {
                    output.WriteLine("The passwords don't match.");
                    return 1;
                }
                operators.RemoveAll(o => Same(o.Name, name));
                operators.Add(new OperatorRecord(name, PasswordHashing.Hash(password, iterations)));
                OperatorStore.Save(path, operators);
                output.WriteLine(
                    args[0] == "add"
                        ? $"Added {name}. They can sign in now."
                        : $"Changed {name}'s password; their sessions have ended."
                );
                return 0;
            case ["remove", var name]:
                operators.RemoveAll(o => Same(o.Name, name));
                OperatorStore.Save(path, operators);
                output.WriteLine($"Removed {name}; their sessions have ended.");
                return 0;
            default:
                output.WriteLine(
                    "Usage: operator add NAME | operator password NAME | operator remove NAME | operator list"
                );
                return 2;
        }
    }

    private static OperatorRecord? Find(List<OperatorRecord> operators, string name) =>
        operators.FirstOrDefault(o => Same(o.Name, name));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a password from the console without showing it, or a line when input is redirected.</summary>
    public static string? AskPassword(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine();
        }
        var typed = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return typed.ToString();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0)
                {
                    typed.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
            }
        }
    }
}

/// <summary>The gateway's data folder: the engine's settings folder (<c>/data</c> in the image).</summary>
internal static class GatewayData
{
    public static string Folder(IConfiguration configuration) =>
        Core.SettingsLocation.Folder(
            configuration.GetSection("Engine").Get<Core.EngineOptions>() ?? new Core.EngineOptions()
        );
}
