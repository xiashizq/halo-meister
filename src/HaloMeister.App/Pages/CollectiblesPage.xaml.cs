using HaloMeister.App.Localization;
using HaloMeister.App.Models;
using HaloMeister.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class CollectiblesPage : Page
{
    private readonly AppState _state = AppState.Current;

    public CollectiblesPage()
    {
        InitializeComponent();

        TerminalsList.ItemsSource = _state.Terminals;
        InsertionList.ItemsSource = _state.InsertionPoints;
        GatesList.ItemsSource = _state.UnlockGates;
        ExtraList.ItemsSource = _state.ExtraTags;

        ExtraExpander.Visibility = _state.ExtraTags.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCount();
    }

    private void OnAll(object sender, RoutedEventArgs e) => Bulk(true);

    private void OnNone(object sender, RoutedEventArgs e) => Bulk(false);

    private void Bulk(bool enabled)
    {
        _state.SetAllTags(enabled, tag =>
            tag.StartsWith(Catalog.TerminalPrefix, StringComparison.Ordinal)
            || tag.StartsWith(Catalog.InsertionPrefix, StringComparison.Ordinal));
        ApplyFilter();
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
        => ApplyFilter();

    private void ApplyFilter()
    {
        string query = FilterBox.Text.Trim();
        TerminalsList.ItemsSource = Filter(_state.Terminals, query);
        InsertionList.ItemsSource = Filter(_state.InsertionPoints, query);
        GatesList.ItemsSource = Filter(_state.UnlockGates, query);
        ExtraList.ItemsSource = Filter(_state.ExtraTags, query);

        if (query.Length > 0)
        {
            TerminalsExpander.IsExpanded = TerminalsList.Items.Cast<object>().Any();
            InsertionExpander.IsExpanded = InsertionList.Items.Cast<object>().Any();
            GatesExpander.IsExpanded = GatesList.Items.Cast<object>().Any();
            ExtraExpander.IsExpanded = ExtraList.Items.Cast<object>().Any();
        }
        UpdateCount(query);
    }

    private static IReadOnlyList<TagToggle> Filter(
        IEnumerable<TagToggle> source,
        string query)
        => query.Length == 0
            ? source.ToList()
            : source.Where(item =>
                    item.Display.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    item.Tag.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

    private void UpdateCount(string query = "")
    {
        int total = _state.Terminals.Count + _state.InsertionPoints.Count +
                    _state.UnlockGates.Count + _state.ExtraTags.Count;
        int visible = TerminalsList.Items.Count + InsertionList.Items.Count +
                      GatesList.Items.Count + ExtraList.Items.Count;
        ItemCountText.Text = query.Length == 0
            ? L.Format("collectibles.item_count", total)
            : L.Format("collectibles.visible_of_total", visible, total);
    }
}
