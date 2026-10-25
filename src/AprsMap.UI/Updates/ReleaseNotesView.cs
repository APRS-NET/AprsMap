// SPDX-License-Identifier: AGPL-3.0-only
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace AprsMap.UI.Updates;

/// <summary>
/// Release notes, formatted (docs/features/F4.15-updates.md, behavior 9.1): headings in the heading weight, list items
/// with a bullet, bold where the notes say, and links as links. Colours and sizes come from the theme the text
/// inherits; nothing is fetched or run.
/// </summary>
public sealed class ReleaseNotesView : StackPanel
{
    /// <summary>The blocks to show.</summary>
    public static readonly StyledProperty<IReadOnlyList<NoteBlock>?> BlocksProperty = AvaloniaProperty.Register<
        ReleaseNotesView,
        IReadOnlyList<NoteBlock>?
    >(nameof(Blocks));

    static ReleaseNotesView() => BlocksProperty.Changed.AddClassHandler<ReleaseNotesView>((view, _) => view.Rebuild());

    /// <summary>Creates the view.</summary>
    public ReleaseNotesView() => Spacing = 4;

    /// <inheritdoc cref="BlocksProperty" />
    public IReadOnlyList<NoteBlock>? Blocks
    {
        get => GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    private void Rebuild()
    {
        Children.Clear();
        foreach (var block in Blocks ?? [])
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
            foreach (var span in block.Spans)
            {
                text.Inlines!.Add(InlineOf(span, block.Kind == NoteBlockKind.Heading));
            }
            switch (block.Kind)
            {
                case NoteBlockKind.Heading:
                    text.FontWeight = FontWeight.SemiBold;
                    text.Margin = new Thickness(0, Children.Count == 0 ? 0 : 8, 0, 0);
                    Children.Add(text);
                    break;
                case NoteBlockKind.Bullet:
                    var row = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                        Margin = new Thickness(12 * block.Level, 0, 0, 0),
                    };
                    row.Children.Add(new TextBlock { Text = "•", Margin = new Thickness(0, 0, 6, 0) });
                    Grid.SetColumn(text, 1);
                    row.Children.Add(text);
                    Children.Add(row);
                    break;
                default:
                    Children.Add(text);
                    break;
            }
        }
    }

    private static Inline InlineOf(NoteSpan span, bool heading)
    {
        var weight = span.Bold || heading ? FontWeight.SemiBold : FontWeight.Normal;
        if (span.Link is not { } link)
        {
            return new Run(span.Text) { FontWeight = weight };
        }
        // A link in the text: the hyperlink's own look, opening in the browser (as About's links do).
        return new InlineUIContainer(
            new HyperlinkButton
            {
                Content = new TextBlock { Text = span.Text, FontWeight = weight },
                NavigateUri = link,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Bottom,
            }
        )
        {
            BaselineAlignment = BaselineAlignment.TextBottom,
        };
    }
}
