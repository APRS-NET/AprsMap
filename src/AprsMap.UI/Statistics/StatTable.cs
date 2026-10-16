// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AprsMap.UI.Statistics;

/// <summary>
/// One of the dashboard's statistics tables (docs/features/F1.22-statistics.md, behaviors 10–14): rows with a count, a
/// share and a bar, sorted by count or by name, copied as tab-separated text, each row applying its View filter.
/// </summary>
public sealed partial class StatTable : ObservableObject
{
    private readonly Localizer _text;
    private List<StatRow> _rows = [];

    internal StatTable(Localizer text, string titleKey)
    {
        _text = text;
        TitleKey = titleKey;
    }

    internal string TitleKey { get; }

    /// <summary>The table's title.</summary>
    public string Title => _text[TitleKey];

    /// <summary>The rows, in the order chosen.</summary>
    public ObservableCollection<StatRow> Rows { get; } = [];

    /// <summary>Whether the rows are by name rather than by count (behavior 11).</summary>
    [ObservableProperty]
    public partial bool ByName { get; set; }

    /// <summary>Whether there's anything to show.</summary>
    public bool HasRows => Rows.Count > 0;

    /// <summary>Sorts by name.</summary>
    [RelayCommand]
    private void SortByName() => ByName = true;

    /// <summary>Sorts by count, most first.</summary>
    [RelayCommand]
    private void SortByCount() => ByName = false;

    partial void OnByNameChanged(bool value) => Order();

    internal void Show(
        IEnumerable<(string Name, string? Detail, int Count, StationFilter? Filter)> rows,
        Action<StationFilter> use
    )
    {
        var all = rows.ToList();
        var total = all.Sum(r => r.Count);
        _rows =
        [
            .. all.Select(r =>
            {
                var percent = total == 0 ? 0 : (int)Math.Round(100.0 * r.Count / total, MidpointRounding.AwayFromZero);
                var count = _text.Format(
                    "Dashboard.Table.Count",
                    new Dictionary<string, object?> { ["count"] = r.Count }
                );
                var share = _text.Format(
                    "Dashboard.Percent",
                    new Dictionary<string, object?> { ["percent"] = percent }
                );
                return new StatRow(
                    r.Name,
                    r.Detail,
                    r.Count,
                    count,
                    share,
                    total == 0 ? 0 : (double)r.Count / total,
                    r.Filter is { } filter ? new RelayCommand(() => use(filter)) : null,
                    _text.Format(
                        "Dashboard.Table.Spoken",
                        new Dictionary<string, object?>
                        {
                            ["name"] = r.Detail is null ? r.Name : r.Name + ", " + r.Detail,
                            ["count"] = count,
                            ["share"] = share,
                        }
                    )
                );
            }),
        ];
        OnPropertyChanged(nameof(Title));
        Order();
    }

    private void Order()
    {
        Rows.Clear();
        var ordered = ByName
            ? _rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            : _rows.OrderByDescending(r => r.Count).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase);
        foreach (var row in ordered)
        {
            Rows.Add(row);
        }
        OnPropertyChanged(nameof(HasRows));
    }

    /// <summary>The table as tab-separated text, in the order shown, with a header row (behavior 14).</summary>
    public string CopyText()
    {
        var text = new StringBuilder();
        text.Append(Title)
            .Append('\t')
            .Append(_text["Dashboard.Table.Detail"])
            .Append('\t')
            .Append(_text["Dashboard.Table.Stations"])
            .Append('\t')
            .Append(_text["Dashboard.Table.Share"])
            .Append('\n');
        foreach (var row in Rows)
        {
            text.Append(row.Name)
                .Append('\t')
                .Append(row.Detail)
                .Append('\t')
                .Append(row.Count.ToString(CultureInfo.InvariantCulture))
                .Append('\t')
                .Append(Math.Round(row.Fraction * 100, 1).ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }
        return text.ToString();
    }
}

/// <summary>A row in a statistics table.</summary>
/// <param name="Name">The device, symbol, kind or reception.</param>
/// <param name="Detail">Its class or category; null when there's none.</param>
/// <param name="Count">How many stations.</param>
/// <param name="CountText">The count, written out.</param>
/// <param name="ShareText">Its share, written out.</param>
/// <param name="Fraction">Its share, 0 to 1, for the bar.</param>
/// <param name="Apply">Applies the row as a View filter and shows the map; null when no filter fits the row.</param>
/// <param name="Spoken">The row read as one sentence, for a screen reader.</param>
public sealed record StatRow(
    string Name,
    string? Detail,
    int Count,
    string CountText,
    string ShareText,
    double Fraction,
    IRelayCommand? Apply,
    string Spoken
)
{
    /// <summary>Whether clicking the row applies a filter.</summary>
    public bool CanApply => Apply is not null;

    /// <summary>" · Software", after the name; null without a detail.</summary>
    public string? DetailSuffix => Detail is null ? null : " · " + Detail;
}
