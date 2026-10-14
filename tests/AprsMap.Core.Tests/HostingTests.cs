// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsMap.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

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
        var store = Assert.IsType<FileSettingsStore>(provider.GetRequiredService<ISettingsStore>());
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "never-written"), store.FilePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledEngineDoesNotConnect()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<ISettingsStore>(InMemorySettingsStore.WithProfile())
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
        var factory = new BlockingSourceFactory();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IPacketLineSourceFactory>(factory)
            .AddSingleton<ISettingsStore>(InMemorySettingsStore.WithProfile())
            .AddAprsMapEngine();
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetRequiredService<IHostedService>();

        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await factory.Created.Task.WaitAsync(TestContext.Current.CancellationToken);
        var status = await provider
            .GetRequiredService<IEngineService>()
            .GetStatusAsync(TestContext.Current.CancellationToken);
        await hosted.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionState.Connecting, status.Connection);
    }

    [Fact]
    public void UnknownBrandKeysReadAsEmpty() => Assert.Equal(string.Empty, Brand.Get("Brand.DoesNotExist"));

    [Fact]
    public void LineSourceFactoryRejectsABadServer()
    {
        var factory = new AprsIsLineSourceFactory(NullLoggerFactory.Instance, TimeProvider.System);

        Assert.Throws<InvalidOperationException>(() =>
            factory.Create(InMemorySettingsStore.Profile("N0CALL") with { Server = "bad host:x" })
        );
    }

    [Fact]
    public async Task LineSourceFactoryBuildsAClientWithoutConnecting()
    {
        var factory = new AprsIsLineSourceFactory(NullLoggerFactory.Instance, TimeProvider.System);

        var source = factory.Create(
            InMemorySettingsStore.Profile("N0CALL") with
            {
                Server = "localhost:14580",
                Filter = "r/40/-105/50",
            }
        );

        await source.DisposeAsync();
    }

    [Theory]
    [InlineData("AprsMap", "AprsMap")]
    [InlineData("APRS Atlas", "APRSAtlas")]
    [InlineData("   ", "unknown")]
    public void SoftwareTokenHasNoSpaces(string input, string expected) =>
        Assert.Equal(expected, AprsIsLineSourceFactory.SoftwareToken(input));

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
    private sealed class BlockingSourceFactory : IPacketLineSourceFactory, IPacketLineSource
    {
        public TaskCompletionSource Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IPacketLineSource Create(StationProfile profile)
        {
            Created.TrySetResult();
            return this;
        }

        public async Task<string> ConnectAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, cancellationToken);
            return string.Empty;
        }

        public IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellationToken) =>
            AsyncEnumerable.Empty<string>();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
