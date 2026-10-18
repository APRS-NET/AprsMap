// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Messages;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>The Messages panel (docs/features/F2.4-messaging.md, behaviors 1–9).</summary>
public sealed class MessagesTests
{
    private static readonly DateTimeOffset Noon = DateTimeOffset.Parse(
        "2026-10-17T12:00:00Z",
        CultureInfo.InvariantCulture
    );

    private static (MessagesViewModel Messages, FakeEngine Engine) Create(EngineStatus? status = null)
    {
        var engine = new FakeEngine();
        var messages = new MessagesViewModel(
            engine,
            new Localizer(CultureInfo.GetCultureInfo("en-US")),
            new FakeTimeProvider(),
            () => status
        );
        return (messages, engine);
    }

    private static ChatMessage Received(string id, string from, string text, int minute, bool read = false) =>
        new(id, from, Outgoing: false, text, Noon.AddMinutes(minute), ChatMessageState.Received, Read: read);

    [Fact]
    public void ConversationsAreNewestFirstWithTheirUnreadCounts()
    {
        var (messages, _) = Create();

        messages.Apply(Received("a", "N1ABC", "Hello", 1));
        messages.Apply(Received("b", "K2XYZ", "Hi there", 2));
        messages.Apply(Received("c", "K2XYZ", "Still there?", 3));

        Assert.Equal(["K2XYZ", "N1ABC"], messages.Conversations.Select(c => c.Correspondent));
        Assert.Equal([2, 1], messages.Conversations.Select(c => c.Unread));
        Assert.Equal("Still there?", messages.Conversations[0].LastText);
        Assert.Equal(3, messages.UnreadTotal);
    }

    [Fact]
    public async Task OpeningAConversationShowsItsMessagesAndMarksThemRead()
    {
        var (messages, engine) = Create();
        messages.Apply(Received("a", "N1ABC", "Hello", 1));
        messages.Apply(
            new ChatMessage(
                "b",
                "N1ABC",
                Outgoing: true,
                "Hi",
                Noon.AddMinutes(2),
                ChatMessageState.Retrying,
                Attempts: 2,
                MaxAttempts: 6
            )
        );

        messages.Selected = messages.Conversations.Single();
        await Task.Yield();

        Assert.Equal(["Hello", "Hi"], messages.Thread.Select(r => r.Text));
        Assert.EndsWith("Sent again, try 2 of 6", messages.Thread[1].StateText, StringComparison.Ordinal);
        Assert.True(messages.Thread[1].CanCancel);
        Assert.Equal(["N1ABC"], engine.MarkedRead);

        messages.Apply(Received("c", "N1ABC", "Still there?", 3)); // arrives while open: read at once
        Assert.Equal(["N1ABC", "N1ABC"], engine.MarkedRead);
    }

    [Fact]
    public async Task NewMessageOpensAConversationAndSendSendsWhatsWritten()
    {
        var (messages, engine) = Create();
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var watch = messages.WatchAsync(watching.Token);
        messages.NewCallsign = "n1abc";
        messages.StartCommand.Execute(null);

        Assert.Equal("N1ABC", messages.Selected?.Correspondent);
        Assert.Equal("No messages yet", messages.Selected!.LastText);
        messages.Compose = new string('x', 100);
        Assert.Equal("2 messages · 100 of 300", messages.ComposeCount);

        await messages.SendCommand.ExecuteAsync(null);

        Assert.Equal([("N1ABC", new string('x', 100))], engine.SentMessages);
        Assert.Empty(messages.Compose);
        for (var i = 0; i < 50 && messages.Thread.Count == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.Single(messages.Thread); // streamed back, as the engine does
        await watching.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watch);
    }

    [Fact]
    public async Task ARefusalShowsItsReason()
    {
        var (messages, engine) = Create();
        messages.Open("N1ABC");
        engine.SendFailure = EngineErrors.ToException(
            new EngineError(EngineErrorCodes.TransmitNotLicensed, "This profile only listens.")
        );
        messages.Compose = "Hi";

        await messages.SendCommand.ExecuteAsync(null);

        Assert.Equal(
            "This profile only listens: confirm you're a licensed amateur in the profile to transmit.",
            messages.Error
        );
        Assert.Equal("Hi", messages.Compose); // kept, to send once it can
    }

    [Fact]
    public async Task SendViaAndRetriesAreSavedForTheConversation()
    {
        var status = new EngineStatus(
            ConnectionState.Connected,
            "T2TEST",
            0,
            "1.0",
            Sources:
            [
                new SourceStatus(
                    "rotate",
                    "rotate.aprs2.net",
                    SourceKind.AprsIs,
                    ConnectionState.Connected,
                    TransmitEnabled: true
                ),
                new SourceStatus(
                    "tnc",
                    "Direwolf",
                    SourceKind.KissTcp,
                    ConnectionState.Connected,
                    TransmitEnabled: true
                ),
            ]
        );
        var (messages, engine) = Create(status);
        messages.Open("N1ABC");
        await Task.Yield();
        Assert.True(messages.Automatic);
        Assert.Equal(["rotate.aprs2.net", "Direwolf"], messages.Sources.Select(s => s.Name));

        messages.Automatic = false;
        messages.Sources[1].IsChecked = true;
        messages.SelectedRetries = messages.Retries.Single(r => r.Value == MessageRetries.SlowHf);
        await Task.Yield();

        var last = engine.ConversationSettings[^1];
        Assert.Equal("N1ABC", last.Correspondent);
        Assert.Equal(["tnc"], last.Via.SourceIds);
        Assert.Equal(MessageRetries.SlowHf, last.Retries);
    }

    [Fact]
    public void TextIsCountedInMessagesOfAtMost67()
    {
        Assert.Equal(3, MessagesViewModel.Split(string.Join(' ', Enumerable.Repeat("seventeen chars!", 9))).Count);
    }

    [Fact]
    public void AnOpenConversationTakesThePanelAndBackSaysWhatsUnreadElsewhere()
    {
        var (messages, _) = Create();
        messages.Apply(Received("a", "N1ABC", "Hello", 1, read: true));
        messages.Apply(Received("b", "K2XYZ", "Hi there", 2));
        messages.Apply(Received("c", "K2XYZ", "Still there?", 3));
        Assert.False(messages.IsOpen);

        messages.Selected = messages.Conversations.Single(c => c.Correspondent == "N1ABC");

        Assert.True(messages.IsOpen);
        Assert.Equal("← Conversations (2 unread)", messages.BackText);
        messages.BackCommand.Execute(null);
        Assert.False(messages.IsOpen);
        Assert.Null(messages.Selected);
    }
}
