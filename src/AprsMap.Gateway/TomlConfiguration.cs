// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace AprsMap.Gateway;

/// <summary>
/// The gateway's TOML file (docs/features/F1.37-gateway-service.md, behavior 1) as configuration: each table is a
/// section and each key a setting, with <c>snake_case</c> names read as the settings' own (<c>operator_listen</c> is
/// <c>OperatorListen</c>), so <c>[gateway] operator_listen</c> is <c>Gateway:OperatorListen</c>. Arrays and arrays of
/// tables are numbered from 0, as configuration numbers them.
/// </summary>
internal static class TomlConfiguration
{
    /// <summary>The environment variable that names the file, when <c>--config</c> doesn't.</summary>
    public const string EnvironmentVariable = "APRSMAP_CONFIG";

    /// <summary>
    /// The file to read: <c>--config PATH</c> (or <c>--config=PATH</c>) in <paramref name="args"/>, else
    /// <see cref="EnvironmentVariable"/>; null for none.
    /// </summary>
    public static string? PathFrom(string[] args, Func<string, string?> environment)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config" && i + 1 < args.Length)
            {
                return args[i + 1];
            }
            if (args[i].StartsWith("--config=", StringComparison.Ordinal))
            {
                return args[i]["--config=".Length..];
            }
        }
        return environment(EnvironmentVariable) is { Length: > 0 } path ? path : null;
    }

    /// <summary><paramref name="args"/> without <c>--config PATH</c> or <c>--config=PATH</c>.</summary>
    public static string[] WithoutConfig(string[] args)
    {
        var rest = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config")
            {
                i++; // and its path
                continue;
            }
            if (!args[i].StartsWith("--config=", StringComparison.Ordinal))
            {
                rest.Add(args[i]);
            }
        }
        return [.. rest];
    }

    /// <summary>
    /// The settings in <paramref name="text"/>; throws <see cref="InvalidDataException"/> with the line and the reason
    /// when it isn't TOML, so the gateway stops at start rather than running on defaults.
    /// </summary>
    public static Dictionary<string, string?> Read(string text, string path)
    {
        TomlTable document;
        try
        {
            document = TomlSerializer.Deserialize<TomlTable>(text)!;
        }
        catch (TomlException ex)
        {
            throw new InvalidDataException($"{path} isn't valid TOML: {ex.Message}", ex);
        }
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Add(settings, null, document);
        return settings;
    }

    private static void Add(Dictionary<string, string?> settings, string? prefix, object? value)
    {
        switch (value)
        {
            case TomlTable table:
                foreach (var (key, item) in table)
                {
                    Add(settings, Join(prefix, Name(key)), item);
                }
                break;
            case TomlTableArray tables:
                for (var i = 0; i < tables.Count; i++)
                {
                    Add(settings, Join(prefix, i.ToString(CultureInfo.InvariantCulture)), tables[i]);
                }
                break;
            case TomlArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    Add(settings, Join(prefix, i.ToString(CultureInfo.InvariantCulture)), array[i]);
                }
                break;
            case null:
                break;
            default:
                settings[prefix!] = value switch
                {
                    bool b => b ? "true" : "false",
                    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                    _ => value.ToString(),
                };
                break;
        }
    }

    private static string Join(string? prefix, string key) => prefix is null ? key : $"{prefix}:{key}";

    /// <summary><c>operator_listen</c> as <c>OperatorListen</c>; a name without underscores as it is.</summary>
    internal static string Name(string key)
    {
        if (!key.Contains('_', StringComparison.Ordinal))
        {
            return key;
        }
        var name = new StringBuilder(key.Length);
        var upper = true;
        foreach (var c in key)
        {
            if (c == '_')
            {
                upper = true;
                continue;
            }
            name.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        return name.ToString();
    }

    /// <summary>
    /// Puts <paramref name="settings"/> into <paramref name="sources"/> after the last JSON file
    /// (<c>appsettings.json</c> and its environment's), so they override the files while the environment variables and
    /// command line after them still override them (behavior 1).
    /// </summary>
    public static void InsertAfterJsonFiles(IList<IConfigurationSource> sources, Dictionary<string, string?> settings)
    {
        var at = 0;
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is Microsoft.Extensions.Configuration.Json.JsonConfigurationSource)
            {
                at = i + 1;
            }
        }
        sources.Insert(
            at,
            new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource { InitialData = settings }
        );
    }

    /// <summary>Adds the file at <paramref name="path"/> to <paramref name="configuration"/>; it must exist.</summary>
    public static IConfigurationBuilder AddToml(this IConfigurationBuilder configuration, string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The configuration file {path} doesn't exist.", path);
        }
        return configuration.AddInMemoryCollection(Read(File.ReadAllText(path), path));
    }
}
