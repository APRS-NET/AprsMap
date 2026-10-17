// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Packets;
using AprsMap.UI.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Own station and transmitting in the UI (docs/features/F2.1-own-station.md, behaviors 1, 2, 4–13).</summary>
public sealed class TransmitUiTests
{
    private static EngineStatus Status(StationToggles toggles, TransmitStatus transmit) =>
        new(ConnectionState.Connected, "T2TEST", 0, "1.0", Verified: true, Toggles: toggles, Transmit: transmit);

    private static MainViewModel Shell(FakeEngine engine, bool shared = false)
    {
        var vm = new MainViewModel(engine, new FakeTimeProvider()) { SharedStation = shared };
        vm.Show(engine.Settings);
        return vm;
    }

    [Fact]
    public async Task TransmitTurnsOnFromTheMenuAndSaysSoInTheStatusBar()
    {
        var engine = new FakeEngine();
        var vm = Shell(engine);
        vm.Show(Status(StationToggles.AllOn, new TransmitStatus(false)));
        Assert.True(vm.CanTransmit);
        Assert.False(vm.TransmitOn);
        Assert.Null(vm.TransmitText); // nothing to say while it's off
        Assert.False(vm.SendPositionCommand.CanExecute(null));

        await vm.ToggleCommand.ExecuteAsync(Toggle.Transmit);

        Assert.Equal([(Toggle.Transmit, true)], engine.Toggled);
        Assert.True(vm.TransmitOn);
        Assert.Equal("Transmitting", vm.TransmitText);
        Assert.False(vm.IsTransmitHeld);
        Assert.True(vm.SendPositionCommand.CanExecute(null));
        Assert.Equal("Transmit on", vm.ToggleAnnouncement);
    }

    [Fact]
    public void AHeldTransmitSaysWhyAndOffersNoSend()
    {
        var engine = new FakeEngine();
        var vm = Shell(engine);

        vm.Show(
            Status(
                StationToggles.AllOn with
                {
                    Transmit = true,
                },
                new TransmitStatus(
                    true,
                    new EngineError(EngineErrorCodes.TransmitNotConnected, "APRS-IS isn't connected.")
                )
            )
        );

        Assert.True(vm.IsTransmitHeld);
        Assert.Equal("Transmit held: APRS-IS isn't connected.", vm.TransmitText);
        Assert.False(vm.SendPositionCommand.CanExecute(null));
    }

    [Fact]
    public async Task SendPositionNowSendsAndARefusalShowsInTheErrorLine()
    {
        var engine = new FakeEngine();
        var vm = Shell(engine);
        vm.Show(Status(StationToggles.AllOn with { Transmit = true }, new TransmitStatus(true)));

        await vm.SendPositionCommand.ExecuteAsync(null);

        Assert.Equal(1, engine.PositionsSent);
        Assert.Equal("Position sent", vm.TransmitAnnouncement);

        engine.SendFailure = EngineErrors.ToException(
            new EngineError(EngineErrorCodes.TransmitNoPosition, "Set the station's location first.")
        );
        await vm.SendPositionCommand.ExecuteAsync(null);

        Assert.Equal("Set the station's location first.", vm.ErrorText);
    }

    [Fact]
    public void AGatewaysBrowserAndAReceiveOnlyEngineCantTransmit()
    {
        var shared = Shell(new FakeEngine(), shared: true);
        shared.Show(Status(StationToggles.AllOn, new TransmitStatus(false)));
        Assert.False(shared.CanTransmit);

        var receiveOnly = Shell(new FakeEngine());
        receiveOnly.Show(Status(StationToggles.AllOn, new TransmitStatus(false, ReceiveOnly: true)));
        Assert.False(receiveOnly.CanTransmit);
    }

    [Fact]
    public void ASentPacketCarriesTheSentMarker()
    {
        var text = new Localizer(CultureInfo.GetCultureInfo("en-US"));
        var row = new PacketRow(
            new PacketView(
                DateTimeOffset.UnixEpoch,
                "N0TEST",
                "Position",
                "Position",
                "N0TEST>APZAMP:!",
                Markers: PacketMarkers.Sent
            ),
            text,
            TimeZoneInfo.Utc
        );

        Assert.True(row.IsSent);
        Assert.False(row.IsDirect);
    }

    [Fact]
    public async Task TheProfileEditorKeepsAndSavesTheSymbolAndComment()
    {
        var two = FakeEngine.TwoProfiles();
        var settings = two with
        {
            Profiles =
            [
                two.Profiles[0] with
                {
                    Callsign = "AB1CD-9",
                    Symbol = "/>",
                    Comment = "Mobile",
                },
                two.Profiles[1],
            ],
        };
        var engine = new FakeEngine { Settings = settings };
        var editing = settings.Profiles[0];
        var wizard = new SetupWizardViewModel(
            engine,
            new Localizer(CultureInfo.GetCultureInfo("en-US")),
            null,
            WizardMode.Edit,
            settings,
            editing
        );
        await wizard.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("/>", wizard.SelectedSymbol?.Symbol);
        Assert.Equal("Mobile", wizard.Comment);
        Assert.Contains(SetupWizardViewModel.Symbols, s => s.Symbol == "/-"); // the house, offered

        wizard.SelectedSymbol = SymbolChoice.For("/[");
        wizard.Comment = "  Walking  ";
        await wizard.FinishCommand.ExecuteAsync(null);

        var profile = Assert.Single(engine.Saved).Profiles.Single(p => p.Id == editing.Id);
        Assert.Equal("/[", profile.Symbol);
        Assert.Equal("Walking", profile.Comment);
    }
}
