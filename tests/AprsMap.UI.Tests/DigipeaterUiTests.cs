// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Packets;
using AprsMap.UI.Settings;

namespace AprsMap.UI.Tests;

/// <summary>The digipeater in the UI (docs/features/F3.5-digipeater.md, behaviors 1 and 5).</summary>
public sealed class DigipeaterUiTests
{
    [Fact]
    public async Task AKissTncOffersDigipeatingAndItsChoiceAndAliasesAreSaved()
    {
        var engine = new FakeEngine();
        var section = new SourcesSectionViewModel(engine, Localizer.Instance, _ => { });
        section.Show(engine.Settings);
        section.AddCommand.Execute(null);
        section.EditKind = SourceKind.KissTcp;
        Assert.True(section.IsKiss);
        Assert.True(section.DigipeatOff); // the default

        section.DigipeatFillIn = true;
        section.EditDigipeatAliases = " PLAINS ";
        section.EditHost = "127.0.0.1";
        await section.SaveCommand.ExecuteAsync(null);

        var tnc = engine.Settings.SourceList.Single(s => s.Kind == SourceKind.KissTcp);
        Assert.Equal((DigipeatMode.FillIn, "PLAINS"), (tnc.Digipeat, tnc.DigipeatAliases));
        Assert.Equal(["PLAINS"], tnc.DigipeatAliasList);
    }

    [Fact]
    public void ARepeatedPacketIsMarkedAndTheDashboardCountsThem()
    {
        var text = new Localizer(CultureInfo.GetCultureInfo("en-US"));
        var packet = FakeEngine.Packet("N0CAR-9") with
        {
            Markers = PacketMarkers.HeardHere | PacketMarkers.DigipeatedHere,
        };
        Assert.True(new PacketRow(packet, text, TimeZoneInfo.Utc).IsDigipeatedHere);

        var row = Statistics.SourceRow.Of(
            text,
            new SourceStatus("tnc", "Direwolf", SourceKind.KissTcp, ConnectionState.Connected, Digipeated: 12),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            at => at.ToString("t", CultureInfo.InvariantCulture)
        );
        Assert.EndsWith("· 12 digipeated", row.State, StringComparison.Ordinal);
    }
}
