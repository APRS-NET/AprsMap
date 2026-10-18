// SPDX-License-Identifier: MIT
namespace AprsMap.Contracts;

/// <summary>A gateway sign-in (docs/features/F2.9-operator-sign-in.md, behavior 4), posted to <see cref="AuthPaths.SignIn"/>.</summary>
/// <param name="Name">The operator's name.</param>
/// <param name="Password">The password; sent over HTTPS only, never stored or logged.</param>
public sealed record SignInRequest(string Name, string Password);

/// <summary>Who this browser is on the gateway (behaviors 4–6), from <see cref="AuthPaths.Me"/>.</summary>
/// <param name="Operator">Signed in as an operator; otherwise a viewer.</param>
/// <param name="Name">The operator's name when signed in.</param>
/// <param name="ExpiresAt">When the session ends unless it's used again, when signed in.</param>
/// <param name="SignInOffered">
/// Whether this gateway offers sign-in here: it has an operator set up, and this address may sign in (behavior 8).
/// </param>
/// <param name="FailedSignIns">Failed sign-ins in the last day, shown to a signed-in operator (behavior 10).</param>
/// <param name="NeedsHttps">
/// An operator is set up but this page came over plain HTTP from another machine, so sign-in isn't offered: the dialog
/// says why (behavior 8.1).
/// </param>
public sealed record AuthInfo(
    bool Operator,
    string? Name = null,
    DateTimeOffset? ExpiresAt = null,
    bool SignInOffered = false,
    int FailedSignIns = 0,
    bool NeedsHttps = false
)
{
    /// <summary>A viewer on a gateway that offers no sign-in.</summary>
    public static AuthInfo Viewer { get; } = new(false);
}

/// <summary>The gateway's sign-in endpoints, relative to its base (not the hub).</summary>
public static class AuthPaths
{
    /// <summary><c>POST</c> a <see cref="SignInRequest"/>: 204 and the session cookie, or an <see cref="EngineError"/>.</summary>
    public const string SignIn = "auth/sign-in";

    /// <summary><c>POST</c>: ends this browser's session on the gateway.</summary>
    public const string SignOut = "auth/sign-out";

    /// <summary><c>GET</c>: this browser's <see cref="AuthInfo"/>.</summary>
    public const string Me = "auth/me";
}
