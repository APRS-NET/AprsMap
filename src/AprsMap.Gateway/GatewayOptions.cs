// SPDX-License-Identifier: AGPL-3.0-only
namespace AprsMap.Gateway;

/// <summary>Gateway settings, bound from the <c>Gateway</c> configuration section.</summary>
internal sealed class GatewayOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Gateway";

    /// <summary>
    /// Lets any browser change the station's settings and switch profiles, and see its passcodes. Off by default:
    /// there is no operator sign-in yet (scope F2.9), and a profile switch changes the station for every viewer. Turn
    /// it on only on a network where everyone may act as the station's operator.
    /// </summary>
    public bool AllowSettingsChanges { get; set; }

    /// <summary>
    /// A path the gateway is served under, such as <c>/aprs</c>, for a reverse proxy that maps a path to it (F1.14
    /// behavior 1). Empty serves from the root.
    /// </summary>
    public string? PathBase { get; set; }

    /// <summary>
    /// Proxies trusted to set <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c>: addresses (<c>10.0.0.2</c>) or
    /// networks (<c>10.0.0.0/8</c>). With none, forwarded headers are ignored (F1.14 behavior 2).
    /// </summary>
    public IList<string> KnownProxies { get; } = [];

    /// <summary>Who may show the app in a frame: the CSP <c>frame-ancestors</c> sources (F1.14 behavior 10).</summary>
    public string FrameAncestors { get; set; } = "'self'";
}
