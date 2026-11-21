// SPDX-License-Identifier: AGPL-3.0-only
using System.Diagnostics;
using ZedMap.Contracts;

namespace ZedMap.Gateway.Tests;

/// <summary>
/// The gateway's installer, <c>deploy/linux/install.sh</c>, moving a gateway installed under an earlier name over
/// (<c>--migrate-from</c>; docs/features/F1.40-gateway-service-updates.md, behavior 14): run against a folder standing in
/// for <c>/</c>, with stand-ins for <c>systemctl</c>, <c>id</c>, <c>useradd</c>, <c>usermod</c>, <c>chown</c> and
/// <c>install</c>'s owner flags. Linux only, as the service is.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class InstallScriptTests : IDisposable
{
    private const string Old = "oldmap";
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("aprsmap-install-");

    private string Root => _root.FullName;

    private string Fs => Path.Combine(Root, "fs");

    private static string Slug => Brand.Slug;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Deploy
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var deploy = Path.Combine(dir.FullName, "deploy", "linux");
                if (Directory.Exists(deploy))
                {
                    return deploy;
                }
            }
            throw new DirectoryNotFoundException("deploy/linux isn't above the test's folder.");
        }
    }

    public InstallScriptTests()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The service and its installer are Linux only.");
        foreach (var dir in new[] { "bin", "state", "users" })
        {
            Directory.CreateDirectory(Path.Combine(Root, dir));
        }
        // systemctl: logs each call; a service starts "active" unless a crash-<service> file says it doesn't stay up.
        Shim(
            "systemctl",
            $$"""
            echo "$*" >> "{{Root}}/systemctl.log"
            svc="${@: -1}"; svc="${svc%.service}"
            case "$1" in
              is-active) cat "{{Root}}/state/$svc" 2>/dev/null || echo inactive ;;
              show) echo 0 ;;
              start) if [ -f "{{Root}}/crash-$svc" ]; then echo failed > "{{Root}}/state/$svc"; else echo active > "{{Root}}/state/$svc"; fi ;;
              stop) echo inactive > "{{Root}}/state/$svc" ;;
            esac
            exit 0
            """
        );
        Shim(
            "id",
            $$"""
            if [ "$1" = -u ]; then echo 0; exit 0; fi
            [ -f "{{Root}}/users/$1" ]
            """
        );
        Shim("useradd", $$"""touch "{{Root}}/users/${@: -1}" """);
        Shim("usermod", "exit 0");
        Shim("chown", "exit 0");
        Shim("hostname", "echo test");
        // install: as itself, without the owner flags only root may use.
        Shim(
            "install",
            """
            args=()
            while [ $# -gt 0 ]; do case "$1" in -o|-g) shift 2 ;; *) args+=("$1"); shift ;; esac; done
            exec /usr/bin/install "${args[@]}"
            """
        );
    }

    public void Dispose() => _root.Delete(recursive: true);

    private void Shim(string name, string body)
    {
        var path = Path.Combine(Root, "bin", name);
        File.WriteAllText(path, "#!/usr/bin/env bash\n" + body.Replace("\r", "", StringComparison.Ordinal) + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private string At(string path) => Path.Combine(Fs, path.TrimStart('/'));

    private void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(At(path))!);
        File.WriteAllText(At(path), text);
    }

    /// <summary>A gateway installed as <see cref="Old"/>: its folders, configuration, data and units.</summary>
    private void OldInstall()
    {
        Write($"/opt/{Old}-gateway/{Old}-gateway", "#!/bin/sh\n");
        Write($"/opt/{Old}-gateway/version", "v0.9.0\n");
        Write(
            $"/etc/{Old}/gateway.toml",
            $"""
            [engine]
            settings_folder = "/var/lib/{Old}"
            [tiles]
            cache_folder = "/var/lib/{Old}/tiles"
            certificate = "/etc/{Old}/cert.pem"
            notes = "/var/lib/{Old}more is not a folder of it"
            """
        );
        Write($"/var/lib/{Old}/settings.json", """{"station":"NZ0P-4"}""");
        Write($"/var/lib/{Old}/update-status.json", """{"version":"0.10.0","step":"installing","reason":""}""");
        Write($"/var/lib/{Old}/history/2026-11-20.db", "packets");
        foreach (var unit in new[] { "gateway.service", "gateway-update.path", "gateway-update.service" })
        {
            Write($"/etc/systemd/system/{Old}-{unit}", "[Unit]\n");
        }
        Write($"/usr/local/libexec/{Old}-gateway-update", "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(Root, "users", Old), "");
        File.WriteAllText(Path.Combine(Root, "state", $"{Old}-gateway"), "active");
    }

    /// <summary>This name's release tarball, as the release workflow builds it.</summary>
    private async Task<string> TarballAsync(string version)
    {
        var build = Path.Combine(Root, "build");
        Directory.CreateDirectory(build);
        foreach (var file in Directory.GetFiles(Deploy))
        {
            File.Copy(file, Path.Combine(build, Path.GetFileName(file)), overwrite: true);
        }
        await File.WriteAllTextAsync(Path.Combine(build, $"{Slug}-gateway"), "#!/bin/sh\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(build, "version"), "v" + version + "\n", Ct);
        var tarball = Path.Combine(Root, $"{Slug}-gateway-v{version}-linux-x64.tar.gz");
        await RunAsync("tar", "-C", build, "-czf", tarball, ".");
        return tarball;
    }

    private async Task<(int Exit, string Output)> InstallAsync(params string[] args) =>
        await RunAsync("bash", [Path.Combine(Deploy, "install.sh"), .. args]);

    private async Task<(int Exit, string Output)> RunAsync(string program, params string[] args)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        start.Environment["PATH"] = Path.Combine(Root, "bin") + ":" + Environment.GetEnvironmentVariable("PATH");
        start.Environment[$"{Slug.ToUpperInvariant()}_ROOT"] = Fs;
        start.Environment[$"{Slug.ToUpperInvariant()}_SETTLE_SECONDS"] = "0";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var errors = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        return (process.ExitCode, await output + await errors);
    }

    private string[] Systemctl => File.ReadAllLines(Path.Combine(Root, "systemctl.log"));

    [Fact]
    public async Task AGatewayUnderAnOldNameIsCopiedOverStartedAndTheOldUnitsGo()
    {
        OldInstall();
        var tarball = await TarballAsync("0.10.0");

        var (exit, output) = await InstallAsync("--migrate-from", Old, tarball);

        Assert.True(exit == 0, output);
        // The configuration and data, copied, with the old folders in the file renamed; a lookalike path left alone.
        var toml = File.ReadAllText(At($"/etc/{Slug}/gateway.toml"));
        Assert.Contains($"settings_folder = \"/var/lib/{Slug}\"", toml, StringComparison.Ordinal);
        Assert.Contains($"cache_folder = \"/var/lib/{Slug}/tiles\"", toml, StringComparison.Ordinal);
        Assert.Contains($"certificate = \"/etc/{Slug}/cert.pem\"", toml, StringComparison.Ordinal);
        Assert.Contains($"/var/lib/{Old}more is not", toml, StringComparison.Ordinal);
        Assert.Equal("""{"station":"NZ0P-4"}""", File.ReadAllText(At($"/var/lib/{Slug}/settings.json")));
        Assert.True(File.Exists(At($"/var/lib/{Slug}/history/2026-11-20.db")));
        // This one installed and running; the notice says it's done, in its own data folder.
        Assert.Equal("v0.10.0", File.ReadAllText(At($"/opt/{Slug}-gateway/version")).Trim());
        Assert.True(File.Exists(At($"/etc/systemd/system/{Slug}-gateway.service")));
        Assert.True(File.Exists(At($"/usr/local/libexec/{Slug}-gateway-update")));
        Assert.Contains(
            "\"version\":\"0.10.0\",\"step\":\"done\"",
            File.ReadAllText(At($"/var/lib/{Slug}/update-status.json")),
            StringComparison.Ordinal
        );
        // The old one stopped first, its units gone so nothing starts it again, its folders kept for the operator.
        Assert.True(
            Array.IndexOf(Systemctl, $"stop {Old}-gateway") < Array.IndexOf(Systemctl, $"start {Slug}-gateway")
        );
        Assert.Contains($"disable {Old}-gateway", Systemctl);
        Assert.Contains($"disable --now {Old}-gateway-update.path", Systemctl);
        Assert.False(File.Exists(At($"/etc/systemd/system/{Old}-gateway.service")));
        Assert.False(File.Exists(At($"/usr/local/libexec/{Old}-gateway-update")));
        Assert.True(File.Exists(At($"/var/lib/{Old}/settings.json")));
        Assert.True(File.Exists(At($"/opt/{Old}-gateway/{Old}-gateway")));
    }

    [Fact]
    public async Task WhenTheNewOneDoesntStayUpTheOldOneStartsAgainFromItsOwnFolders()
    {
        OldInstall();
        File.WriteAllText(Path.Combine(Root, $"crash-{Slug}-gateway"), "");
        var tarball = await TarballAsync("0.10.0");

        var (exit, output) = await InstallAsync("--migrate-from", Old, tarball);

        Assert.NotEqual(0, exit);
        Assert.Contains($"so {Old} is back", output, StringComparison.Ordinal);
        Assert.Equal($"start {Old}-gateway", Systemctl[^1]);
        Assert.Contains($"disable {Slug}-gateway", Systemctl);
        Assert.True(File.Exists(At($"/etc/systemd/system/{Old}-gateway.service")));
        Assert.True(File.Exists(At($"/usr/local/libexec/{Old}-gateway-update")));
        Assert.Equal("active", File.ReadAllText(Path.Combine(Root, "state", $"{Old}-gateway")).Trim());
    }

    [Fact]
    public async Task DataAlreadyUnderTheNewNameIsNotOverwritten()
    {
        OldInstall();
        Write($"/var/lib/{Slug}/settings.json", """{"station":"kept"}""");
        var tarball = await TarballAsync("0.10.0");

        var (exit, output) = await InstallAsync("--migrate-from", Old, tarball);

        Assert.True(exit == 0, output);
        Assert.Equal("""{"station":"kept"}""", File.ReadAllText(At($"/var/lib/{Slug}/settings.json")));
    }

    [Theory]
    [InlineData("../etc")]
    [InlineData("Old Map")]
    public async Task AnOldNameThatIsntASlugIsRefusedBeforeAnything(string old)
    {
        var tarball = await TarballAsync("0.10.0");

        var (exit, output) = await InstallAsync("--migrate-from", old, tarball);

        Assert.NotEqual(0, exit);
        Assert.Contains("Not a slug", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Root, "systemctl.log")));
    }

    [Fact]
    public async Task APlainInstallStillInstallsAndWaitsForTheConfigurationOnAFirstInstall()
    {
        var tarball = await TarballAsync("0.9.0");

        var (exit, output) = await InstallAsync(tarball);

        Assert.True(exit == 0, output);
        Assert.True(File.Exists(At($"/etc/{Slug}/gateway.toml")));
        Assert.True(File.Exists(At($"/etc/systemd/system/{Slug}-gateway-update.path")));
        Assert.Contains("set your callsign", output, StringComparison.Ordinal);
        Assert.DoesNotContain($"start {Slug}-gateway", Systemctl);
    }
}
