using HaloMeister.App.Localization;
using HaloMeister.App.Models;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class RawPage : Page
{
    private readonly AppState _state = AppState.Current;

    public RawPage()
    {
        InitializeComponent();
        Apply();
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => Apply();

    private void Apply()
    {
        string query = Filter.Text.Trim();

        IReadOnlyList<RawRow> rows = query.Length == 0
            ? _state.RawRows
            : _state.RawRows
                .Where(r => r.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

        RawList.ItemsSource = rows;
        CountText.Text = L.Format("raw.properties_count", rows.Count, _state.RawRows.Count);
    }
}
