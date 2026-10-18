// SPDX-License-Identifier: AGPL-3.0-only
namespace AprsMap.Gateway;

/// <summary>Gateway settings, bound from the <c>Gateway</c> configuration section.</summary>
internal sealed class GatewayOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Gateway";

    /// <summary>
    /// Lets any browser change the station's settings and switch profiles, and see its passcodes, on a gateway with no
    /// operator set up. Off by default: a profile switch changes the station for every viewer. Turn it on only on a
    /// network where everyone may act as the station's operator. Once an operator is set up it's ignored: signing in
    /// decides (docs/features/F2.9-operator-sign-in.md, behavior 17).
    /// </summary>
    public bool AllowSettingsChanges { get; set; }

    /// <summary>
    /// Operators set up in configuration, each a name and a password hash from <c>operator add</c> (F2.9 behavior 1).
    /// Most gateways keep them in <c>operators.json</c> in the data folder instead.
    /// </summary>
    public IList<OperatorOptions> Operators { get; } = [];

    /// <summary>
    /// Other origins whose pages may call the hub and <c>/auth</c> (F2.9 behavior 16), such as
    /// <c>https://aprs.example.org</c>; the gateway's own origin always may.
    /// </summary>
    public IList<string> AllowedOrigins { get; } = [];

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

    /// <summary>
    /// Lets each browser's map view widen the station's APRS-IS filter (F1.2 behavior 9): up to 8 views, each capped at
    /// 500 km. Off by default, so anonymous viewers can't steer what the station receives; followed stations count
    /// either way.
    /// </summary>
    public bool FollowViewers { get; set; }

    /// <summary>
    /// A second port where an operator signs in (docs/features/F1.26-public-console.md, behaviors 1–3), such as 8081,
    /// published on the host's loopback only. With it set, the main port never offers sign-in and ignores a session
    /// cookie; null for none, where sign-in is offered on the main port.
    /// </summary>
    public int? OperatorPort { get; set; }

    /// <summary>The page's title, such as "What N0CALL's TNC hears" (F1.26 behavior 10); null for none.</summary>
    public string? Title { get; set; }

    /// <summary>The most browsers connected at once (F1.14 behavior 3).</summary>
    public int MaxViewers { get; set; } = 100;

    /// <summary>The most browsers connected at once from one address.</summary>
    public int MaxViewersPerAddress { get; set; } = 5;

    /// <summary>Hub calls a viewer may make per second, on average (F1.14 behavior 4).</summary>
    public int CallsPerSecond { get; set; } = 20;

    /// <summary>Hub calls a viewer may make at once, above the average.</summary>
    public int CallBurst { get; set; } = 40;

    /// <summary>Who may show the app in a frame: the CSP <c>frame-ancestors</c> sources (F1.14 behavior 10).</summary>
    public string FrameAncestors { get; set; } = "'self'";
}
