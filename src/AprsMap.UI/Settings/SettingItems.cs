// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AprsMap.UI.Settings;

/// <summary>
/// One option on a settings page, built from the registry (docs/features/F0.6-appearance.md, behavior 4). Its label
/// and help come from <c>Strings.resx</c> and follow the language.
/// </summary>
public abstract partial class SettingItem : ObservableObject
{
    private readonly Localizer _text;
    private readonly string _labelKey;
    private readonly string? _helpKey;

    /// <summary>Creates the item.</summary>
    protected SettingItem(string id, string labelKey, string? helpKey, Localizer text)
    {
        Id = id;
        _labelKey = labelKey;
        _helpKey = helpKey;
        _text = text;
        Label = text[labelKey];
        Help = helpKey is null ? null : text[helpKey];
    }

    /// <summary>A stable identifier (for search and tests).</summary>
    public string Id { get; }

    /// <summary>The label, in the viewer's language.</summary>
    [ObservableProperty]
    public partial string Label { get; set; }

    /// <summary>One line of help, or null.</summary>
    [ObservableProperty]
    public partial string? Help { get; set; }

    /// <summary>The localizer, for subclasses' own texts.</summary>
    protected Localizer Text => _text;

    /// <summary>Rewords the item after a language change.</summary>
    public virtual void Retranslate()
    {
        Label = _text[_labelKey];
        Help = _helpKey is null ? null : _text[_helpKey];
    }

    /// <summary>Shows the current value again (after a reset, an undo or a change elsewhere).</summary>
    public abstract void Refresh();
}

/// <summary>A value to choose, with its label.</summary>
/// <param name="Value">The value (boxed); compared with <see cref="object.Equals(object?)"/>.</param>
/// <param name="Label">The label, in the viewer's language.</param>
public sealed record SettingOption(object? Value, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>An option with a few choices, shown as a drop-down.</summary>
public sealed partial class ChoiceSetting : SettingItem
{
    private readonly Func<IReadOnlyList<(object? Value, string Label)>> _options;
    private readonly Func<object?> _get;
    private readonly Action<object?> _set;
    private bool _refreshing;

    /// <summary>Creates the setting; <paramref name="options"/> is asked again after a language change.</summary>
    public ChoiceSetting(
        string id,
        string labelKey,
        string? helpKey,
        Localizer text,
        Func<IReadOnlyList<(object? Value, string Label)>> options,
        Func<object?> get,
        Action<object?> set
    )
        : base(id, labelKey, helpKey, text)
    {
        _options = options;
        _get = get;
        _set = set;
        Retranslate();
    }

    /// <summary>The choices.</summary>
    public ObservableCollection<SettingOption> Options { get; } = [];

    /// <summary>The current choice; choosing applies it at once (behavior 5).</summary>
    [ObservableProperty]
    public partial SettingOption? Selected { get; set; }

    partial void OnSelectedChanged(SettingOption? value)
    {
        if (!_refreshing && value is not null && !Equals(value.Value, _get()))
        {
            _set(value.Value);
        }
    }

    /// <inheritdoc />
    public override void Retranslate()
    {
        base.Retranslate();
        _refreshing = true;
        try
        {
            Options.Clear();
            foreach (var (value, label) in _options())
            {
                Options.Add(new SettingOption(value, label));
            }
        }
        finally
        {
            _refreshing = false;
        }
        Refresh();
    }

    /// <inheritdoc />
    public override void Refresh()
    {
        _refreshing = true;
        try
        {
            var current = _get();
            Selected = Options.FirstOrDefault(o => Equals(o.Value, current));
        }
        finally
        {
            _refreshing = false;
        }
    }
}

/// <summary>An on/off option, shown as a check box.</summary>
public sealed partial class ToggleSetting : SettingItem
{
    private readonly Func<bool> _get;
    private readonly Action<bool> _set;
    private bool _refreshing;

    /// <summary>Creates the setting.</summary>
    public ToggleSetting(string id, string labelKey, string? helpKey, Localizer text, Func<bool> get, Action<bool> set)
        : base(id, labelKey, helpKey, text)
    {
        _get = get;
        _set = set;
        Refresh();
    }

    /// <summary>On or off; changing it applies at once.</summary>
    [ObservableProperty]
    public partial bool IsOn { get; set; }

    partial void OnIsOnChanged(bool value)
    {
        if (!_refreshing && value != _get())
        {
            _set(value);
        }
    }

    /// <inheritdoc />
    public override void Refresh()
    {
        _refreshing = true;
        try
        {
            IsOn = _get();
        }
        finally
        {
            _refreshing = false;
        }
    }
}
