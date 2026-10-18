// SPDX-License-Identifier: AGPL-3.0-only
using System.Net;
using System.Net.Http.Json;
using AprsMap.Contracts;
using AprsMap.UI.Platform;

namespace AprsMap.Browser;

/// <summary>
/// The gateway's <c>/auth</c> endpoints (docs/features/F2.9-operator-sign-in.md), from the page's own origin, so the
/// browser keeps the session cookie and sends it with every request and the hub's connection.
/// </summary>
internal sealed class BrowserOperatorSession(HttpClient gateway) : IOperatorSession
{
    public async Task<AuthInfo> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await gateway.GetFromJsonAsync(AuthPaths.Me, EngineJsonContext.Default.AuthInfo, cancellationToken)
                ?? AuthInfo.Viewer;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            return AuthInfo.Viewer; // an older gateway without sign-in
        }
    }

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

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        using var response = await gateway.PostAsync(AuthPaths.SignOut, content: null, cancellationToken);
    }
}
