// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;

namespace AprsMap.UI.Tests;

public class RangeTextTests
{
    [Theory]
    [InlineData(100.0, 45, DistanceUnit.Kilometres, "100 km NE 045°")]
    [InlineData(100.0, 45, DistanceUnit.Miles, "62 mi NE 045°")]
    [InlineData(100.0, 45, DistanceUnit.NauticalMiles, "54 nmi NE 045°")]
    [InlineData(4.24, 359, DistanceUnit.Kilometres, "4.2 km N 359°")]
    [InlineData(1234.0, 191, DistanceUnit.Kilometres, "1,234 km S 191°")]
    [InlineData(20.0, 202, DistanceUnit.Kilometres, "20 km SSW 202°")]
    public void DistanceAndBearingAreWrittenInTheViewersUnit(
        double km,
        int bearing,
        DistanceUnit unit,
        string expected
    ) => Assert.Equal(expected, RangeText.Format(km, bearing, unit, Localizer.Instance));

    [Fact]
    public void WithoutMyLocationNothingIsWritten()
    {
        Assert.Null(RangeText.Format(null, null, DistanceUnit.Kilometres, Localizer.Instance));
        Assert.Null(RangeText.Format(100, null, DistanceUnit.Kilometres, Localizer.Instance));
    }
}
