// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;

namespace AprsMap.UI.Platform;

/// <summary>
/// Who this viewer is on a gateway (docs/features/F2.9-operator-sign-in.md, behaviors 4–6): the browser's goes to the
/// gateway's <c>/auth</c> endpoints. The desktop has none: whoever runs it is the operator.
/// </summary>
public interface IOperatorSession
{
    /// <summary>Whether this browser is signed in, and whether sign-in is offered here.</summary>
    Task<AuthInfo> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Signs in; the gateway sets the session cookie.</summary>
    /// <exception cref="EngineAccessException">Refused: the error's <c>auth.*</c> code says why.</exception>
    Task SignInAsync(SignInRequest request, CancellationToken cancellationToken = default);

    /// <summary>Ends this browser's session on the gateway.</summary>
    Task SignOutAsync(CancellationToken cancellationToken = default);
}
