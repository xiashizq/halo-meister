using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class ScenarioPropsPage : Page, IActivatablePage
{
    private readonly RuntimeTagMemoryService _game = RuntimeTagMemoryService.Current;
    private readonly ScenarioPropsService _props = new(RuntimeTagMemoryService.Current);
    private IReadOnlyList<ScenarioPropPlacement> _placements = [];
    private IReadOnlyList<ScenarioPropGroup> _groups = [];
    private ScenarioPropGroup? _selectedGroup;
    private int _skipped;
    private bool _busy;
    private bool _hasScanned;

    public ScenarioPropsPage()
    {
        InitializeComponent();
        _game.ConnectionChanged += OnConnectionChanged;
        UpdateControls();
    }

    public void OnActivated() => UpdateControls();

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            ScenarioPropsSession session = await Task.Run(_props.Scan);
            ApplySession(session, restoreSelection: false);
            ShowStatus(
                L.Format("scenario_props.found", _placements.Count, _skipped),
                InfoBarSeverity.Success);
        });
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        string? selectedKey = _selectedGroup?.Key;
        await RunBusy(async () =>
        {
            ScenarioPropsSession session = await Task.Run(_props.Scan);
            ApplySession(session, restoreSelection: true, selectedKey);
            ShowStatus(
                L.Format("scenario_props.refreshed", _placements.Count, _skipped),
                InfoBarSeverity.Success);
        });
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter(restoreSelection: true);

    private void OnGroupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedGroup = GroupList.SelectedItem as ScenarioPropGroup;
        ShowSelection();
    }

    private async void OnHidePlacement(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ScenarioPropPlacement placement })
            return;
        await SubmitAsync([placement.ScriptName], hidden: true);
    }

    private async void OnShowPlacement(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ScenarioPropPlacement placement })
            return;
        await SubmitAsync([placement.ScriptName], hidden: false);
    }

    private async void OnHideGroup(object sender, RoutedEventArgs e)
    {
        if (_selectedGroup is null) return;
        await SubmitAsync(
            _selectedGroup.Placements.Select(item => item.ScriptName).ToArray(),
            hidden: true);
    }

    private async void OnShowGroup(object sender, RoutedEventArgs e)
    {
        if (_selectedGroup is null) return;
        await SubmitAsync(
            _selectedGroup.Placements.Select(item => item.ScriptName).ToArray(),
            hidden: false);
    }

    private async Task SubmitAsync(IReadOnlyList<string> names, bool hidden)
    {
        if (_busy || names.Count == 0) return;
        await RunBusy(async () =>
        {
            ScenarioPropScriptSubmission submission = await _props.SetHiddenAsync(names, hidden);
            string message = names.Count == 1
                ? L.Format(
                    hidden ? "scenario_props.hide_submitted" : "scenario_props.show_submitted",
                    names[0])
                : L.Format(
                    hidden ? "scenario_props.hide_submitted_many" : "scenario_props.show_submitted_many",
                    names.Count);
            ShowScriptResult(submission.Result, message);
        });
    }

    private void ApplySession(
        ScenarioPropsSession session,
        bool restoreSelection,
        string? selectedKey = null)
    {
        _placements = session.Placements;
        _skipped = session.SkippedCount;
        _hasScanned = true;
        SearchBox.IsEnabled = true;
        _groups = ScenarioPropGroup.FromPlacements(_placements);
        ApplyFilter(restoreSelection, selectedKey);
    }

    private void ApplyFilter(bool restoreSelection, string? selectedKey = null)
    {
        string query = SearchBox.Text.Trim();
        string? key = restoreSelection ? selectedKey ?? _selectedGroup?.Key : null;
        ScenarioPropGroup[] filtered = _groups
            .Where(group =>
                query.Length == 0 ||
                group.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        GroupList.ItemsSource = filtered;
        GroupList.SelectedItem = key is null
            ? null
            : filtered.FirstOrDefault(group => group.Key == key);
        CountText.Text = _hasScanned
            ? L.Format("scenario_props.shown_count", filtered.Length, _groups.Count, _skipped)
            : L.Get("scenario_props.not_scanned");
        if (GroupList.SelectedItem is null)
        {
            _selectedGroup = null;
            ShowSelection();
        }
    }

    private void ShowSelection()
    {
        bool selected = _selectedGroup is not null;
        EmptyState.Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
        SelectionDetails.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        if (_selectedGroup is null)
        {
            PlacementList.ItemsSource = null;
            UpdateControls();
            return;
        }

        SelectedGroupText.Text = _selectedGroup.Title;
        SelectedGroupDetailText.Text = _selectedGroup.Detail;
        PlacementList.ItemsSource = _selectedGroup.Placements;
        UpdateControls();
    }

    private void ShowScriptResult(ScriptExecutionResult result, string submittedMessage)
    {
        if (result.Outcome == ScriptOutcome.Failed)
        {
            ShowStatus(result.Message, InfoBarSeverity.Error);
            return;
        }

        ShowStatus(
            result.Outcome == ScriptOutcome.Submitted
                ? submittedMessage
                : result.Message,
            result.Outcome == ScriptOutcome.Confirmed
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Warning);
    }

    private async Task RunBusy(Func<Task> action)
    {
        _busy = true;
        UpdateControls();
        try { await action(); }
        catch (Exception ex) { ShowStatus(ex.Message, InfoBarSeverity.Error); }
        finally
        {
            _busy = false;
            UpdateControls();
        }
    }

    private void OnConnectionChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(UpdateControls);

    private void UpdateControls()
    {
        bool connected = _game.IsConnected;
        ScanButton.IsEnabled = !_busy && connected;
        RefreshButton.IsEnabled = !_busy && connected && _hasScanned;
        bool canGroup = !_busy && connected && _selectedGroup is { Placements.Count: > 0 };
        HideGroupButton.IsEnabled = canGroup;
        ShowGroupButton.IsEnabled = canGroup;
        BusyRing.IsActive = _busy;
        ConnectionText.Text = _hasScanned
            ? L.Format("scenario_props.loaded_summary", _placements.Count, _skipped)
            : connected
                ? L.Get("scenario_props.connected_scan_hint")
                : L.Get("scenario_props.disconnected");
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }
}
