// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsMap.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AprsMap.Core.Tests;

public class HostingTests
{
    [Fact]
    public void AddAprsMapEngineRegistersOneEngineBehindTheContract()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddAprsMapEngine()
            .Configure<EngineOptions>(o => o.SettingsFolder = Path.Combine(Path.GetTempPath(), "never-written"));
        using var provider = services.BuildServiceProvider();

        var engine = provider.GetRequiredService<Engine>();
        Assert.Same(engine, provider.GetRequiredService<IEngineService>());
        Assert.Single(provider.GetServices<IHostedService>());
        Assert.IsType<NominatimGeocoder>(provider.GetRequiredService<IGeocoder>());
        var store = Assert.IsType<FileSettingsStore>(provider.GetRequiredService<ISettingsStore>());
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "never-written"), store.FilePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledEngineDoesNotConnect()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ISettingsStore>(InMemorySettingsStore.WithProfile())
            .AddSingleton<IMessageStore>(new InMemoryMessageStore()) // never the real Data folder
            .AddAprsMapEngine()
            .Configure<EngineOptions>(o => o.Enabled = false);
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetRequiredService<IHostedService>();

        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);

        var status = await provider
            .GetRequiredService<IEngineService>()
            .GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ConnectionState.Disconnected, status.Connection);
    }

    [Fact]
    public async Task EnabledEngineStartsConnectingWithTheHost()
    {
        var data = Directory.CreateTempSubdirectory("aprsmap-hosting-");
        using var cleanup = new Cleanup(data);
        var factory = new BlockingSourceFactory();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IPacketSourceFactory>(factory)
            .AddSingleton<ISettingsStore>(InMemorySettingsStore.WithProfile())
            .AddSingleton<IMessageStore>(new InMemoryMessageStore()) // never the real Data folder
            .AddAprsMapEngine()
            .Configure<EngineOptions>(o => o.DataFolder = data.FullName); // the dashboard's history too (#386)
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetRequiredService<IHostedService>();

        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await factory.Created.Task.WaitAsync(TestContext.Current.CancellationToken);
        var status = await provider
            .GetRequiredService<IEngineService>()
            .GetStatusAsync(TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionState.Connecting, status.Connection);
        Assert.True(File.Exists(Path.Combine(data.FullName, FileHistoryStore.FileName))); // saved as it stopped
    }

    [Fact]
    public async Task AnEngineForUpdatesOnlyOpensNoSourceAndKeepsNoHistory()
    {
        // The desktop as a gateway's client (F1.39 behavior 5): its own engine checks for updates and nothing else.
        var data = Directory.CreateTempSubdirectory("aprsmap-hosting-");
        using var cleanup = new Cleanup(data);
        var factory = new BlockingSourceFactory();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IPacketSourceFactory>(factory)
            .AddSingleton<ISettingsStore>(InMemorySettingsStore.WithProfile())
            .AddSingleton<IMessageStore>(new InMemoryMessageStore())
            .AddAprsMapEngine()
            .Configure<EngineOptions>(o =>
            {
                o.DataFolder = data.FullName;
                o.UpdatesOnly = true;
                o.Updates.Check = false; // no request to GitHub from a test
            });
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetRequiredService<IHostedService>();

        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);

        Assert.False(factory.Created.Task.IsCompleted);
        Assert.False(File.Exists(Path.Combine(data.FullName, FileHistoryStore.FileName)));
    }

    [Fact]
    public async Task WithAprsIsOffOnStartTheSwitchIsTurnedOffBeforeConnecting()
    {
        // #415: APRS-IS only by hand.
        var data = Directory.CreateTempSubdirectory("aprsmap-hosting-");
        using var cleanup = new Cleanup(data);
        var factory = new BlockingSourceFactory();
        var store = InMemorySettingsStore.WithProfile();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IPacketSourceFactory>(factory)
            .AddSingleton<ISettingsStore>(store)
            .AddSingleton<IMessageStore>(new InMemoryMessageStore())
            .AddAprsMapEngine()
            .Configure<EngineOptions>(o =>
            {
                o.DataFolder = data.FullName;
                o.AprsIsOnStart = false;
            });
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetRequiredService<IHostedService>();

        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, TestContext.Current.CancellationToken);
        var settings = await provider
            .GetRequiredService<IEngineService>()
            .GetSettingsAsync(TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);

        Assert.False(settings.EffectiveToggles.AprsIs);
        Assert.False(factory.Created.Task.IsCompleted); // APRS-IS never opened
        Assert.False((await store.LoadAsync(TestContext.Current.CancellationToken))!.EffectiveToggles.AprsIs);
    }

    private sealed class Cleanup(DirectoryInfo folder) : IDisposable
    {
        public void Dispose() => folder.Delete(recursive: true);
    }

    [Fact]
    public void UnknownBrandKeysReadAsEmpty() => Assert.Equal(string.Empty, Brand.Get("Brand.DoesNotExist"));

    [Fact]
    public void SourceFactoryRejectsABadServer()
    {
        var factory = new AprsIsPacketSourceFactory(
            Options.Create(new EngineOptions()),
            NullLoggerFactory.Instance,
            TimeProvider.System
        );

        var ex = Assert.Throws<AprsNet.Packets.AprsValidationException>(() =>
            factory.Create(
                InMemorySettingsStore.Profile("N0CALL"),
                InMemorySettingsStore.Rotate with
                {
                    Server = "bad host:x",
                },
                "m/150"
            )
        );
        Assert.Equal(AprsNet.Packets.AprsValidationErrorCode.ServerHostInvalid, ex.Code);
    }

    [Fact]
    public async Task SourceFactoryBuildsAConnectionWithoutConnecting()
    {
        var factory = new AprsIsPacketSourceFactory(
            Options.Create(new EngineOptions()),
            NullLoggerFactory.Instance,
            TimeProvider.System
        );

        var source = factory.Create(
            InMemorySettingsStore.Profile("N0CALL"),
            InMemorySettingsStore.Rotate with
            {
                Server = "localhost:14580",
            },
            "r/40/-105/50"
        );

        await source.DisposeAsync();
    }

    [Theory]
    [InlineData("AprsMap", "AprsMap")]
    [InlineData("APRS Atlas", "APRSAtlas")]
    [InlineData("   ", "unknown")]
    public void SoftwareTokenHasNoSpaces(string input, string expected) =>
        Assert.Equal(expected, AprsIsPacketSourceFactory.SoftwareToken(input));

    [Fact]
    public void BrandValuesComeFromBrandingProps()
    {
        Assert.False(string.IsNullOrWhiteSpace(Brand.DisplayName));
        Assert.Equal(Brand.Slug.ToLowerInvariant(), Brand.Slug);
        Assert.StartsWith("AP", Brand.Tocall, StringComparison.Ordinal);
        Assert.StartsWith("https://", Brand.SourceUrl, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(Brand.Version));
        Assert.DoesNotContain(Brand.Slug, Brand.PreviousSlugs);
    }

    /// <summary>A source whose connect never completes, to observe the engine mid-connect.</summary>
    private sealed class BlockingSourceFactory : IPacketSourceFactory, IPacketSource
    {
        public TaskCompletionSource Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IPacketSource Create(StationProfile profile, SourceSettings source, string filter)
        {
            Created.TrySetResult();
            return this;
        }

        public async Task<PacketSourceLogin> StartAsync(
            Action<PacketSourceEvent> stateChanged,
            CancellationToken cancellationToken
        )
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken);
            return new PacketSourceLogin(string.Empty, false);
        }

        public ValueTask SetFilterAsync(string filter, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public IAsyncEnumerable<AprsNet.Packets.AprsPacket> ReadPacketsAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<AprsNet.Packets.AprsPacket>();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
