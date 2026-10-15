// SPDX-License-Identifier: AGPL-3.0-only
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace AprsMap.UI.Packets;

/// <summary>
/// A packet's raw text with the part at fault marked (F1.8 behavior 3): in the warning colour, bold and underlined, so
/// it isn't told by colour alone. Built in code because XAML puts a space between neighbouring runs, and raw text must
/// stay exact.
/// </summary>
public sealed class RawText : SelectableTextBlock
{
    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Inlines?.Clear();
        if (DataContext is not PacketRow row)
        {
            return;
        }
        Inlines ??= [];
        Inlines.Add(new Run(row.RawBefore));
        if (row.RawFault.Length > 0)
        {
            var fault = new Run(row.RawFault)
            {
                FontWeight = FontWeight.Bold,
                TextDecorations = Avalonia.Media.TextDecorations.Underline,
            };
            fault.Bind(TextElement.ForegroundProperty, this.GetResourceObservable("Brush.Warn"));
            Inlines.Add(fault);
            Inlines.Add(new Run(row.RawAfter));
        }
    }
}
