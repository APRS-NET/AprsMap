// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using AprsNet.AprsIsTcpClient;

namespace AprsMap.Core;

/// <summary>Turns what ended or stopped an APRS-IS connection into an <see cref="EngineError"/> the UI translates.</summary>
internal static class AprsIsErrors
{
    /// <param name="error">What APRS.NET reported; null when the server closed the connection cleanly.</param>
    /// <param name="wasConnected">Whether the connection had logged in, so the failure was a drop and not a failed attempt.</param>
    public static EngineError From(Exception? error, bool wasConnected) =>
        error switch
        {
            LoginRejectedException rejected => new EngineError(
                EngineErrorCodes.AprsIsLoginRefused,
                rejected.Message,
                new Dictionary<string, string> { [EngineErrorArgs.Reason] = rejected.ServerMessage }
            ),
            KissTncLoginException tnc => new EngineError(EngineErrorCodes.AprsIsKissTnc, tnc.Message),
            _ when !wasConnected => new EngineError(
                EngineErrorCodes.AprsIsUnreachable,
                error?.Message ?? "The APRS-IS server couldn't be reached."
            ),
            null => new EngineError(EngineErrorCodes.AprsIsServerClosed, "The APRS-IS server closed the connection."),
            TimeoutException timeout => new EngineError(EngineErrorCodes.AprsIsIdle, timeout.Message),
            _ => new EngineError(EngineErrorCodes.AprsIsLinkFailed, error.Message),
        };
}
