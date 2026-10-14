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
}
