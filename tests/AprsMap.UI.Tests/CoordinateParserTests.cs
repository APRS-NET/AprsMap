// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Input;

namespace AprsMap.UI.Tests;

public class CoordinateParserTests
{
    [Theory]
    [InlineData("40.0150, -105.2705", 40.015, -105.2705)]
    [InlineData("40.0150 -105.2705", 40.015, -105.2705)]
    [InlineData("  -33.8688,151.2093 ", -33.8688, 151.2093)]
    [InlineData("40.0150 N, 105.2705 W", 40.015, -105.2705)]
    [InlineData("40.0150N 105.2705W", 40.015, -105.2705)]
    [InlineData("n40.0150 w105.2705", 40.015, -105.2705)]
    [InlineData("N 40.0150 W 105.2705", 40.015, -105.2705)]
    [InlineData("105.2705 W 40.0150 N", 40.015, -105.2705)] // the letters say which is which
    [InlineData("40 00.90 N 105 16.23 W", 40.015, -105.2705)]
    [InlineData("40°00.90'N 105°16.23'W", 40.015, -105.2705)]
    [InlineData("40 00.90, -105 16.23", 40.015, -105.2705)]
    [InlineData("40°00′54″N 105°16′13.8″W", 40.015, -105.2705)]
    [InlineData("40 0 54 S 105 16 13.8 E", -40.015, 105.2705)]
    [InlineData("90, 180", 90, 180)]
    public void ReadsTheCommonForms(string text, double latitude, double longitude)
    {
        Assert.Equal(CoordinateParseError.None, CoordinateParser.TryParse(text, out var lat, out var lon));
        Assert.Equal(latitude, lat, 5);
        Assert.Equal(longitude, lon, 5);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Boulder")]
    [InlineData("40.0150")]
    [InlineData("40.0150, -105.2705, 12")]
    [InlineData("40.0150 N 41.0 S")] // two latitudes
    [InlineData("-40.0150 S, 105 W")] // a sign and a letter
    [InlineData("40.5 30 N, 105 W")] // a fraction before the minutes
    [InlineData("40 -30 N, 105 W")] // negative minutes
    [InlineData("40,0150 -105,2705 x")]
    [InlineData("40 1 2 3, 105")]
    public void RefusesWhatItCantRead(string? text) =>
        Assert.Equal(CoordinateParseError.Unreadable, CoordinateParser.TryParse(text, out _, out _));

    [Theory]
    [InlineData("91, 0")]
    [InlineData("0, -180.5")]
    [InlineData("40 60 N, 105 W")]
    [InlineData("40 10 60 N, 105 W")]
    public void RefusesWhatIsOffTheGlobe(string text) =>
        Assert.Equal(CoordinateParseError.OutOfRange, CoordinateParser.TryParse(text, out _, out _));
}
