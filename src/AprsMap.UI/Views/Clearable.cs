// SPDX-License-Identifier: AGPL-3.0-only
using AprsMap.UI.Localization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace AprsMap.UI.Views;

/// <summary>
/// A clear button for search and filter boxes (#420): <c>views:Clearable.IsOn="True"</c> on a <see cref="TextBox"/>
/// puts an × inside it, shown while it has text, that empties it (and so its filter) and keeps the focus there. Escape
/// in a box with text does the same; in an empty box it goes on to whatever else Escape does.
/// </summary>
public static class Clearable
{
    /// <summary>The × drawn in the box, as the update notice's.</summary>
    private const string Cross =
        "M 6.4,5 L 12,10.6 L 17.6,5 L 19,6.4 L 13.4,12 L 19,17.6 L 17.6,19 L 12,13.4 L 6.4,19 L 5,17.6 L 10.6,12 L 5,6.4 Z";

    /// <summary>Whether the box has the clear button.</summary>
    public static readonly AttachedProperty<bool> IsOnProperty = AvaloniaProperty.RegisterAttached<TextBox, bool>(
        "IsOn",
        typeof(Clearable)
    );

    static Clearable() => IsOnProperty.Changed.AddClassHandler<TextBox>(OnChanged);

    /// <summary>Whether <paramref name="box"/> has the clear button.</summary>
    public static bool GetIsOn(TextBox box) => box.GetValue(IsOnProperty);

    /// <summary>Gives <paramref name="box"/> the clear button, or takes it away.</summary>
    public static void SetIsOn(TextBox box, bool value) => box.SetValue(IsOnProperty, value);

    private static void OnChanged(TextBox box, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true || box.InnerRightContent is Button { Name: "ClearButton" })
        {
            return;
        }
        var icon = new PathIcon { Data = StreamGeometry.Parse(Cross) };
        icon.Bind(Layoutable.WidthProperty, icon.GetResourceObservable("Icon.Small"));
        icon.Bind(Layoutable.HeightProperty, icon.GetResourceObservable("Icon.Small"));
        icon.Bind(TemplatedControl.ForegroundProperty, icon.GetResourceObservable("Brush.TextMuted")); // quiet
        var button = new Button
        {
            Name = "ClearButton",
            Content = icon,
            Background = null,
            BorderThickness = default,
            Padding = new Thickness(4, 2),
            Margin = new Thickness(0, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            IsVisible = !string.IsNullOrEmpty(box.Text),
        };
        var name = Localizer.Instance.Observe("Search.Clear");
        button.Bind(AutomationProperties.NameProperty, name);
        button.Bind(ToolTip.TipProperty, name);
        button.Click += (_, args) =>
        {
            Clear(box);
            args.Handled = true;
        };
        box.InnerRightContent = button;
        box.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty)
            {
                button.IsVisible = !string.IsNullOrEmpty(box.Text);
            }
        };
        box.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (
            e.Key == Key.Escape
            && e.KeyModifiers == KeyModifiers.None
            && sender is TextBox box
            && !string.IsNullOrEmpty(box.Text)
        )
        {
            Clear(box);
            e.Handled = true; // an empty box lets Escape go on
        }
    }

    private static void Clear(TextBox box)
    {
        box.Text = string.Empty;
        box.Focus();
    }
}
