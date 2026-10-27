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
/// <param name="ApiKey">
/// An app's API key (docs/features/F1.38-gateway-api-key.md, behavior 8): with <paramref name="Operator"/>, the gateway
/// let it in as the operator "API key", which has no session to end; without, the gateway refused it.
/// </param>
/// <param name="NoOperator">
/// This is the gateway's operator port, but no operator is set up yet, so sign-in can't be offered: the dialog says how
/// to set one up on the gateway's computer (#413).
/// </param>
public sealed record AuthInfo(
    bool Operator,
    string? Name = null,
    DateTimeOffset? ExpiresAt = null,
    bool SignInOffered = false,
    int FailedSignIns = 0,
    bool NeedsHttps = false,
    bool ApiKey = false,
    bool NoOperator = false
)
{
    /// <summary>A viewer on a gateway that offers no sign-in.</summary>
    public static AuthInfo Viewer { get; } = new(false);
}

/// <summary>Whether the gateway has an API key (docs/features/F1.38-gateway-api-key.md, behavior 6).</summary>
/// <param name="Set">A key is saved, so apps with it are let in as the operator.</param>
/// <param name="SetAt">When it was saved.</param>
public sealed record ApiKeyStatus(bool Set, DateTimeOffset? SetAt = null)
{
    /// <summary>No key: API access is off.</summary>
    public static ApiKeyStatus None { get; } = new(false);
}

/// <summary>The gateway's API key (docs/features/F1.38-gateway-api-key.md).</summary>
public static class ApiKeys
{
    /// <summary>The fewest characters a key typed or pasted may have (behavior 1).</summary>
    public const int MinLength = 32;

    /// <summary>The longest a key may be.</summary>
    public const int MaxLength = 256;

    /// <summary>The scheme the key is sent with, in the <c>Authorization</c> header (behavior 5).</summary>
    public const string Scheme = "Bearer";
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
