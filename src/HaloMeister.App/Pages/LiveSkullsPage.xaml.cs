using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class LiveSkullsPage : Page, IActivatablePage
{
    private readonly LiveSkullsService _skulls = new();
    private IReadOnlyList<LiveSkullItem> _items = [];
    private readonly Dictionary<string, bool> _loaded =
        new(StringComparer.Ordinal);
    private bool _busy;
    private bool _loading;

    public LiveSkullsPage()
    {
        InitializeComponent();
        CategoryBox.Items.Add(L.Get("live_skulls.filter_all"));
        foreach (LiveSkullCategory category in Enum.GetValues<LiveSkullCategory>())
            CategoryBox.Items.Add(LiveSkullMetadata.CategoryLabel(category));
        CategoryBox.SelectedIndex = 0;
        UpdateBridgeStatus();
        UpdateButtons();
    }

    public void OnActivated()
    {
        UpdateBridgeStatus();
        UpdateButtons();
        if (_items.Count == 0 &&
            _skulls.BridgeStatus is { IsRuntimeReady: true, IsStale: false })
        {
            _ = RefreshAsync();
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
        => await RefreshAsync();

    private async Task RefreshAsync()
    {
        await RunBusy(async () =>
        {
            IReadOnlyList<LiveSkullItem> items = await _skulls.ReadAsync();
            ShowItems(items);
            ShowStatus(
                L.Format("live_skulls.read_all_success", items.Count),
                InfoBarSeverity.Success);
        });
    }

    private async void OnDisableAll(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            LiveSkullItem[] enabled = _items
                .Where(item => item.IsEnabled)
                .ToArray();
            foreach (LiveSkullItem item in enabled)
            {
                await _skulls.SetAsync(item.Name, false);
                item.IsEnabled = false;
                _loaded[item.Name] = false;
            }
            ShowItems(await _skulls.ReadAsync());
            ShowStatus(
                enabled.Length == 0
                    ? L.Get("live_skulls.all_already_off")
                    : L.Format("live_skulls.turned_off_count", enabled.Length),
                InfoBarSeverity.Success);
        });
    }

    private async void OnSkullToggled(object sender, RoutedEventArgs e)
    {
        if (_loading ||
            _busy ||
            sender is not ToggleSwitch toggle ||
            toggle.DataContext is not LiveSkullItem item ||
            !_loaded.TryGetValue(item.Name, out bool previous) ||
            previous == toggle.IsOn)
        {
            return;
        }

        _busy = true;
        BusyRing.IsActive = true;
        UpdateButtons();
        try
        {
            await _skulls.SetAsync(item.Name, toggle.IsOn);
            _loaded[item.Name] = toggle.IsOn;
            item.IsEnabled = toggle.IsOn;
            UpdateSummary();
            ShowStatus(
                L.Format(
                    "live_skulls.item_now_status",
                    item.DisplayName,
                    L.Get(toggle.IsOn ? "cheat_globals.on" : "cheat_globals.off")),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _loading = true;
            toggle.IsOn = previous;
            item.IsEnabled = previous;
            _loading = false;
            ShowStatus(UserFacingErrors.Format(ex), InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            UpdateBridgeStatus();
            UpdateButtons();
        }
    }

    private void OnFilterChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        => ApplyFilter();

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
        => ApplyFilter();

    private void OnEnabledOnlyChanged(object sender, RoutedEventArgs e)
        => ApplyFilter();

    private void ApplyFilter()
    {
        string query = FilterBox.Text.Trim();
        int categoryIndex = CategoryBox.SelectedIndex;
        LiveSkullCategory? category = categoryIndex > 0
            ? Enum.GetValues<LiveSkullCategory>()[categoryIndex - 1]
            : null;
        bool enabledOnly = EnabledOnlyToggle.IsChecked == true;

        LiveSkullItem[] shown = _items
            .Where(item =>
                (category is null || item.Category == category) &&
                (!enabledOnly || item.IsEnabled) &&
                (query.Length == 0 ||
                 item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 item.EnglishName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 item.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 item.Description.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        // Rebuilding the list re-fires Toggled for every row; ShowItems/ToggleSwitch guard on _loading.
        bool wasLoading = _loading;
        _loading = true;
        SkullsList.ItemsSource = shown;
        _loading = wasLoading;

        bool empty = shown.Length == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        SkullsList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Text = _items.Count == 0
            ? L.Get("live_skulls.load_mission_then_refresh")
            : L.Get("live_skulls.no_match");
    }
    private void ShowItems(IReadOnlyList<LiveSkullItem> items)
    {
        _loading = true;
        _items = items;
        _loaded.Clear();
        foreach (LiveSkullItem item in items)
            _loaded[item.Name] = item.IsEnabled;
        ApplyFilter();
        _loading = false;
        UpdateSummary();
        UpdateButtons();
    }

    private async Task RunBusy(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        BusyRing.IsActive = true;
        UpdateButtons();
        try { await action(); }
        catch (Exception ex) { ShowStatus(UserFacingErrors.Format(ex), InfoBarSeverity.Error); }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            UpdateBridgeStatus();
            UpdateButtons();
        }
    }

    private void UpdateSummary()
    {
        int enabled = _items.Count(item => item.IsEnabled);
        SummaryText.Text = _items.Count == 0
            ? L.Get("live_skulls.load_mission_then_refresh")
            : L.Format("live_skulls.enabled_summary", enabled, _items.Count);
    }

    private void UpdateButtons()
    {
        ScriptingBridgeStatus bridge = _skulls.BridgeStatus;
        bool ready = !_busy && bridge.IsRuntimeReady && !bridge.IsStale;
        RefreshButton.IsEnabled = ready;
        DisableAllButton.IsEnabled =
            ready && _items.Any(item => item.IsEnabled);
        FilterBox.IsEnabled = !_busy;
        SkullsList.IsEnabled = !_busy && _items.Count > 0;
    }

    private void UpdateBridgeStatus()
    {
        BridgeStatusText.Text = _skulls.BridgeStatus.Summary;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        if (severity == InfoBarSeverity.Success)
        {
            MainWindow.Instance?.DismissStatus();
            SummaryText.Text = message;
            return;
        }

        MainWindow.Instance?.Report(message, severity);
    }
}
