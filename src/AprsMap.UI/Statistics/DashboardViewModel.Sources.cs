// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.ObjectModel;
using AprsMap.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AprsMap.UI.Statistics;

/// <summary>A choice in the dashboard's source picker: every source (null) or one (#454).</summary>
/// <param name="Id">The source, or null for every source.</param>
/// <param name="Name">"All sources", or the source's name.</param>
public sealed record SourceOption(string? Id, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>The dashboard by source (#454): the picker, and the sources side by side.</summary>
public sealed partial class DashboardViewModel
{
    /// <summary>All sources, then each of the profile's; shown when it has two or more.</summary>
    public ObservableCollection<SourceOption> SourceOptions { get; } = [];

    /// <summary>Whether the picker is offered: the profile has two or more sources.</summary>
    [ObservableProperty]
    public partial bool HasSourceChoice { get; private set; }

    /// <summary>The source shown; All sources (or null) for every one. Per viewer, for the session.</summary>
    [ObservableProperty]
    public partial SourceOption? SelectedSource { get; set; }

    /// <summary>Each source against the others: "42 stations · 1,240 packets · 7 heard only here".</summary>
    public ObservableCollection<ValueRow> Comparison { get; } = [];

    /// <summary>Whether the comparison is shown: every source's dashboard, with two or more.</summary>
    [ObservableProperty]
    public partial bool HasComparison { get; private set; }

    /// <summary>"Over the last 24 hours", over 7 days, where the comparison doesn't reach; null otherwise.</summary>
    [ObservableProperty]
    public partial string? ComparisonNote { get; private set; }

    // That the tiles read from the stations held count every source, while one source is shown; null otherwise.
    private string? _sourceNote;

    // The picker is being filled: its selection changes without asking again.
    private bool _fillingSources;

    partial void OnSelectedSourceChanged(SourceOption? value)
    {
        if (_fillingSources)
        {
            return;
        }
        ShowScope();
        if (_running is not null)
        {
            _ = RefreshAsync(_running.Token);
        }
    }

    /// <summary>The picker's choices from the profile's sources, keeping the one shown while it's still there.</summary>
    private void ShowSourceOptions()
    {
        var ids = _sources.Ids;
        var wanted = ids.Select(id => new SourceOption(id, _sources.NameOf(id)))
            .Prepend(new SourceOption(null, _text["Dashboard.Sources.All"]));
        if (!SourceOptions.SequenceEqual(wanted))
        {
            var selected = SelectedSource?.Id;
            _fillingSources = true;
            try
            {
                SourceOptions.Clear();
                foreach (var option in wanted)
                {
                    SourceOptions.Add(option);
                }
                SelectedSource = SourceOptions.FirstOrDefault(o => o.Id == selected) ?? SourceOptions[0];
            }
            finally
            {
                _fillingSources = false;
            }
        }
        HasSourceChoice = ids.Count > 1;
    }

    /// <summary>The comparison and the source note, from <paramref name="dashboard"/>.</summary>
    private void ShowSources(Dashboard dashboard)
    {
        ShowSourceOptions();
        _sourceNote = dashboard.SourceId is null ? null : _text["Dashboard.Sources.SomeTilesAll"];
        Comparison.Clear();
        foreach (var row in dashboard.Comparison ?? [])
        {
            Comparison.Add(
                new ValueRow(
                    _sources.NameOf(row.SourceId),
                    _text.Format(
                        "Dashboard.Sources.Row",
                        new Dictionary<string, object?>
                        {
                            ["stations"] = row.Stations,
                            ["packets"] = row.Packets,
                            ["only"] = row.OnlyHere,
                        }
                    )
                )
            );
        }
        HasComparison = Comparison.Count > 0;
        ComparisonNote =
            HasComparison && dashboard.Range == DashboardRange.Week ? _text["Dashboard.Sources.Day"] : null;
    }
}
