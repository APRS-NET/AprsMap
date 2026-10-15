// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text;
using AprsMap.Contracts;
using AprsNet.Packets;

namespace AprsMap.Core;

/// <summary>Turns APRS.NET's coded validation errors (gap G27) into <see cref="EngineError"/>s the UI can translate.</summary>
internal static class AprsErrors
{
    /// <summary>
    /// The error for <paramref name="exception"/>: <c>aprs.</c> plus the code in kebab case (for example
    /// <c>aprs.callsign-invalid-character</c>), with the field, value, character position and profile as arguments.
    /// </summary>
    public static EngineError From(AprsValidationException exception, string? profile = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        Add(args, EngineErrorArgs.Profile, profile);
        Add(args, EngineErrorArgs.Field, exception.ParamName);
        Add(args, EngineErrorArgs.Value, exception.Value);
        Add(args, EngineErrorArgs.Index, exception.Index?.ToString(CultureInfo.InvariantCulture));
        Add(args, EngineErrorArgs.Character, exception.Character?.ToString());
        return new EngineError(
            EngineErrorCodes.AprsPrefix + Kebab(exception.Code.ToString()),
            profile is null ? exception.Message : $"Profile '{profile}': {exception.Message}",
            args
        );
    }

    /// <summary>
    /// The error for a Maidenhead locator APRS.NET rejected: <c>maidenhead.empty</c>, <c>maidenhead.wrong-length</c>
    /// or <c>maidenhead.invalid-character</c> with the character, its position and its pair (<c>square</c>…).
    /// </summary>
    public static EngineError From(MaidenheadParseError error, string value)
    {
        ArgumentNullException.ThrowIfNull(error);
        var args = new Dictionary<string, string>(StringComparer.Ordinal) { [EngineErrorArgs.Value] = value };
        if (error.Index >= 0)
        {
            args[EngineErrorArgs.Index] = error.Index.ToString(CultureInfo.InvariantCulture);
        }
        Add(args, EngineErrorArgs.Character, error.Character?.ToString());
        Add(args, EngineErrorArgs.Part, error.Part is { } part ? Kebab(part.ToString()) : null);
        var code = error.Kind switch
        {
            MaidenheadParseErrorKind.Empty => EngineErrorCodes.LocatorEmpty,
            MaidenheadParseErrorKind.WrongLength => EngineErrorCodes.LocatorWrongLength,
            _ => EngineErrorCodes.LocatorInvalidCharacter,
        };
        return new EngineError(code, error.Message, args);
    }

    /// <summary><c>CallsignInvalidLength</c> → <c>callsign-invalid-length</c>.</summary>
    internal static string Kebab(string name)
    {
        var text = new StringBuilder(name.Length + 8);
        foreach (var c in name)
        {
            if (char.IsUpper(c) && text.Length > 0)
            {
                text.Append('-');
            }
            text.Append(char.ToLowerInvariant(c));
        }
        return text.ToString();
    }

    private static void Add(Dictionary<string, string> args, string name, string? value)
    {
        if (value is not null)
        {
            args[name] = value;
        }
    }
}
