// SPDX-License-Identifier: AGPL-3.0-only
using System.Text;

namespace AprsMap.UI.Updates;

/// <summary>What kind of block a line of release notes is.</summary>
public enum NoteBlockKind
{
    /// <summary>A heading: <c>## …</c>.</summary>
    Heading,

    /// <summary>A list item: <c>* …</c> or <c>- …</c>.</summary>
    Bullet,

    /// <summary>Plain text.</summary>
    Paragraph,
}

/// <summary>A run of text in a block: bold, a link, or neither.</summary>
/// <param name="Text">The words shown.</param>
/// <param name="Bold">Shown bold (<c>**…**</c>).</param>
/// <param name="Link">Where it goes (<c>[…](https://…)</c>); only http and https are links.</param>
public sealed record NoteSpan(string Text, bool Bold = false, Uri? Link = null);

/// <summary>A heading, a list item or a paragraph of release notes.</summary>
/// <param name="Kind">Which.</param>
/// <param name="Level">A heading's level (2 for <c>##</c>); a list item's depth from 0.</param>
/// <param name="Spans">Its text.</param>
public sealed record NoteBlock(NoteBlockKind Kind, int Level, IReadOnlyList<NoteSpan> Spans);

/// <summary>
/// Release notes as GitHub writes them, read into blocks the update dialog shows formatted
/// (docs/features/F4.15-updates.md, behavior 9.1): the Markdown release-please writes (headings, lists, bold, links and
/// code), nothing more. Anything else stays as text; nothing in the notes runs or loads.
/// </summary>
public static class ReleaseNotes
{
    /// <summary>
    /// The blocks of <paramref name="markdown"/>; a first heading naming <paramref name="version"/> is left out, as the
    /// dialog's own heading says it.
    /// </summary>
    public static IReadOnlyList<NoteBlock> Parse(string markdown, string? version = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var blocks = new List<NoteBlock>();
        var paragraph = new StringBuilder();
        void EndParagraph()
        {
            if (paragraph.Length > 0)
            {
                blocks.Add(new NoteBlock(NoteBlockKind.Paragraph, 0, Spans(paragraph.ToString())));
                paragraph.Clear();
            }
        }
        foreach (var raw in markdown.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                EndParagraph();
                continue;
            }
            var hashes = trimmed.TakeWhile(c => c == '#').Count();
            if (hashes is >= 1 and <= 6 && trimmed.Length > hashes && trimmed[hashes] == ' ')
            {
                EndParagraph();
                var spans = Spans(trimmed[(hashes + 1)..].Trim());
                var isVersion =
                    blocks.Count == 0
                    && version is not null
                    && string.Concat(spans.Select(s => s.Text)).Contains(version, StringComparison.Ordinal);
                if (!isVersion)
                {
                    blocks.Add(new NoteBlock(NoteBlockKind.Heading, hashes, spans));
                }
                continue;
            }
            if (trimmed.Length > 2 && trimmed[0] is '*' or '-' && trimmed[1] == ' ')
            {
                EndParagraph();
                var depth = (line.Length - trimmed.Length) / 2;
                blocks.Add(new NoteBlock(NoteBlockKind.Bullet, depth, Spans(trimmed[2..].Trim())));
                continue;
            }
            if (paragraph.Length > 0)
            {
                paragraph.Append(' ');
            }
            paragraph.Append(trimmed);
        }
        EndParagraph();
        return blocks;
    }

    /// <summary>The runs of a line: <c>**bold**</c>, <c>[text](url)</c>, and <c>`code`</c> shown as its text.</summary>
    internal static IReadOnlyList<NoteSpan> Spans(string text)
    {
        var spans = new List<NoteSpan>();
        var current = new StringBuilder();
        var bold = false;
        void Flush()
        {
            if (current.Length > 0)
            {
                spans.Add(new NoteSpan(current.ToString(), bold));
                current.Clear();
            }
        }
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                Flush();
                bold = !bold;
                i++;
                continue;
            }
            if (text[i] == '`')
            {
                continue; // code is shown as its words
            }
            if (text[i] == '[' && LinkAt(text, i) is var (label, target, end))
            {
                Flush();
                var words = label
                    .Replace("**", "", StringComparison.Ordinal)
                    .Replace("`", "", StringComparison.Ordinal);
                spans.Add(
                    Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
                        ? new NoteSpan(words, bold || label.StartsWith("**", StringComparison.Ordinal), uri)
                        : new NoteSpan(words, bold)
                );
                i = end;
                continue;
            }
            current.Append(text[i]);
        }
        Flush();
        return spans;
    }

    /// <summary>A link starting at <paramref name="start"/>: its label, target and the index of its closing bracket.</summary>
    private static (string Label, string Target, int End)? LinkAt(string text, int start)
    {
        var close = text.IndexOf("](", start + 1, StringComparison.Ordinal);
        if (close < 0)
        {
            return null;
        }
        var end = text.IndexOf(')', close + 2);
        return end < 0 ? null : (text[(start + 1)..close], text[(close + 2)..end], end);
    }
}
