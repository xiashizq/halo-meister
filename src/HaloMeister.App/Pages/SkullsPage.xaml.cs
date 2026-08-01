using HaloMeister.App.Localization;
using HaloMeister.App.Models;
using HaloMeister.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class SkullsPage : Page
{
    private readonly AppState _state = AppState.Current;

    public SkullsPage()
    {
        InitializeComponent();
        OnFilterChanged(this, null);
    }

    private void OnAll(object sender, RoutedEventArgs e) => Bulk(true);

    private void OnNone(object sender, RoutedEventArgs e) => Bulk(false);

    private void Bulk(bool enabled)
    {
        _state.SetAllTags(enabled, tag => tag.StartsWith(Catalog.SkullPrefix, StringComparison.Ordinal));
        OnFilterChanged(this, null!);
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs? e)
    {
        string query = Filter.Text.Trim();
        IReadOnlyList<TagToggle> filtered = query.Length == 0
            ? _state.Skulls
            : _state.Skulls
                .Where(s => s.Display.Contains(query, StringComparison.OrdinalIgnoreCase)
                            || s.Tag.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        SkullList.ItemsSource = filtered;
        SkullCountText.Text = query.Length == 0
            ? L.Format("skulls.skull_count", filtered.Count)
            : L.Format("skulls.visible_of_total", filtered.Count, _state.Skulls.Count);
    }
}
