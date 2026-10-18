// SPDX-License-Identifier: AGPL-3.0-only
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.ViewModels;

/// <summary>
/// A dialog over the main view (docs/features/F1.29-menus.md, behaviors 9–11, 14, 17): a title, what it shows, and
/// Close (also Escape).
/// </summary>
/// <param name="title">The title.</param>
/// <param name="content">What it shows: a view model the dialog has a template for.</param>
/// <param name="close">Closes it.</param>
public sealed partial class DialogViewModel(string title, object content, Action close) : ObservableObject
{
    /// <summary>The title.</summary>
    public string Title { get; } = title;

    /// <summary>What it shows.</summary>
    public object Content { get; } = content;

    /// <summary>Closes the dialog.</summary>
    [RelayCommand]
    private void Close() => close();
}

/// <summary>A question with one action and Cancel, such as Reset layout's (behavior 14).</summary>
/// <param name="text">The question.</param>
/// <param name="confirm">The action's words.</param>
/// <param name="run">The action.</param>
/// <param name="done">Closes the dialog, after the action or on Cancel.</param>
public sealed partial class ConfirmViewModel(string text, string confirm, Action run, Action done) : ObservableObject
{
    /// <summary>The question.</summary>
    public string Text { get; } = text;

    /// <summary>The action's words.</summary>
    public string ConfirmText { get; } = confirm;

    /// <summary>Runs the action and closes.</summary>
    [RelayCommand]
    private void Confirm()
    {
        run();
        done();
    }

    /// <summary>Closes without running it.</summary>
    [RelayCommand]
    private void Cancel() => done();
}

/// <summary>One keyboard shortcut: its menu, what it does and its keys (behavior 17).</summary>
public sealed record ShortcutRow(string Menu, string Command, string Keys);

/// <summary>Help › Keyboard shortcuts (behavior 17).</summary>
public sealed class ShortcutsViewModel(IReadOnlyList<ShortcutRow> rows)
{
    /// <summary>Every shortcut, menu by menu.</summary>
    public IReadOnlyList<ShortcutRow> Rows { get; } = rows;
}
