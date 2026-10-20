// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Messages;
using Microsoft.Extensions.Time.Testing;

namespace AprsMap.UI.Tests;

/// <summary>Email and QRU in Messages (docs/features/F2.6-email-and-qru.md).</summary>
public sealed class EmailQruUiTests
{
    private static (MessagesViewModel Messages, FakeEngine Engine) Create()
    {
        var engine = new FakeEngine();
        var messages = new MessagesViewModel(
            engine,
            new Localizer(CultureInfo.GetCultureInfo("en-US")),
            new FakeTimeProvider(),
            () => null
        );
        return (messages, engine);
    }

    [Fact]
    public async Task AnEmailIsOneMessageToTheGatewayAndOpensItsConversation()
    {
        var (messages, engine) = Create();
        Assert.False(messages.SendEmailCommand.CanExecute(null));

        messages.EmailTo = " n0call@example.com ";
        messages.EmailText = "Running late, home by 6";
        Assert.Equal("42 of 67", messages.EmailCount);
        await messages.SendEmailCommand.ExecuteAsync(null);

        Assert.Equal([("EMAIL-2", "n0call@example.com Running late, home by 6")], engine.SentMessages);
        Assert.Equal("EMAIL-2", messages.Selected?.Correspondent);
        Assert.Equal(string.Empty, messages.EmailText);
        Assert.Null(messages.ServiceError);
    }

    [Theory]
    [InlineData("mum work", "Hello", "The address or shortcut is one word, without spaces.")]
    [InlineData("n0call@example.com", "This text runs exactly one character past the cap", "One character too long")]
    public async Task AnEmailThatWontFitOneMessageIsRefused(string to, string text, string said)
    {
        var (messages, engine) = Create();
        messages.EmailTo = to;
        messages.EmailText = text;

        await messages.SendEmailCommand.ExecuteAsync(null);

        Assert.Empty(engine.SentMessages);
        Assert.StartsWith(said, messages.ServiceError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskingQruServersSaysWhatWasAskedAndWhere()
    {
        var (messages, engine) = Create();
        Assert.Equal("INFO", messages.QruGroup);

        messages.QruGroup = " rep ";
        await messages.AskQruCommand.ExecuteAsync(null);

        Assert.Equal(["REP"], engine.QruAsked);
        Assert.Equal("Asked QRU servers for REP on 2 sources", messages.QruText);

        engine.SendFailure = new EngineOperationException(
            new EngineError(EngineErrorCodes.TransmitOff, "Transmit is off.")
        );
        await messages.AskQruCommand.ExecuteAsync(null);
        Assert.Null(messages.QruText);
        Assert.NotNull(messages.ServiceError);
    }
}
