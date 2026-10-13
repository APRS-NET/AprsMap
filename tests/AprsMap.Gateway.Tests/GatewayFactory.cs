// SPDX-License-Identifier: AGPL-3.0-only
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AprsMap.Gateway.Tests;

/// <summary>Hosts the gateway in memory with the APRS-IS connection switched off.</summary>
public sealed class GatewayFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("Engine:Enabled", "false").UseEnvironment("Testing");
}
