// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.Contracts;
using AprsMap.UI.Localization;

namespace AprsMap.UI.ViewModels;

/// <summary>The APRS-IS connection in words, for the status bar and About (docs/features/F1.1-aprs-is-connection.md).</summary>
internal static class ConnectionText
{
    /// <summary>The status bar's text. A reconnect counts down to <see cref="EngineStatus.RetryAt"/> from <paramref name="now"/>.</summary>
    public static string Summary(Localizer text, EngineStatus status, DateTimeOffset now)
    {
        var server = status.Server ?? string.Empty;
        return status.Connection switch
        {
            ConnectionState.Connected => text.Format(
                "Status.Connected",
                new Dictionary<string, object?> { ["server"] = server, ["verified"] = status.Verified ? "yes" : "no" }
            ),
            ConnectionState.Connecting => text.Format(
                "Status.Connecting",
                new Dictionary<string, object?> { ["server"] = server }
            ),
            ConnectionState.Reconnecting => Reconnecting(text, status, now),
            ConnectionState.Refused => text.Format(
                "Status.Refused",
                new Dictionary<string, object?>
                {
                    ["reason"] = status.ConnectionError?.Arg(EngineErrorArgs.Reason) ?? string.Empty,
                }
            ),
            ConnectionState.NotConfigured => text["Status.NotConfigured"],
            _ => text["Status.Disconnected"],
        };
    }

    /// <summary>
    /// Whether the login is verified and how often the connection came back, for the tooltip and About; null when there
    /// is no connection to describe.
    /// </summary>
    public static string? Details(Localizer text, EngineStatus status)
    {
        if (status.Connection is not (ConnectionState.Connected or ConnectionState.Reconnecting))
        {
            return null;
        }
        var details = text.Format(
            "Status.Details",
            new Dictionary<string, object?>
            {
                ["verified"] = status.Verified ? "yes" : "no",
                ["reconnects"] = status.Reconnects,
            }
        );
        // The filter in use, for anyone checking what APRS-IS sends (F1.2 behavior 7).
        return string.IsNullOrEmpty(status.Filter)
            ? details
            : details
                + Environment.NewLine
                + text.Format("Status.Filter", new Dictionary<string, object?> { ["filter"] = status.Filter });
    }

    /// <summary>Whether the status bar text changes by itself, second by second (the reconnect countdown).</summary>
    public static bool IsCountingDown(EngineStatus? status) =>
        status is { Connection: ConnectionState.Reconnecting, RetryAt: not null };

    private static string Reconnecting(Localizer text, EngineStatus status, DateTimeOffset now)
    {
        var reason = status.ConnectionError is { } error ? text.Format(error) : string.Empty;
        var seconds = status.RetryAt is { } at ? (int)Math.Ceiling((at - now).TotalSeconds) : 0;
        return seconds > 0
            ? text.Format(
                "Status.Reconnecting",
                new Dictionary<string, object?> { ["seconds"] = seconds, ["reason"] = reason }
            )
            : text.Format("Status.Reconnecting.Now", new Dictionary<string, object?> { ["reason"] = reason });
    }
}
