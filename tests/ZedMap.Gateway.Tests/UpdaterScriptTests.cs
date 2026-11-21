// SPDX-License-Identifier: AGPL-3.0-only
using System.Diagnostics;
using System.Security.Cryptography;

namespace ZedMap.Gateway.Tests;

/// <summary>
/// The service gateway's updater, <c>deploy/linux/zedmap-gateway-update.sh</c>
/// (docs/features/F1.40-gateway-service-updates.md, acceptance tests 4, 5): run against folders of its own, with
/// stand-ins for <c>systemctl</c> and <c>curl</c> that serve a fake release. Linux only, as the service is.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class UpdaterScriptTests : IDisposable
{
    private const string Source = "https://github.com/test/repo";

    /// <summary>
    /// The next slug in these tests: any slug other than this one; not the real NextSlug, which a rename (and
    /// rename-check) rewrites along with this one's.
    /// </summary>
    private const string Next = "nextname";
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("zedmap-updater-");

    private string Root => _root.FullName;

    private string App => Path.Combine(Root, "opt", "zedmap-gateway");

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
        // systemctl: logs each call; a gateway with a "crash" file doesn't stay up, after rewriting the settings as a
        // newer version might.
        Shim(
            "systemctl",
            $$"""
            echo "$*" >> "{{Root}}/systemctl.log"
            case "$1" in
              is-active) cat "{{Root}}/active" ;;
              show) cat "{{Root}}/restarts" ;;
              start) if [ -f "{{App}}/crash" ]; then echo failed > "{{Root}}/active"; echo '{"newer":true}' > "{{Data}}/settings.json"; else echo active > "{{Root}}/active"; fi ;;
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
        File.WriteAllText(Path.Combine(folder, "zedmap-gateway"), "#!/bin/sh\n");
        File.SetUnixFileMode(
            Path.Combine(folder, "zedmap-gateway"),
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

    /// <summary>
    /// Publishes <paramref name="version"/> under the next slug, as the release that renames the gateway will
    /// (docs/branding.md, "Renaming in two steps"): <c>{Next}-gateway-vX</c>, holding <c>{Next}-gateway</c> and an
    /// <c>install.sh</c> stand-in that logs how it was called and exits with <paramref name="installExit"/>.
    /// </summary>
    private async Task RenamedReleaseAsync(string version, int installExit = 0)
    {
        var build = Path.Combine(Root, "build-" + version);
        Directory.CreateDirectory(build);
        await File.WriteAllTextAsync(Path.Combine(build, $"{Next}-gateway"), "#!/bin/sh\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(build, "version"), "v" + version + "\n", Ct);
        await File.WriteAllTextAsync(
            Path.Combine(build, "install.sh"),
            $"#!/usr/bin/env bash\necho \"$@\" > \"{Root}/install.log\"\nexit {installExit}\n",
            Ct
        );
        foreach (var file in new[] { $"{Next}-gateway", "install.sh" })
        {
            File.SetUnixFileMode(
                Path.Combine(build, file),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }
        var folder = Path.Combine(Releases, "v" + version);
        Directory.CreateDirectory(folder);
        var tarball = Path.Combine(folder, $"{Next}-gateway-v{version}-{Rid}.tar.gz");
        await RunAsync("tar", "-C", build, "-czf", tarball, ".");
        var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(tarball, Ct)));
        await File.WriteAllTextAsync(tarball + ".sha256", $"{hash}  {Path.GetFileName(tarball)}\n", Ct);
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
        var tarball = Path.Combine(folder, $"zedmap-gateway-v{version}-{Rid}.tar.gz");
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
        return await RunAsync("bash", [Path.Combine(Deploy, "zedmap-gateway-update.sh"), .. args]);
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
        start.Environment["ZEDMAP_DATA"] = Data;
        start.Environment["ZEDMAP_APP"] = App;
        start.Environment["ZEDMAP_SETTLE_SECONDS"] = "0";
        start.Environment["ZEDMAP_UNITS"] = Path.Combine(Root, "units");
        start.Environment["ZEDMAP_LIBEXEC"] = Path.Combine(Root, "libexec");
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
        Assert.True(File.Exists(Path.Combine(Root, "libexec", "zedmap-gateway-update")));
        Assert.True(File.Exists(Path.Combine(Root, "units", "zedmap-gateway-update.path")));
        Assert.Equal(
            [
                $"{Source}/releases/download/v0.9.0/zedmap-gateway-v0.9.0-{Rid}.tar.gz",
                $"{Source}/releases/download/v0.9.0/zedmap-gateway-v0.9.0-{Rid}.tar.gz.sha256",
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
    public async Task TheSettingsComeBackWithTheRollback()
    {
        // docs/features/F1.40-gateway-service-updates.md, behavior 9.
        var settings = Path.Combine(Data, "settings.json");
        await File.WriteAllTextAsync(settings, """{"older":true}""", Ct);
        await ReleaseAsync("0.9.0", crashes: true);

        var (exit, _) = await UpdateAsync("""{"version":"0.9.0"}""");

        Assert.NotEqual(0, exit);
        Assert.Equal("""{"older":true}""", (await File.ReadAllTextAsync(settings, Ct)).Trim());
    }

    [Fact]
    public async Task ASuccessfulUpdateKeepsACopyOfTheSettingsFromBefore()
    {
        await File.WriteAllTextAsync(Path.Combine(Data, "settings.json"), """{"older":true}""", Ct);
        await ReleaseAsync("0.9.0");

        var (exit, output) = await UpdateAsync("""{"version":"0.9.0"}""");

        Assert.True(exit == 0, output);
        Assert.Equal(
            """{"older":true}""",
            (await File.ReadAllTextAsync(Path.Combine(Data, "settings.json.before-update"), Ct)).Trim()
        );
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

    [Fact]
    public async Task AReleaseUnderTheNextSlugIsFetchedAndHandedToItsOwnInstaller()
    {
        // docs/branding.md, "Renaming in two steps": this install knows the next slug (next-slug, from NextSlug); the
        // release that renames has no zedmap-gateway tarball, so the updater takes <next>-gateway's and lets that
        // release's install.sh move the install over, as only it knows the new names.
        File.WriteAllText(Path.Combine(App, "next-slug"), Next + "\n");
        await RenamedReleaseAsync("0.10.0");

        var (exit, output) = await UpdateAsync("""{"version":"0.10.0"}""");

        Assert.True(exit == 0, output);
        Assert.Equal(
            [
                $"{Source}/releases/download/v0.10.0/zedmap-gateway-v0.10.0-{Rid}.tar.gz",
                $"{Source}/releases/download/v0.10.0/{Next}-gateway-v0.10.0-{Rid}.tar.gz",
                $"{Source}/releases/download/v0.10.0/{Next}-gateway-v0.10.0-{Rid}.tar.gz.sha256",
            ],
            File.ReadAllLines(Path.Combine(Root, "curl.log"))
        );
        var install = (await File.ReadAllTextAsync(Path.Combine(Root, "install.log"), Ct)).Trim();
        Assert.StartsWith("--migrate-from zedmap ", install, StringComparison.Ordinal);
        Assert.EndsWith($"{Next}-gateway-v0.10.0-{Rid}.tar.gz", install, StringComparison.Ordinal);
        Assert.Contains("\"step\":\"done\"", Status, StringComparison.Ordinal);
        Assert.Equal("v0.8.0", Installed); // the installer moves it; the updater leaves the old folder alone
        Assert.False(File.Exists(Path.Combine(Root, "systemctl.log"))); // nor stops it: the installer does
    }

    [Fact]
    public async Task WhenTheNewReleasesInstallerFailsTheInstalledOneStays()
    {
        File.WriteAllText(Path.Combine(App, "next-slug"), Next + "\n");
        await RenamedReleaseAsync("0.10.0", installExit: 1);

        var (exit, _) = await UpdateAsync("""{"version":"0.10.0"}""");

        Assert.NotEqual(0, exit);
        Assert.Equal("v0.8.0", Installed);
        Assert.Contains($"the move to {Next} failed", Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutANextSlugARenamedReleaseIsNotTaken()
    {
        await RenamedReleaseAsync("0.10.0");

        var (exit, _) = await UpdateAsync("""{"version":"0.10.0"}""");

        Assert.NotEqual(0, exit);
        Assert.Contains("couldn't download zedmap-gateway-v0.10.0", Status, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Root, "install.log")));
    }

    [Theory]
    [InlineData(Next + "\n../evil")]
    [InlineData("Zed Map")]
    public async Task ANextSlugThatIsntASlugIsIgnored(string next)
    {
        File.WriteAllText(Path.Combine(App, "next-slug"), next);
        await RenamedReleaseAsync("0.10.0");

        var (exit, _) = await UpdateAsync("""{"version":"0.10.0"}""");

        Assert.NotEqual(0, exit);
        Assert.False(File.Exists(Path.Combine(Root, "install.log")));
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
        var (exit, _) = await RunAsync("bash", Path.Combine(Deploy, "zedmap-gateway-update.sh"), "--semver-gt", a, b);

        Assert.Equal(newer, exit == 0);
    }
}
