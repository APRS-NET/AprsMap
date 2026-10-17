// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>
/// The login an unlicensed user's profile listens with (docs/features/F2.1-own-station.md, behavior 4): <c>SWL</c> and six
/// digits, such as <c>SWL482913</c>. It ends in digits, so it's never an amateur callsign, and it never transmits. The
/// engine makes them with APRS.NET's <c>AprsIsLogin</c>; this is the same rule for the UI, which doesn't reference APRS.NET.
/// </summary>
public static class ListenerId
{
    /// <summary>The prefix, for "short-wave listener".</summary>
    public const string Prefix = "SWL";

    /// <summary>Whether <paramref name="login"/> is a listener ID rather than a callsign.</summary>
    public static bool Is(string? login) =>
        login is { Length: 9 }
        && login.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
        && login.AsSpan(Prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;
}
