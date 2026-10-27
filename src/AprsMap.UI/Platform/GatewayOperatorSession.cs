// SPDX-License-Identifier: AGPL-3.0-only
using System.Net;
using System.Net.Http.Json;
using AprsMap.Contracts;

namespace AprsMap.UI.Platform;

/// <summary>
/// The gateway's <c>/auth</c> endpoints (docs/features/F2.9-operator-sign-in.md). In the browser, from the page's own
/// origin, so the browser keeps the session cookie and sends it with every request and the hub's connection; on the
/// desktop as the gateway's client, with the API key in <paramref name="gateway"/>'s headers
/// (docs/features/F1.39-desktop-gateway-client.md), where <paramref name="passwordSignIn"/> is off: its hub connection
/// carries the key, not a browser's session cookie, so signing in with a password isn't offered.
/// </summary>
public sealed class GatewayOperatorSession(HttpClient gateway, bool passwordSignIn = true) : IOperatorSession
{
    /// <summary>
    /// Who this app is on the gateway. A refused API key is a viewer with <see cref="AuthInfo.ApiKey"/> set, so the app
    /// can say why it's only watching (F1.38 behavior 7).
    /// </summary>
    public async Task<AuthInfo> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await gateway.GetAsync(new Uri(AuthPaths.Me, UriKind.Relative), cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new AuthInfo(false, ApiKey: true);
            }
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                // Over plain HTTP from another computer the gateway won't take the key at all (#414).
                return new AuthInfo(false, ApiKey: true, NeedsHttps: true);
            }
            response.EnsureSuccessStatusCode();
            var info =
                await response.Content.ReadFromJsonAsync(EngineJsonContext.Default.AuthInfo, cancellationToken)
                ?? AuthInfo.Viewer;
            return passwordSignIn ? info : info with { SignInOffered = false, NeedsHttps = false };
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            return AuthInfo.Viewer; // an older gateway without sign-in
        }
    }

    /// <inheritdoc />
    public async Task SignInAsync(SignInRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await gateway.PostAsJsonAsync(
            AuthPaths.SignIn,
            request,
            EngineJsonContext.Default.SignInRequest,
            cancellationToken
        );
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return;
        }
        EngineError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync(EngineJsonContext.Default.EngineError, cancellationToken);
        }
        catch (System.Text.Json.JsonException) { }
        throw EngineErrors.ToException(
            error
                ?? new EngineError(
                    EngineErrorCodes.AuthNotOfferedHere,
                    $"The gateway answered {(int)response.StatusCode}."
                )
        );
    }

    /// <inheritdoc />
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        using var response = await gateway.PostAsync(AuthPaths.SignOut, content: null, cancellationToken);
    }
}
