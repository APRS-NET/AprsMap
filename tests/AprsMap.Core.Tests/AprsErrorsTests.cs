// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsMap.Core;
using AprsNet.Packets;

namespace AprsMap.Core.Tests;

public class AprsErrorsTests
{
    [Theory]
    [InlineData("CallsignInvalidLength", "callsign-invalid-length")]
    [InlineData("ServerPortOutOfRange", "server-port-out-of-range")]
    [InlineData("PathTooLong", "path-too-long")]
    public void CodesBecomeKebabCase(string name, string expected) => Assert.Equal(expected, AprsErrors.Kebab(name));

    [Fact]
    public void EveryAprsNetCodeMapsToItsOwnEngineCode()
    {
        var codes = Enum.GetValues<AprsValidationErrorCode>()
            .Select(c => AprsErrors.From(new AprsValidationException(c, "English.", "field", "value")).Code)
            .ToList();

        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, c => Assert.StartsWith(EngineErrorCodes.AprsPrefix, c, StringComparison.Ordinal));
        Assert.All(codes, c => Assert.Equal(EngineErrorKind.Argument, EngineErrorCodes.KindOf(c)));
    }

    [Fact]
    public void TheValueAndPositionComeAlong()
    {
        var exception = Assert.Throws<AprsValidationException>(() => Callsign.Parse("N0 CALL"));

        var error = AprsErrors.From(exception, "Home");

        Assert.Equal("aprs.callsign-invalid-character", error.Code);
        Assert.Equal("Home", error.Arg(EngineErrorArgs.Profile));
        Assert.Equal("N0 CALL", error.Arg(EngineErrorArgs.Value));
        Assert.Equal("2", error.Arg(EngineErrorArgs.Index));
        Assert.Equal(" ", error.Arg(EngineErrorArgs.Character));
        Assert.StartsWith("Profile 'Home': ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingDetailsAreLeftOut()
    {
        var error = AprsErrors.From(
            new AprsValidationException(AprsValidationErrorCode.ServerHostEmpty, "No host.", null, null)
        );

        Assert.Equal("aprs.server-host-empty", error.Code);
        Assert.Equal("No host.", error.Message);
        Assert.NotNull(error.Args);
        Assert.Empty(error.Args);
    }
}
