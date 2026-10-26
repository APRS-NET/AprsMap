// SPDX-License-Identifier: AGPL-3.0-only
using System.Diagnostics;
using System.Security.Cryptography;

namespace AprsMap.Gateway.Tests;

/// <summary>
/// The service gateway's updater, <c>deploy/linux/aprsmap-gateway-update.sh</c>
/// (docs/features/F1.40-gateway-service-updates.md, acceptance tests 4, 5): run against folders of its own, with
/// stand-ins for <c>systemctl</c> and <c>curl</c> that serve a fake release. Linux only, as the service is.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class UpdaterScriptTests : IDisposable
{
    private const string Source = "https://github.com/test/repo";
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("aprsmap-updater-");

    private string Root => _root.FullName;

    private string App => Path.Combine(Root, "opt", "aprsmap-gateway");

    private string Data => Path.Combine(Root, "data");

    private string Releases => Path.Combine(Root, "releases");

    private static string Rid =>
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
        == System.Runtime.InteropServices.Architecture.Arm64
            ? "linux-arm64"
            : "linux-x64";

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

    public UpdaterScriptTests()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The service and its updater are Linux only.");
        foreach (var dir in new[] { App, Data, Releases, Path.Combine(Root, "bin"), Path.Combine(Root, "units") })
        {
            Directory.CreateDirectory(dir);
        }
        Gateway(App, "v0.8.0");
        File.WriteAllText(Path.Combine(App, "release-source"), Source + "\n");
        File.WriteAllText(Path.Combine(Root, "active"), "active");
        File.WriteAllText(Path.Combine(Root, "restarts"), "0");
        // systemctl: logs each call; a gateway with a "crash" file doesn't stay up.
        Shim(
            "systemctl",
            $$"""
            echo "$*" >> "{{Root}}/systemctl.log"
            case "$1" in
              is-active) cat "{{Root}}/active" ;;
              show) cat "{{Root}}/restarts" ;;
              start) if [ -f "{{App}}/crash" ]; then echo failed > "{{Root}}/active"; else echo active > "{{Root}}/active"; fi ;;
            esac
            exit 0
            """
        );
        // curl: serves the release's files from the folder, and logs what was asked for.
        Shim(
            "curl",
            $$"""
            out=""; url=""
            while [ $# -gt 0 ]; do case "$1" in -o) out="$2"; shift 2 ;; https://*) url="$1"; shift ;; *) shift ;; esac; done
            echo "$url" >> "{{Root}}/curl.log"
            file="{{Releases}}/${url#{{Source}}/releases/download/}"
            [ -f "$file" ] || exit 22
            cp "$file" "$out"
            """
        );
    }

    public void Dispose() => _root.Delete(recursive: true);

    /// <summary>A gateway install in <paramref name="folder"/>: the program, the deploy files and its version.</summary>
    private static void Gateway(string folder, string version)
    {
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.GetFiles(Deploy))
        {
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: true);
        }
        File.WriteAllText(Path.Combine(folder, "aprsmap-gateway"), "#!/bin/sh\n");
        File.SetUnixFileMode(
            Path.Combine(folder, "aprsmap-gateway"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        );
        File.WriteAllText(Path.Combine(folder, "version"), version + "\n");
        File.WriteAllText(Path.Combine(folder, "release-source"), Source + "\n");
    }

    private void Shim(string name, string body)
    {
        var path = Path.Combine(Root, "bin", name);
        File.WriteAllText(path, "#!/usr/bin/env bash\n" + body.Replace("\r", "", StringComparison.Ordinal) + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Publishes <paramref name="version"/> as a release: its tarball and checksum, or a wrong checksum.</summary>
    private async Task ReleaseAsync(string version, bool wrongChecksum = false, bool crashes = false)
    {
        var build = Path.Combine(Root, "build-" + version);
        Gateway(build, "v" + version);
        if (crashes)
        {
            File.WriteAllText(Path.Combine(build, "crash"), "");
        }
        var folder = Path.Combine(Releases, "v" + version);
        Directory.CreateDirectory(folder);
        var tarball = Path.Combine(folder, $"aprsmap-gateway-v{version}-{Rid}.tar.gz");
        await RunAsync("tar", "-C", build, "-czf", tarball, ".");
        var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(tarball, Ct)));
        await File.WriteAllTextAsync(
            tarball + ".sha256",
            $"{(wrongChecksum ? new string('0', 64) : hash)}  {Path.GetFileName(tarball)}\n",
            Ct
        );
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(int Exit, string Output)> UpdateAsync(string request, params string[] args)
    {
        await File.WriteAllTextAsync(Path.Combine(Data, "update-request.json"), request, Ct);
        return await RunAsync("bash", [Path.Combine(Deploy, "aprsmap-gateway-update.sh"), .. args]);
    }

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
        start.Environment["APRSMAP_DATA"] = Data;
        start.Environment["APRSMAP_APP"] = App;
        start.Environment["APRSMAP_SETTLE_SECONDS"] = "0";
        start.Environment["APRSMAP_UNITS"] = Path.Combine(Root, "units");
        start.Environment["APRSMAP_LIBEXEC"] = Path.Combine(Root, "libexec");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var errors = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        return (process.ExitCode, await output + await errors);
    }

    private string Status => File.ReadAllText(Path.Combine(Data, "update-status.json"));

    private string Installed => File.ReadAllText(Path.Combine(App, "version")).Trim();

    [Fact]
    public async Task ANewerReleaseIsCheckedSwappedInAndStarted()
    {
        await ReleaseAsync("0.9.0");

        var (exit, output) = await UpdateAsync("""{"version":"0.9.0"}""");

        Assert.True(exit == 0, output);
        Assert.Equal("v0.9.0", Installed);
        Assert.Equal("v0.8.0", File.ReadAllText(Path.Combine(App + ".previous", "version")).Trim());
        Assert.Contains("\"step\":\"done\"", Status, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Data, "update-request.json")));
        Assert.True(File.Exists(Path.Combine(Root, "libexec", "aprsmap-gateway-update")));
        Assert.True(File.Exists(Path.Combine(Root, "units", "aprsmap-gateway-update.path")));
        Assert.Equal(
            [
                $"{Source}/releases/download/v0.9.0/aprsmap-gateway-v0.9.0-{Rid}.tar.gz",
                $"{Source}/releases/download/v0.9.0/aprsmap-gateway-v0.9.0-{Rid}.tar.gz.sha256",
            ],
            File.ReadAllLines(Path.Combine(Root, "curl.log"))
        );
    }

    [Fact]
    public async Task AWrongChecksumLeavesTheInstalledOne()
    {
        await ReleaseAsync("0.9.0", wrongChecksum: true);

        var (exit, _) = await UpdateAsync("""{"version":"0.9.0"}""");

        Assert.NotEqual(0, exit);
        Assert.Equal("v0.8.0", Installed);
        Assert.Contains("doesn't match its checksum", Status, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Root, "systemctl.log"))); // the gateway was never stopped
    }

    [Fact]
    public async Task ANewVersionThatDoesntStayUpIsRolledBack()
    {
        await ReleaseAsync("0.9.0", crashes: true);

        var (exit, _) = await UpdateAsync("""{"version":"0.9.0"}""");

        Assert.NotEqual(0, exit);
        Assert.Equal("v0.8.0", Installed);
        Assert.True(File.Exists(Path.Combine(App + ".failed", "crash")));
        Assert.Contains("didn't stay running, so 0.8.0 is back", Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"version":"0.7.0"}""", "isn't newer than 0.8.0")]
    [InlineData("""{"version":"0.8.0"}""", "isn't newer than 0.8.0")]
    [InlineData("""{"version":"0.9.0; rm -rf /"}""", "isn't a version")]
    [InlineData("""{"url":"https://evil.example/x.tar.gz"}""", "isn't a version")]
    public async Task ARequestForAnythingButANewerVersionIsRefusedBeforeAnyDownload(string request, string why)
    {
        var (exit, _) = await UpdateAsync(request);

        Assert.NotEqual(0, exit);
        Assert.Contains(why, Status, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Root, "curl.log")));
        Assert.False(File.Exists(Path.Combine(Data, "update-request.json")));
    }

    [Theory]
    [InlineData("0.9.0", "0.8.0", true)]
    [InlineData("0.8.0", "0.8.0-alpha.3", true)]
    [InlineData("0.8.0-alpha.10", "0.8.0-alpha.9", true)]
    [InlineData("0.8.0-beta", "0.8.0-alpha.9", true)]
    [InlineData("0.8.0-alpha.1", "0.8.0-alpha", true)]
    [InlineData("0.10.0", "0.9.9", true)]
    [InlineData("0.8.0-alpha.3", "0.8.0", false)]
    [InlineData("0.8.0", "0.8.0", false)]
    [InlineData("0.7.9", "0.8.0-alpha.1", false)]
    public async Task VersionsCompareAsSemVer(string a, string b, bool newer)
    {
        var (exit, _) = await RunAsync("bash", Path.Combine(Deploy, "aprsmap-gateway-update.sh"), "--semver-gt", a, b);

        Assert.Equal(newer, exit == 0);
    }
}
