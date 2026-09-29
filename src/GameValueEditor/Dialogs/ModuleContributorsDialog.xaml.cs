using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using GameValueEditor.Services.Adapters;

namespace GameValueEditor.Dialogs;

public partial class ModuleContributorsDialog : Window
{
    private readonly IReadOnlyList<ContributorRow> _source;
    private readonly ObservableCollection<ContributorRow> _rows = [];

    public ModuleContributorsDialog(IReadOnlyList<GameModuleContributor> contributors)
    {
        InitializeComponent();
        _source = contributors.Select(item => new ContributorRow(
            item.DisplayName,
            new Uri(item.ProfileUrl),
            item.FirstContributionDate.ToString("yyyy-MM-dd"),
            item.LatestContributionDate.ToString("yyyy-MM-dd"))).ToList();
        ContributorsGrid.ItemsSource = _rows;
        ApplySort("Name");
    }

    private void SortComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || SortComboBox.SelectedItem is not ComboBoxItem item) return;
        ApplySort(item.Tag?.ToString() ?? "Name");
    }

    private void ApplySort(string mode)
    {
        var sorted = mode switch
        {
            "First" => _source.OrderBy(item => item.FirstContributionDate).ThenBy(item => item.DisplayName),
            "Latest" => _source.OrderByDescending(item => item.LatestContributionDate).ThenBy(item => item.DisplayName),
            _ => _source.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
        };
        _rows.Clear();
        foreach (var row in sorted) _rows.Add(row);
    }

    private void ContributorLink_OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private sealed record ContributorRow(
        string DisplayName,
        Uri ProfileUri,
        string FirstContributionDate,
        string LatestContributionDate);
}
