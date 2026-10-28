// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Settings;

namespace AprsMap.UI.Tests;

/// <summary>An AGWPE source in Settings › Sources (docs/features/F3.8-agwpe.md, behavior 1).</summary>
public sealed class AgwpeUiTests
{
    private static SourcesSectionViewModel Section(FakeEngine engine)
    {
        var section = new SourcesSectionViewModel(engine, new Localizer(CultureInfo.GetCultureInfo("en-US")), _ => { });
        section.Show(engine.Settings);
        section.AddCommand.Execute(null);
        section.EditKind = SourceKind.Agwpe;
        return section;
    }

    [Fact]
    public async Task ItsRadioPortIsPickedFromTheServersListAndKeptFromZero()
    {
        var engine = new FakeEngine
        {
            Agwpe = new AgwpeServer(true, "2005.127", [new(0, "VHF 1200"), new(1, "HF 300")]),
        };
        var section = Section(engine);
        Assert.Equal(
            (true, true, false, true),
            (section.IsKiss, section.ShowsHost, section.ShowsTncPort, section.IsTnc)
        );
        Assert.Equal("8000", section.PortPlaceholder);

        await section.RefreshAgwpePortsCommand.ExecuteAsync(null);
        Assert.Equal("Type the server's host first.", section.AgwpeNote);
        section.EditHost = "localhost";
        await section.RefreshAgwpePortsCommand.ExecuteAsync(null);

        Assert.Equal([("localhost", 8000)], engine.AgwpeQueries);
        Assert.Equal(["1 · VHF 1200", "2 · HF 300"], section.AgwpePorts);
        Assert.Equal("The server (version 2005.127) has 2 radio ports.", section.AgwpeNote);
        Assert.Equal("1 · VHF 1200", section.EditAgwpePort); // its first, picked

        section.EditAgwpePort = section.AgwpePorts[1];
        await section.SaveCommand.ExecuteAsync(null);
        var saved = engine.Settings.SourceList.Single(s => s.Kind == SourceKind.Agwpe);
        Assert.Equal(("localhost", (int?)null, (int?)1), (saved.Host, saved.Port, saved.TncPort));
    }

    [Fact]
    public async Task WithNoAnswerThePortIsTypedFromOne()
    {
        var engine = new FakeEngine();
        var section = Section(engine);
        section.EditHost = "pi.local";
        section.EditPort = "8010";

        await section.RefreshAgwpePortsCommand.ExecuteAsync(null);
        Assert.Equal(
            "No AGWPE server answered at pi.local:8010. Type the radio port, or check the host and port and ask again.",
            section.AgwpeNote
        );
        section.EditAgwpePort = "3";
        await section.SaveCommand.ExecuteAsync(null);

        var saved = engine.Settings.SourceList.Single(s => s.Kind == SourceKind.Agwpe);
        Assert.Equal((8010, 2), (saved.Port, saved.TncPort));
        section.Rows.Single(r => r.Source.Id == saved.Id).EditCommand.Execute(null);
        Assert.Equal("3", section.EditAgwpePort); // shown from 1 again
    }
}
