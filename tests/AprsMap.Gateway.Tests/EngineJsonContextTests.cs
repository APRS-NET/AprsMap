// SPDX-License-Identifier: AGPL-3.0-only
using System.Reflection;
using System.Text.Json;
using AprsMap.Contracts;

namespace AprsMap.Gateway.Tests;

public class EngineJsonContextTests
{
    /// <summary>What the browser app has: source-generated metadata and nothing else.</summary>
    internal static readonly JsonSerializerOptions WithoutReflection = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = EngineJsonContext.Default,
    };

    public static TheoryData<Type> HubTypes()
    {
        var types = new TheoryData<Type>();
        foreach (var type in typeof(IEngineService).GetMethods().SelectMany(WireTypes).Distinct())
        {
            types.Add(type);
        }
        return types;
    }

    [Theory]
    [MemberData(nameof(HubTypes))]
    public void EveryEngineServiceTypeHasSourceGeneratedJsonMetadata(Type type) =>
        Assert.NotNull(EngineJsonContext.Default.GetTypeInfo(type));

    [Fact]
    public void HubTypesRoundTripWithoutReflection()
    {
        var status = new EngineStatus(ConnectionState.Connected, "T2TEST", 42, "1.2.3");
        var packet = new PacketView(
            new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero),
            "N0CALL-9",
            "Position",
            "car at 49.0583, -72.0292",
            "N0CALL-9>APRS:!4903.50N/07201.75W>",
            49.0583,
            -72.0292,
            "car"
        );

        var station = new StationState("N0CALL-9", packet.ReceivedAt, 49.0583, -72.0292, "/>");
        var batch = new StationBatch([station], ["LEADER"]);

        Assert.Equal(status, RoundTrip(status));
        Assert.Equal(packet, RoundTrip(packet));
        var copy = RoundTrip(batch);
        Assert.NotNull(copy);
        Assert.Equal(station, Assert.Single(copy.Changed));
        Assert.Equal(["LEADER"], copy.Removed);
    }

    [Fact]
    public void SettingsRoundTripWithoutReflection()
    {
        var profile = new StationProfile(
            "home",
            "Home",
            "N0CALL-9",
            12345,
            "rotate.aprs2.net:14580",
            "r/40/-105/50",
            40,
            -105
        );
        var settings = new AppSettings([profile], "home", UnitSettings.Imperial, ReadOnly: true);

        var copy = RoundTrip(settings);

        Assert.NotNull(copy);
        Assert.Equal(profile, Assert.Single(copy.Profiles));
        Assert.Equal("home", copy.ActiveProfileId);
        Assert.Equal(UnitSettings.Imperial, copy.Units);
        Assert.True(copy.ReadOnly);
        Assert.Equal(profile, copy.GetActiveProfile());
    }

    [Fact]
    public void GeocodingTypesRoundTripWithoutReflection()
    {
        IReadOnlyList<GeocodeResult> results = [new("Boulder, Colorado", 40.015, -105.27)];
        var info = new GeocodingInfo(true, "nominatim.openstreetmap.org", "https://example.org/policy");

        Assert.Equal(results, RoundTrip(results));
        Assert.Equal(info, RoundTrip(info));
        Assert.Equal(GeocodingInfo.Disabled, RoundTrip(GeocodingInfo.Disabled));
    }

    private static T? RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, WithoutReflection), WithoutReflection);

    /// <summary>Parameter, return and stream item types, unwrapped from Task and IAsyncEnumerable.</summary>
    private static IEnumerable<Type> WireTypes(MethodInfo method)
    {
        var types = method
            .GetParameters()
            .Select(p => p.ParameterType)
            .Where(t => t != typeof(CancellationToken))
            .Append(method.ReturnType);
        foreach (var type in types)
        {
            var unwrapped =
                type.IsGenericType
                && (
                    type.GetGenericTypeDefinition() == typeof(Task<>)
                    || type.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>)
                )
                    ? type.GetGenericArguments()[0]
                    : type;
            if (unwrapped != typeof(Task) && unwrapped != typeof(void))
            {
                yield return unwrapped;
            }
        }
    }
}
