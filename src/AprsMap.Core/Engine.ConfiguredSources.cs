// SPDX-License-Identifier: MIT
using AprsMap.Contracts;
using Microsoft.Extensions.Logging;

namespace AprsMap.Core;

/// <summary>
/// The devices the gateway's configuration names (#424): the USB TNC (<c>Engine:KissSerial</c>), gpsd
/// (<c>Engine:Gpsd</c>) and a KISS over TCP TNC (<c>Engine:Kiss</c>). Their connection comes from the configuration on
/// every start and every save, not only the first run, so editing <c>gateway.toml</c> and restarting fixes a wrong baud
/// or a moved device. An existing source of that kind is adopted, keeping its id, so profiles and history carry on.
/// </summary>
public sealed partial class Engine
{
    /// <summary>The configured USB TNC's id.</summary>
    public const string ConfiguredKissSerialId = "config-kiss-serial";

    /// <summary>The configured gpsd's id.</summary>
    public const string ConfiguredGpsdId = "config-gpsd";

    /// <summary>The configured KISS over TCP TNC's id.</summary>
    public const string ConfiguredKissId = "config-kiss";

    /// <summary>One device the configuration names: how to recognise it, and what it says.</summary>
    private sealed record ConfiguredSource(
        string Id,
        Func<SourceSettings, bool> IsKind,
        Func<SourceSettings> Create,
        Func<SourceSettings, SourceSettings> Apply
    );

    /// <summary>The devices the configuration names now.</summary>
    private List<ConfiguredSource> ConfiguredSources()
    {
        List<ConfiguredSource> list = [];
        if (_options.KissSerial.Port is { Length: > 0 } serialPort)
        {
            var o = _options.KissSerial;
            list.Add(
                new(
                    ConfiguredKissSerialId,
                    s => s.Kind == SourceKind.KissSerial,
                    () =>
                        SourceSettings.KissSerialFor(serialPort.Trim(), o.Baud, o.TncPort) with
                        {
                            Id = ConfiguredKissSerialId,
                        },
                    s =>
                        s with
                        {
                            SerialPort = serialPort.Trim(),
                            Baud = o.Baud ?? s.Baud,
                            TncPort = o.TncPort ?? s.TncPort,
                            Name = Named(o.Name, s.Name),
                        }
                )
            );
        }
        if (_options.Gpsd.Host is { Length: > 0 } gpsdHost)
        {
            var o = _options.Gpsd;
            list.Add(
                new(
                    ConfiguredGpsdId,
                    s => s is { Kind: SourceKind.Gps, GpsLink: GpsLink.Gpsd },
                    () =>
                        SourceSettings.GpsNetworkFor(GpsLink.Gpsd, gpsdHost.Trim(), o.Port) with
                        {
                            Id = ConfiguredGpsdId,
                        },
                    s => s with { Host = gpsdHost.Trim(), Port = o.Port ?? s.Port, Name = Named(o.Name, s.Name) }
                )
            );
        }
        if (_options.Kiss.Host is { Length: > 0 } kissHost)
        {
            var o = _options.Kiss;
            list.Add(
                new(
                    ConfiguredKissId,
                    s => s.Kind == SourceKind.KissTcp,
                    () => SourceSettings.KissTcpFor(kissHost.Trim(), o.Port, o.TncPort) with { Id = ConfiguredKissId },
                    s =>
                        s with
                        {
                            Host = kissHost.Trim(),
                            Port = o.Port ?? s.Port,
                            TncPort = o.TncPort ?? s.TncPort,
                            Name = Named(o.Name, s.Name),
                        }
                )
            );
        }
        return list;
    }

    private static string Named(string? configured, string current) =>
        string.IsNullOrWhiteSpace(configured) ? current : configured.Trim();

    /// <summary>
    /// <paramref name="settings"/> with the configured devices' connections as the configuration says: each found by
    /// its id, or an existing source of its kind adopted (the one with its name, or the only one), or added to the
    /// active profile. Logs each value it changes. A station with no profile yet is left to the first run.
    /// </summary>
    private AppSettings WithConfiguredSources(AppSettings settings)
    {
        if (settings.Profiles.Count == 0)
        {
            return settings;
        }
        var sources = settings.SourceList.ToList();
        var profiles = settings.Profiles.ToList();
        var changed = false;
        foreach (var configured in ConfiguredSources())
        {
            var at = sources.FindIndex(s => s.Id == configured.Id);
            if (at < 0)
            {
                var ofKind = sources.Where(configured.IsKind).ToList();
                var wanted = configured.Create().Name;
                var adopt = ofKind.Find(s => s.Name == wanted) ?? (ofKind.Count == 1 ? ofKind[0] : null);
                at = adopt is null ? -1 : sources.IndexOf(adopt);
            }
            if (at < 0)
            {
                // Not there: added, and used by the active profile when it can take it.
                var added = configured.Apply(configured.Create()) with
                {
                    Configured = true,
                };
                sources.Add(added);
                var active = profiles.FindIndex(p => p.Id == settings.ActiveProfileId);
                if (active >= 0)
                {
                    var withIt = profiles[active] with { SourceIds = [.. profiles[active].SourceIds, added.Id] };
                    var candidate = settings with
                    {
                        Sources = sources,
                        Profiles = [.. profiles.Select((p, i) => i == active ? withIt : p)],
                    };
                    if (IsValid(candidate))
                    {
                        profiles[active] = withIt;
                    }
                }
                LogConfiguredAdded(_logger, added.Name);
                changed = true;
                continue;
            }
            var before = sources[at];
            var after = configured.Apply(before) with { Configured = true };
            if (after != before)
            {
                foreach (var (what, was, now) in Differences(before, after))
                {
                    LogConfiguredChanged(_logger, after.Name, what, was, now);
                }
                sources[at] = after;
                changed = true;
            }
        }
        // The same settings when nothing changed, so a load with nothing to apply saves nothing.
        return changed ? settings with { Sources = sources, Profiles = profiles } : settings;
    }

    private bool IsValid(AppSettings settings)
    {
        try
        {
            SettingsValidator.Validate(settings, _time);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static IEnumerable<(string What, string Was, string Now)> Differences(SourceSettings a, SourceSettings b)
    {
        static string Text(object? value) =>
            Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "(none)";
        if (a.SerialPort != b.SerialPort)
        {
            yield return ("serial port", Text(a.SerialPort), Text(b.SerialPort));
        }
        if (a.Baud != b.Baud)
        {
            yield return ("baud", Text(a.Baud ?? SourceSettings.DefaultBaud), Text(b.Baud));
        }
        if (a.TncPort != b.TncPort)
        {
            yield return ("radio port", Text(a.TncPort), Text(b.TncPort));
        }
        if (a.Host != b.Host)
        {
            yield return ("host", Text(a.Host), Text(b.Host));
        }
        if (a.Port != b.Port)
        {
            yield return ("port", Text(a.Port), Text(b.Port));
        }
        if (a.Name != b.Name)
        {
            yield return ("name", a.Name, b.Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: {What} {Was} → {Now}, from the configuration")]
    private static partial void LogConfiguredChanged(
        ILogger logger,
        string source,
        string what,
        string was,
        string now
    );

    [LoggerMessage(Level = LogLevel.Information, Message = "{Source}: added, from the configuration")]
    private static partial void LogConfiguredAdded(ILogger logger, string source);
}
