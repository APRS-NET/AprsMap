// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Updates;

namespace AprsMap.UI.Tests;

/// <summary>Release notes read for the update dialog (docs/features/F4.15-updates.md, behavior 9.1).</summary>
public sealed class ReleaseNotesTests
{
    private const string Notes = """
        ## [0.5.0-alpha.6](https://github.com/APRS-NET/AprsMap/compare/v0.5.0-alpha.5...v0.5.0-alpha.6) (2026-10-25)


        ### Features

        * **updates:** check on start and every two hours ([f8e75a7](https://github.com/APRS-NET/AprsMap/commit/f8e75a7)), closes [#361](https://github.com/APRS-NET/AprsMap/issues/361)
        * the `browser` folder ships with the desktop app


        ### Bug Fixes

        * **deps:** release APRS.NET 0.6.0-alpha.2 ([341c23d](https://github.com/APRS-NET/AprsMap/commit/341c23d))
          * a nested note
        Some closing words
        over two lines.
        """;

    [Fact]
    public void ReleasePleaseNotesBecomeHeadingsBulletsBoldAndLinks()
    {
        var blocks = ReleaseNotes.Parse(Notes, "0.5.0-alpha.6");

        Assert.Equal(
            [
                NoteBlockKind.Heading,
                NoteBlockKind.Bullet,
                NoteBlockKind.Bullet,
                NoteBlockKind.Heading,
                NoteBlockKind.Bullet,
                NoteBlockKind.Bullet,
                NoteBlockKind.Paragraph,
            ],
            blocks.Select(b => b.Kind)
        ); // the version heading is the dialog's own, left out
        Assert.Equal("Features", Assert.Single(blocks[0].Spans).Text);
        Assert.Equal(3, blocks[0].Level);

        var feature = blocks[1].Spans;
        Assert.Equal(new NoteSpan("updates:", Bold: true), feature[0]);
        Assert.Equal(" check on start and every two hours (", feature[1].Text);
        Assert.Equal(new Uri("https://github.com/APRS-NET/AprsMap/commit/f8e75a7"), feature[2].Link);
        Assert.Equal("f8e75a7", feature[2].Text);
        Assert.Equal("#361", feature[^1].Text);

        Assert.Equal("the browser folder ships with the desktop app", Assert.Single(blocks[2].Spans).Text); // code as text
        Assert.Equal(1, blocks[5].Level); // nested
        Assert.Equal("Some closing words over two lines.", Assert.Single(blocks[6].Spans).Text);
    }

    [Fact]
    public void OnlyWebAddressesAreLinks()
    {
        var spans = ReleaseNotes.Spans("see [this](javascript:alert(1)) and [that](https://example.org/)");

        Assert.Null(spans.Single(s => s.Text == "this").Link);
        Assert.Equal(new Uri("https://example.org/"), spans.Single(s => s.Text == "that").Link);
    }

    [Fact]
    public void TextWithoutMarkdownStaysAsItIs()
    {
        var block = Assert.Single(ReleaseNotes.Parse("Fixes [a bracket and * a star"));
        Assert.Equal("Fixes [a bracket and * a star", Assert.Single(block.Spans).Text);
    }
}
