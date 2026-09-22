using System.Windows;
using System.Windows.Input;
using CVmaker.App.Controls;
using CVmaker.App.Localization;
using CVmaker.Core.Cut;

namespace CVmaker.App;

public sealed class IssueRow
{
    public string OutTime { get; init; } = "";
    public string OrigTime { get; init; } = "";
    public string ColumnText { get; init; } = "";
    public string Description { get; init; } = "";
}

/// <summary>
/// Shown before an export that keeps the notes when the cut created overlapping, duplicated or
/// over-shortened notes. The list is collapsed behind a chevron and scrolls when long.
/// </summary>
public partial class NoteIssuesDialog : Window
{
    private readonly int _count;

    public NoteIssuesDialog(Window owner, IReadOnlyList<NoteIssue> issues)
    {
        InitializeComponent();
        Owner = owner;
        _count = issues.Count;
        int overlaps = issues.Count(i => i.Kind != NoteIssueKind.ShortHold);
        int shortHolds = issues.Count(i => i.Kind == NoteIssueKind.ShortHold);
        SummaryText.Text = Loc.F("Issues.Summary", overlaps, shortHolds);
        Rows.ItemsSource = issues.Select(i => new IssueRow
        {
            OutTime = WaveformView.FormatTime(i.OutputTimeMs),
            OrigTime = i.OriginalTimeMs >= 0 ? Loc.F("Issues.Orig", WaveformView.FormatTime(i.OriginalTimeMs)) : "",
            ColumnText = Loc.F("Issues.Column", i.Column),
            Description = i.Kind switch
            {
                NoteIssueKind.Overlap => Loc.F("Issue.Overlap", i.LengthMs),
                NoteIssueKind.Duplicate => Loc.T("Issue.Duplicate"),
                _ => Loc.F("Issue.ShortHold", i.LengthMs),
            },
        }).ToList();
        DetailsToggle.IsChecked = false;
        UpdateToggleText();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; } };
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void UpdateToggleText() =>
        DetailsToggle.Content = DetailsToggle.IsChecked == true ? Loc.T("Issues.Hide") : Loc.F("Issues.Show", _count);

    private void DetailsToggle_Changed(object sender, RoutedEventArgs e)
    {
        DetailsScroll.Visibility = DetailsToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateToggleText();
    }

    internal void ExpandDetails() => DetailsToggle.IsChecked = true;

    /// <summary>True when the user chose to continue.</summary>
    public static bool Show(Window owner, IReadOnlyList<NoteIssue> issues) => new NoteIssuesDialog(owner, issues).ShowDialog() == true;

    private void ContinueButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
