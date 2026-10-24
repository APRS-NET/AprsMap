// SPDX-License-Identifier: AGPL-3.0-only
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Primitives;

namespace AprsMap.Server;

/// <summary>
/// The gateway's HTTP pipeline for a real network (docs/features/F1.14-gateway.md): a path base and trusted proxies,
/// security headers on every response, and the browser app's files served precompressed with cache lifetimes that let
/// an upgrade reach browsers on their next load.
/// </summary>
internal static partial class GatewayHttp
{
    /// <summary>How long a file whose name carries its content hash may be cached: a year, never revalidated.</summary>
    private const string Immutable = "public, max-age=31536000, immutable";

    /// <summary>Files without a content hash (index.html, main.js, the boot scripts) are checked on every load.</summary>
    private const string Revalidate = "no-cache";

    /// <summary>Reads the gateway's options before the app is built, for the parts of the pipeline they shape.</summary>
    public static GatewayOptions Options(IConfiguration configuration) =>
        configuration.GetSection(GatewayOptions.SectionName).Get<GatewayOptions>() ?? new GatewayOptions();

    /// <summary>Trusts forwarded headers from the configured proxies only (behavior 2).</summary>
    public static void AddGatewayProxies(this IServiceCollection services, GatewayOptions options)
    {
        services.Configure<ForwardedHeadersOptions>(forwarded =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // Not the loopback defaults: only what the operator names is trusted.
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();
            foreach (var entry in options.KnownProxies)
            {
                if (entry.Contains('/', StringComparison.Ordinal))
                {
                    forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(entry));
                }
                else
                {
                    forwarded.KnownProxies.Add(IPAddress.Parse(entry));
                }
            }
        });
    }

    /// <summary>
    /// The front of the pipeline: forwarded headers, the path base (anything outside it is not found; behavior 1) and
    /// the security headers (behavior 10).
    /// </summary>
    public static void UseGatewayFront(this WebApplication app, GatewayOptions options)
    {
        if (options.KnownProxies.Count > 0)
        {
            app.UseForwardedHeaders();
        }
        if (!string.IsNullOrWhiteSpace(options.PathBase))
        {
            var pathBase = new PathString("/" + options.PathBase.Trim().Trim('/'));
            app.UsePathBase(pathBase);
            app.Use(
                (context, next) =>
                {
                    if (!context.Request.PathBase.StartsWithSegments(pathBase))
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return Task.CompletedTask;
                    }
                    return next(context);
                }
            );
        }
        var policy = ContentSecurityPolicy(options.FrameAncestors);
        app.Use(
            (context, next) =>
            {
                var headers = context.Response.Headers;
                headers.ContentSecurityPolicy = policy;
                headers.XContentTypeOptions = "nosniff";
                headers["Referrer-Policy"] = "same-origin";
                return next(context);
            }
        );
    }

    /// <summary>
    /// Only the gateway's own origin, for scripts (with WebAssembly), connections, images, fonts and workers. Styles
    /// may be inline: index.html carries its loading screen's style block.
    /// </summary>
    internal static string ContentSecurityPolicy(string frameAncestors) =>
        "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; connect-src 'self'; img-src 'self' data: blob:; "
        + "font-src 'self' data:; style-src 'self' 'unsafe-inline'; worker-src 'self' blob:; object-src 'none'; "
        + $"base-uri 'self'; frame-ancestors {frameAncestors}";

    /// <summary>
    /// The browser app's files (behavior 9): a <c>.br</c> or <c>.gz</c> copy when the browser takes it, a year's cache
    /// for files named by their content hash, and revalidation for the rest.
    /// </summary>
    public static void UseBrowserApp(this WebApplication app)
    {
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".dat"] = "application/octet-stream";
        // Known, so the precompressed copies are served; each gets its original's type below.
        contentTypes.Mappings[".br"] = "application/octet-stream";
        contentTypes.Mappings[".gz"] = "application/octet-stream";
        var files = app.Environment.WebRootFileProvider;
        app.UseDefaultFiles();
        app.Use(
            (context, next) =>
            {
                Precompressed(context, files);
                return next(context);
            }
        );
        app.UseStaticFiles(
            new StaticFileOptions
            {
                ContentTypeProvider = contentTypes,
                OnPrepareResponse = file =>
                {
                    var name = file.File.Name;
                    var headers = file.Context.Response.Headers;
                    if (Compressed(name) is { } encoding)
                    {
                        var original = name[..^3]; // ".br" or ".gz"
                        headers.ContentEncoding = encoding;
                        headers.ContentType = contentTypes.TryGetContentType(original, out var type)
                            ? type
                            : "application/octet-stream";
                        name = original;
                    }
                    headers.Vary = "Accept-Encoding";
                    headers.CacheControl = Fingerprint().IsMatch(name) ? Immutable : Revalidate;
                },
            }
        );
    }

    /// <summary>Serves <c>file.br</c> (or <c>.gz</c>) for <c>file</c> when the browser accepts it and the copy exists.</summary>
    private static void Precompressed(HttpContext context, Microsoft.Extensions.FileProviders.IFileProvider files)
    {
        var path = context.Request.Path.Value;
        if (path is null || path.EndsWith('/') || !HttpMethods.IsGet(context.Request.Method))
        {
            return;
        }
        var accepts = context.Request.Headers.AcceptEncoding;
        foreach (var (encoding, suffix) in new[] { ("br", ".br"), ("gzip", ".gz") })
        {
            if (Accepts(accepts, encoding) && files.GetFileInfo(path + suffix).Exists)
            {
                context.Request.Path = path + suffix;
                return;
            }
        }
    }

    private static bool Accepts(StringValues header, string encoding) =>
        header.Any(v => v is not null && v.Split(',').Any(part => part.Trim().Split(';')[0] == encoding));

    private static string? Compressed(string name) =>
        name.EndsWith(".br", StringComparison.Ordinal) ? "br"
        : name.EndsWith(".gz", StringComparison.Ordinal) ? "gzip"
        : null;

    /// <summary>A name with a content hash before its extension, as the .NET publish writes them: <c>name.0123456789.wasm</c>.</summary>
    [GeneratedRegex(@"\.[a-z0-9]{10}\.[a-z]+$")]
    private static partial Regex Fingerprint();
}
