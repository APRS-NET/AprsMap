// SPDX-License-Identifier: AGPL-3.0-only
using System.Diagnostics.Metrics;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using AprsMap.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace AprsMap.Gateway;

/// <summary>Marks a hub method that can transmit: each call by an operator is logged with their name (F2.9 behavior 15).</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class TransmitsAttribute : Attribute;

/// <summary>
/// Operator sign-in (docs/features/F2.9-operator-sign-in.md): the session cookie, <c>/auth/sign-in</c>,
/// <c>/auth/sign-out</c> and <c>/auth/me</c>, the origin check, and the hub filter that gives a connection its role.
/// </summary>
internal static partial class OperatorSignIn
{
    /// <summary>The session cookie: <c>__Host-</c> prefixed, so it's Secure, for this host only and the whole path.</summary>
    public static readonly string CookieName = "__Host-" + Brand.Slug + "-session";

    /// <summary>The claim holding the session's id.</summary>
    public const string SessionClaim = "sid";

    /// <summary>Registers the operators, sessions, limits and cookie; <paramref name="folder"/> is the data folder.</summary>
    public static void AddOperatorSignIn(this IServiceCollection services, string folder)
    {
        services.AddSingleton(sp => new OperatorStore(
            folder,
            sp.GetRequiredService<IOptionsMonitor<GatewayOptions>>()
        ));
        services.AddSingleton<OperatorSessions>();
        services.AddSingleton<SignInLimiter>();
        services.AddSingleton<SignInAudit>();
        services.AddSingleton<OperatorFilter>();
        services.TryAddTimeProvider();
        // The cookie's keys outlive a restart, in the data folder (behavior 5).
        services
            .AddDataProtection()
            .SetApplicationName(Brand.Slug)
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(folder, "keys")));
        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = CookieName;
                o.Cookie.HttpOnly = true;
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.Path = "/"; // a __Host- cookie must have the root path
                o.ExpireTimeSpan = OperatorSessions.MaxAge;
                o.SlidingExpiration = false; // the gateway times out unused sessions itself
                o.Events.OnValidatePrincipal = context =>
                {
                    var sessions = context.HttpContext.RequestServices.GetRequiredService<OperatorSessions>();
                    if (SessionId(context.Principal) is not { } id || sessions.Use(id) is null)
                    {
                        context.RejectPrincipal();
                    }
                    return Task.CompletedTask;
                };
                o.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                o.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });
    }

    private static void TryAddTimeProvider(this IServiceCollection services)
    {
        if (!services.Any(s => s.ServiceType == typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }

    /// <summary>The session id in <paramref name="user"/>'s cookie, if any.</summary>
    public static string? SessionId(ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true ? user.FindFirstValue(SessionClaim) : null;

    /// <summary>
    /// Refuses requests to the hub and to <c>/auth</c> from another site's pages (behavior 16), then reads the cookie.
    /// </summary>
    public static void UseOperatorSignIn(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptionsMonitor<GatewayOptions>>();
        app.Use(
            async (context, next) =>
            {
                var path = context.Request.Path;
                if (
                    (path.StartsWithSegments(EngineHub.Path) || path.StartsWithSegments("/auth"))
                    && context.Request.Headers.Origin is [{ } origin]
                    && !IsAllowedOrigin(context.Request, origin, options.CurrentValue)
                )
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                await next(context);
            }
        );
        app.UseAuthentication();
        if (app.Services.GetRequiredService<OperatorStore>().Any && options.CurrentValue.AllowSettingsChanges)
        {
            LogSettingsChangesIgnored(
                app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AprsMap.Gateway")
            );
        }
    }

    /// <summary>The gateway's own origin, or one listed in <see cref="GatewayOptions.AllowedOrigins"/>.</summary>
    internal static bool IsAllowedOrigin(HttpRequest request, string origin, GatewayOptions options) =>
        string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase)
        || options.AllowedOrigins.Any(o => string.Equals(o.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase));

    /// <summary>Over HTTPS (directly or through a trusted proxy), or from the gateway's own host (behavior 8.1).</summary>
    internal static bool IsSecureEnough(HttpContext context) =>
        context.Request.IsHttps || context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);

    /// <summary>Maps <c>/auth/sign-in</c>, <c>/auth/sign-out</c> and <c>/auth/me</c>.</summary>
    public static void MapOperatorSignIn(this WebApplication app)
    {
        app.MapPost("/" + AuthPaths.SignIn, SignInAsync);
        app.MapPost("/" + AuthPaths.SignOut, SignOutAsync);
        app.MapGet("/" + AuthPaths.Me, Me);
    }

    private static async Task<IResult> SignInAsync(
        HttpContext context,
        OperatorStore operators,
        OperatorSessions sessions,
        SignInLimiter limiter,
        SignInAudit audit
    )
    {
        if (!operators.Any)
        {
            return Refuse(StatusCodes.Status404NotFound, EngineErrorCodes.AuthNotOfferedHere, "No operator is set up.");
        }
        if (!IsSecureEnough(context))
        {
            return Refuse(
                StatusCodes.Status403Forbidden,
                EngineErrorCodes.AuthNeedsHttps,
                "Signing in needs HTTPS, or the gateway's own host."
            );
        }
        SignInRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(EngineJsonContext.Default.SignInRequest);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            request = null;
        }
        if (request is not { Name: { } typed, Password: { } password })
        {
            return Results.BadRequest();
        }
        var name = typed.Trim();
        var address = Viewers.AddressOf(context);
        if (limiter.Wait(address, name) is { } wait)
        {
            var minutes = (int)Math.Ceiling(wait.TotalMinutes);
            audit.Waited(name, address, minutes);
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(
                System.Globalization.CultureInfo.InvariantCulture
            );
            return Refuse(
                StatusCodes.Status429TooManyRequests,
                EngineErrorCodes.AuthTooManyTries,
                $"Too many tries. Wait {minutes} min and try again.",
                new Dictionary<string, string>
                {
                    [EngineErrorArgs.Minutes] = minutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }
            );
        }
        var op = operators.Find(name);
        // An unknown name is checked against a decoy, so both answers take the same time (behavior 7).
        if (!PasswordHashing.Verify(password, op?.PasswordHash) || op is null)
        {
            limiter.Failed(address, name);
            audit.Failed(name, address);
            return Refuse(
                StatusCodes.Status401Unauthorized,
                EngineErrorCodes.AuthWrongCredentials,
                "That name and password don't match."
            );
        }
        var session = sessions.Start(op);
        await context.SignInAsync(
            new ClaimsPrincipal(
                new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, op.Name), new Claim(SessionClaim, session.Id)],
                    CookieAuthenticationDefaults.AuthenticationScheme
                )
            ),
            new AuthenticationProperties { IsPersistent = true }
        );
        audit.SignedIn(op.Name, address);
        return Results.NoContent();
    }

    private static async Task<IResult> SignOutAsync(HttpContext context, OperatorSessions sessions, SignInAudit audit)
    {
        if (SessionId(context.User) is { } id)
        {
            sessions.End(id);
            audit.SignedOut(context.User.Identity?.Name ?? string.Empty, Viewers.AddressOf(context));
        }
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.NoContent();
    }

    private static IResult Me(
        HttpContext context,
        OperatorStore operators,
        OperatorSessions sessions,
        SignInLimiter limiter
    )
    {
        var offered = operators.Any && IsSecureEnough(context);
        var session = SessionId(context.User) is { } id ? sessions.Use(id) : null;
        var info = session is null
            ? new AuthInfo(false, SignInOffered: offered)
            : new AuthInfo(true, session.Name, session.ExpiresAt, offered, limiter.FailedToday);
        return Results.Json(info, EngineJsonContext.Default.AuthInfo);
    }

    private static IResult Refuse(int status, string code, string message, Dictionary<string, string>? args = null) =>
        Results.Json(new EngineError(code, message, args), EngineJsonContext.Default.EngineError, statusCode: status);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Gateway:AllowSettingsChanges is ignored: an operator is set up, so signing in decides who may change the station"
    )]
    private static partial void LogSettingsChangesIgnored(ILogger logger);
}

/// <summary>
/// Logs and counts each sign-in, failure, wait and sign-out with the time, address and name tried, never the password
/// (behavior 10), and each operator's transmit call (behavior 15).
/// </summary>
internal sealed partial class SignInAudit(ILogger<SignInAudit> logger) : IDisposable
{
    private readonly Meter _meter = new(Viewers.MeterName);
    private Counter<long>? _signIns;

    private Counter<long> SignIns =>
        _signIns ??= _meter.CreateCounter<long>(
            "aprsmap.gateway.sign_ins",
            unit: "{sign-in}",
            description: "Operator sign-ins by outcome."
        );

    public void SignedIn(string name, string address)
    {
        SignIns.Add(1, new KeyValuePair<string, object?>("outcome", "signed-in"));
        LogSignedIn(name, address);
    }

    public void Failed(string name, string address)
    {
        SignIns.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
        LogFailed(name, address);
    }

    public void Waited(string name, string address, int minutes)
    {
        SignIns.Add(1, new KeyValuePair<string, object?>("outcome", "waited"));
        LogWaited(name, address, minutes);
    }

    public void SignedOut(string name, string address) => LogSignedOut(name, address);

    public void Transmitted(string name, string method, IReadOnlyList<object?> arguments)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            var what = string.Join(", ", arguments.Where(a => a is not CancellationToken));
            LogTransmitted(name, method, what);
        }
    }

    public void Dispose() => _meter.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "Operator {Name} signed in from {Address}")]
    private partial void LogSignedIn(string name, string address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed sign-in for {Name} from {Address}")]
    private partial void LogFailed(string name, string address);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Sign-in for {Name} from {Address} refused: too many tries, {Minutes} min to wait"
    )]
    private partial void LogWaited(string name, string address, int minutes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Operator {Name} signed out from {Address}")]
    private partial void LogSignedOut(string name, string address);

    [LoggerMessage(Level = LogLevel.Information, Message = "Operator {Name}: {Method}({Arguments})")]
    private partial void LogTransmitted(string name, string method, string arguments);
}

/// <summary>
/// Gives each hub call its role from the session its connection was opened with (behavior 13): checked on every call,
/// and a connection whose session has ended is closed. Logs every transmitting call by an operator (behavior 15).
/// </summary>
internal sealed class OperatorFilter(OperatorSessions sessions, SignInAudit audit) : IHubFilter
{
    /// <summary>Where a call's operator name is kept while it runs; absent for a viewer.</summary>
    public const string OperatorKey = "aprsmap.operator";

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next
    )
    {
        var context = invocationContext.Context;
        context.Items.Remove(OperatorKey);
        if (OperatorSignIn.SessionId(context.User) is { } id)
        {
            if (sessions.Use(id) is not { } session)
            {
                context.Abort(); // signed out, expired or the password changed: the browser connects again as a viewer
                throw new HubException(
                    EngineHub.EncodeError(new EngineError(EngineErrorCodes.OperatorOnly, EngineHub.OperatorOnlyMessage))
                );
            }
            context.Items[OperatorKey] = session.Name;
            if (invocationContext.HubMethod.GetCustomAttribute<TransmitsAttribute>() is not null)
            {
                audit.Transmitted(session.Name, invocationContext.HubMethodName, invocationContext.HubMethodArguments);
            }
        }
        return await next(invocationContext);
    }

    public Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        if (OperatorSignIn.SessionId(context.Context.User) is { } id && sessions.Use(id) is not null)
        {
            sessions.Attach(id, context.Context);
        }
        return next(context);
    }

    public Task OnDisconnectedAsync(
        HubLifetimeContext context,
        Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next
    )
    {
        sessions.Detach(context.Context.ConnectionId);
        return next(context, exception);
    }
}
