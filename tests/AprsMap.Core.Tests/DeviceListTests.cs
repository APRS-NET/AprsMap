// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Net;
using System.Text;
using AprsMap.Contracts;
using AprsNet.DeviceId;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.Core.Tests;

/// <summary>Device list updates (docs/features/F4.16-device-list.md, acceptance tests 1–6).</summary>
public sealed class DeviceListTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _folder = Directory.CreateTempSubdirectory("aprsmap-devicelist-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>A list as published, generated at <paramref name="generated"/>, naming the TOCALL APZXQ1.</summary>
    private static string List(
        string generated,
        string tocalls = """{"APZXQ1":{"vendor":"Example Radio Co","model":"XQ-1","class":"ht"}}"""
    ) =>
        $$$"""{"classes":{"ht":{}},"tocalls":{{{tocalls}}},"mice":{},"micelegacy":{},"meta":{"generation_time":"{{{generated}}}"}}""";

    private const string Newer = "2099-01-01T00:00:00Z";
    private const string Older = "2001-01-01T00:00:00Z";

    /// <summary>A server that answers with what it's given, and remembers what it was asked.</summary>
    private sealed class Server : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request);
            return Task.FromResult(Answer(request));
        }

        public static Func<HttpRequestMessage, HttpResponseMessage> With(string body, string etag = "\"v1\"") =>
            request =>
                request.Headers.IfNoneMatch.Any(t => t.Tag == etag)
                    ? new HttpResponseMessage(HttpStatusCode.NotModified)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                        Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag) },
                    };
    }

    private (
        Engine Engine,
        Server Server,
        FakeTimeProvider Time,
        InMemorySettingsStore Store,
        EngineTests.FakeSourceFactory Sources
    ) Create(
        string? url = "https://mirror.example/tocalls.json",
        DeviceListSchedule schedule = DeviceListSchedule.Weekly,
        bool internet = true
    )
    {
        var home = InMemorySettingsStore.Profile("N0TEST") with { SourceIds = [InMemorySettingsStore.Rotate.Id] };
        var store = new InMemorySettingsStore(
            new AppSettings(
                [home],
                home.Id,
                UnitSettings.Metric,
                Sources: [InMemorySettingsStore.Rotate],
                Toggles: StationToggles.AllOn with
                {
                    Internet = internet,
                },
                DeviceList: new DeviceListSettings(schedule)
            )
        );
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-21T12:00:00Z", CultureInfo.InvariantCulture));
        var options = new EngineOptions();
        options.DeviceList.Url = url;
        options.DeviceList.Folder = _folder;
        var server = new Server();
        var sources = new EngineTests.FakeSourceFactory();
        var engine = new Engine(Options.Create(options), sources, store, time, NullLogger<Engine>.Instance)
        {
            DeviceListHttp = new HttpClient(server),
        };
        engine.DeviceListHttp.DefaultRequestHeaders.UserAgent.ParseAdd("aprsmap/0.0 (test)");
        return (engine, server, time, store, sources);
    }

    private string Kept => Path.Combine(_folder, Engine.DeviceListFile);

    [Fact]
    public async Task WithNoDownloadedListTheBuiltInOneIsUsed()
    {
        var (engine, _, _, _, _) = Create();
        using (engine)
        {
            var status = await engine.GetDeviceListAsync(Ct);

            Assert.Equal(DeviceListOrigin.BuiltIn, status.Origin);
            Assert.Equal(AprsDeviceList.BuiltIn.GeneratedAt, status.GeneratedAt);
            Assert.Equal(AprsDeviceList.BuiltIn.Commit, status.Commit);
            Assert.True(status.CanDownload);
            Assert.Null(status.LastChecked);
        }
    }

    [Fact]
    public async Task ANewerListIsKeptUsedAndNamesStationsAlreadyHeardThenANotModifiedChangesNothing()
    {
        var (engine, server, _, store, sources) = Create();
        using (engine)
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            var aprsIs = sources.Enqueue("T2TEST");
            var run = engine.RunAsync(cts.Token);
            await aprsIs.Connected.Task.WaitAsync(Ct);
            await aprsIs.Write("W1AW-9>APZXQ1,TCPIP*:!4903.50N/07201.75W>Mobile");
            await WaitUntil(async () => await engine.GetStationAsync("W1AW-9", Ct) is not null);
            var before = (await engine.GetStationAsync("W1AW-9", Ct))!.Device; // the built-in list: "Experimental"
            Assert.NotEqual("XQ-1", before?.Model);

            server.Answer = Server.With(List(Newer));
            var status = await engine.UpdateDeviceListAsync(Ct);

            Assert.Equal(DeviceListOrigin.Downloaded, status.Origin);
            Assert.Null(status.LastError);
            Assert.Equal(List(Newer), await File.ReadAllTextAsync(Kept, Ct)); // kept as published
            Assert.Equal(
                new StationDevice("Example Radio Co", "XQ-1", "HandHeld"),
                (await engine.GetStationAsync("W1AW-9", Ct))!.Device
            ); // named at once (behavior 3)
            Assert.Equal("\"v1\"", (await engine.GetSettingsAsync(Ct)).EffectiveDeviceList.ETag);
            Assert.Contains(
                "aprsmap",
                server.Requests[0].Headers.UserAgent.ToString(),
                StringComparison.OrdinalIgnoreCase
            );

            status = await engine.UpdateDeviceListAsync(Ct);
            Assert.Equal("\"v1\"", server.Requests[^1].Headers.IfNoneMatch.Single().Tag); // asked only for a newer one
            Assert.Equal((DeviceListOrigin.Downloaded, (EngineError?)null), (status.Origin, status.LastError));
            await cts.CancelAsync();
            await run;
        }
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 400; i++)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, Ct);
        }
        Assert.Fail("Condition was not met in time.");
    }

    [Theory]
    [InlineData(Older, null, EngineErrorCodes.DeviceListNotNewer)]
    [InlineData(null, "{ not json", EngineErrorCodes.DeviceListUnreadable)]
    [InlineData(
        Newer,
        """{"classes":{"spaceship":{}},"tocalls":{"APZXQ1":{"class":"spaceship"}},"mice":{},"micelegacy":{}}""",
        EngineErrorCodes.DeviceListUnreadable
    )]
    public async Task AListThatIsntNewerOrCantBeReadIsThrownAwayAndSaysWhy(string? generated, string? body, string code)
    {
        var (engine, server, _, _, _) = Create();
        using (engine)
        {
            server.Answer = Server.With(body ?? List(generated!));

            var status = await engine.UpdateDeviceListAsync(Ct);

            Assert.Equal(DeviceListOrigin.BuiltIn, status.Origin);
            Assert.Equal(code, status.LastError?.Code);
            Assert.False(File.Exists(Kept));
            Assert.NotNull(status.LastChecked);
        }
    }

    [Fact]
    public async Task AnUnreachableServerOrA404IsSaidAndChangesNothing()
    {
        var (engine, server, _, _, _) = Create();
        using (engine)
        {
            Assert.Equal(
                EngineErrorCodes.DeviceListUnexpectedAnswer,
                (await engine.UpdateDeviceListAsync(Ct)).LastError?.Code
            );
            server.Answer = _ => throw new HttpRequestException("No route to host");
            Assert.Equal(
                EngineErrorCodes.DeviceListUnreachable,
                (await engine.UpdateDeviceListAsync(Ct)).LastError?.Code
            );
        }
    }

    [Fact]
    public async Task OnStartAKeptListNewerThanTheBuiltInIsUsedAndAnOlderOneIsDeleted()
    {
        await File.WriteAllTextAsync(Kept, List(Newer), Ct);
        var (engine, _, _, _, _) = Create();
        using (engine)
        {
            engine.LoadSavedDeviceList();
            Assert.Equal(DeviceListOrigin.Downloaded, (await engine.GetDeviceListAsync(Ct)).Origin);
        }

        await File.WriteAllTextAsync(Kept, List(Older), Ct); // the app was updated since
        var (again, _, _, _, _) = Create();
        using (again)
        {
            again.LoadSavedDeviceList();
            Assert.Equal(DeviceListOrigin.BuiltIn, (await again.GetDeviceListAsync(Ct)).Origin);
            Assert.False(File.Exists(Kept));
        }
    }

    [Theory]
    [InlineData(DeviceListSchedule.Weekly, true, 2)] // a few minutes after start, then a week later
    [InlineData(DeviceListSchedule.Daily, true, 8)]
    [InlineData(DeviceListSchedule.Off, true, 0)]
    [InlineData(DeviceListSchedule.Weekly, false, 0)] // the Internet switch off
    public async Task TheScheduleChecksOnTimeAndNeverWithTheInternetOff(
        DeviceListSchedule schedule,
        bool internet,
        int checks
    )
    {
        var (engine, server, time, _, _) = Create(schedule: schedule, internet: internet);
        server.Answer = Server.With(List(Older));
        using (engine)
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            var run = engine.RunDeviceListAsync(cts.Token);
            for (var hour = 0; hour < 7 * 24 + 1; hour++)
            {
                time.Advance(TimeSpan.FromHours(1));
                for (var i = 0; i < 20 && server.Requests.Count < checks; i++)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1), TimeProvider.System, Ct);
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), TimeProvider.System, Ct);
            Assert.Equal(checks, server.Requests.Count);
            await cts.CancelAsync();
            await run;
        }
    }

    [Fact]
    public async Task AnEmptyAddressNeverDownloads()
    {
        var (engine, server, _, _, _) = Create(url: "");
        using (engine)
        {
            var status = await engine.UpdateDeviceListAsync(Ct);
            Assert.False(status.CanDownload);
            Assert.Empty(server.Requests);
        }
    }
}
