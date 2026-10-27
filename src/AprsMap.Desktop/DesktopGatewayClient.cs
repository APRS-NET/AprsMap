// SPDX-License-Identifier: AGPL-3.0-only
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AprsMap.UI.Platform;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace AprsMap.Desktop;

/// <summary>
/// The desktop's choice of station (docs/features/F1.39-desktop-gateway-client.md, behaviors 1, 7): its own engine or a
/// gateway's, kept in <c>gateway-client.json</c> in its settings folder, readable by this user only as it holds the
/// API key. <c>--gateway URL</c> shows that gateway for one run, with the saved key.
/// </summary>
internal sealed class DesktopGatewayClient : IGatewayClientHost
{
    /// <summary>The file's name in the settings folder.</summary>
    public const string FileName = "gateway-client.json";

    /// <summary>The option that shows a gateway for one run.</summary>
    public const string Option = "--gateway";

    private readonly string _path;
    private readonly string[] _args;
    private bool _restart;

    private DesktopGatewayClient(string folder, string[] args, GatewayClientSettings settings, Uri? connected)
    {
        _path = Path.Combine(folder, FileName);
        _args = args;
        Settings = settings;
        Connected = connected;
    }

    /// <summary>
    /// The choice saved in <paramref name="folder"/>, and the gateway this run shows: <c>--gateway URL</c> in
    /// <paramref name="args"/>, else the saved one.
    /// </summary>
    public static DesktopGatewayClient Load(string folder, string[] args)
    {
        var settings = Read(Path.Combine(folder, FileName));
        var connected = GatewayIn(args) ?? settings.Gateway;
        return new DesktopGatewayClient(folder, args, settings, connected);
    }

    /// <inheritdoc />
    public GatewayClientSettings Settings { get; private set; }

    /// <inheritdoc />
    public Uri? Connected { get; }

    /// <summary>The key this run sends: the saved one.</summary>
    public string? ApiKey => Settings.ApiKey;

    /// <inheritdoc />
    public async Task SaveAsync(GatewayClientSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        var file = new GatewayClientFile(settings.Gateway?.ToString(), settings.ApiKey);
        await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(file, GatewayClientJson.Default.GatewayClientFile),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite); // behavior 7
        }
        File.Move(temporary, _path, overwrite: true);
        Settings = settings;
    }

    /// <inheritdoc />
    public Task<ConnectionTestOutcome> TestAsync(
        Uri gateway,
        string? apiKey,
        CancellationToken cancellationToken = default
    ) => GatewayConnectionTest.RunAsync(gateway, apiKey, cancellationToken: cancellationToken);

    /// <inheritdoc />
    public bool Restart()
    {
        if (Executable() is null)
        {
            return false;
        }
        _restart = true;
        // The usual close, which saves the layout and preferences; Finish then opens the app again.
        Dispatcher.UIThread.Post(() =>
            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown()
        );
        return true;
    }

    /// <summary>Called as the app exits: after Apply and restart, opens it again without a one-run gateway.</summary>
    public void Finish()
    {
        if (_restart && Executable() is { } executable)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            foreach (var arg in WithoutGateway(_args))
            {
                start.ArgumentList.Add(arg);
            }
            Process.Start(start)?.Dispose();
        }
    }

    /// <summary>The program to start again: the AppImage itself when run from one, rather than its mounted copy.</summary>
    private static string? Executable() =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage && File.Exists(appImage)
            ? appImage
            : Environment.ProcessPath;

    /// <summary>The gateway in <c>--gateway URL</c> or <c>--gateway=URL</c>; null when there's none or it isn't one.</summary>
    internal static Uri? GatewayIn(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var value =
                args[i] == Option && i + 1 < args.Length ? args[i + 1]
                : args[i].StartsWith(Option + "=", StringComparison.Ordinal) ? args[i][(Option.Length + 1)..]
                : null;
            if (
                value is not null
                && Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https"
            )
            {
                return uri;
            }
        }
        return null;
    }

    /// <summary><paramref name="args"/> without <c>--gateway</c> and the start-over options, for the next start.</summary>
    internal static IEnumerable<string> WithoutGateway(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == Option)
            {
                i++;
            }
            else if (
                !args[i].StartsWith(Option + "=", StringComparison.Ordinal)
                && !args[i].StartsWith("--reset-", StringComparison.OrdinalIgnoreCase)
            )
            {
                yield return args[i];
            }
        }
    }

    internal static GatewayClientSettings Read(string path)
    {
        try
        {
            if (
                File.Exists(path)
                && JsonSerializer.Deserialize(File.ReadAllText(path), GatewayClientJson.Default.GatewayClientFile)
                    is { } file
            )
            {
                return new GatewayClientSettings(
                    Uri.TryCreate(file.Gateway, UriKind.Absolute, out var gateway) ? gateway : null,
                    string.IsNullOrWhiteSpace(file.ApiKey) ? null : file.ApiKey
                );
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable: this computer's own engine, as on a first start.
        }
        return new GatewayClientSettings();
    }
}

/// <summary><c>gateway-client.json</c>: the gateway's address (none for this computer) and its API key.</summary>
internal sealed record GatewayClientFile(string? Gateway, string? ApiKey);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(GatewayClientFile))]
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage] // generated code
internal sealed partial class GatewayClientJson : JsonSerializerContext;
