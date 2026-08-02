using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class SquadsPage : Page
{
    private readonly RuntimeTagMemoryService _game = RuntimeTagMemoryService.Current;
    private readonly ScenarioSquadsService _squads = new();
    private IReadOnlyList<ScenarioSquadInfo> _all = [];
    private ScenarioSquadInfo? _selected;
    private string _scenarioPath = "";
    private bool _busy;
    private bool _hasScanned;

    public SquadsPage()
    {
        InitializeComponent();
        _game.ConnectionChanged += OnConnectionChanged;
        Unloaded += OnUnloaded;
        UpdateControls();
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            ScenarioSquadsSession session = await Task.Run(_squads.Scan);
            _all = session.Squads;
            _scenarioPath = session.ScenarioPath;
            _hasScanned = true;
            SearchBox.IsEnabled = true;
            ScenarioPathText.Text = L.Format("squads.scenario_path", _scenarioPath);
            ApplyFilter();
            ShowStatus(
                L.Format("squads.found_squads", _all.Count),
                InfoBarSeverity.Success);
        });
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        int selectedIndex = _selected?.Index ?? -1;
        await RunBusy(async () =>
        {
            ScenarioSquadsSession session = await Task.Run(_squads.Scan);
            _all = session.Squads;
            _scenarioPath = session.ScenarioPath;
            ScenarioPathText.Text = L.Format("squads.scenario_path", _scenarioPath);
            _selected = _all.FirstOrDefault(item => item.Index == selectedIndex);
            ApplyFilter();
            ShowSelection();
            ShowStatus(
                L.Format("squads.refreshed_squads", _all.Count),
                InfoBarSeverity.Success);
        });
    }

    private async void OnPlace(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        ScenarioSquadInfo squad = _selected;
        await RunBusy(async () =>
        {
            ScriptExecutionResult result = await _squads.PlaceAsync(squad);
            ShowScriptResult(
                result,
                L.Format("squads.placed_submitted", squad.ScriptName));
        });
    }

    private async void OnErase(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        ScenarioSquadInfo squad = _selected;
        await RunBusy(async () =>
        {
            ScriptExecutionResult result = await _squads.EraseAsync(squad);
            ShowScriptResult(
                result,
                L.Format("squads.erased_submitted", squad.ScriptName));
        });
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string query = SearchBox.Text.Trim();
        ScenarioSquadInfo[] filtered = _all
            .Where(squad =>
                query.Length == 0 ||
                squad.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        SquadList.ItemsSource = filtered;
        SquadList.SelectedItem = _selected;
        CountText.Text = L.Format(
            "squads.shown_count",
            filtered.Length,
            _all.Count);
    }

    private void OnSquadClicked(object sender, ItemClickEventArgs e)
    {
        _selected = e.ClickedItem as ScenarioSquadInfo;
        ShowSelection();
    }

    private void ShowSelection()
    {
        bool selected = _selected is not null;
        EmptyState.Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
        SelectionDetails.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        if (_selected is null)
        {
            UpdateControls();
            return;
        }

        SelectedNameText.Text = _selected.Name;
        SelectedIndexText.Text = L.Format("squads.index_label", _selected.Index);
        FlagsText.Text = _selected.FlagNames.Count == 0
            ? L.Format("squads.flags_none", _selected.FlagsHex)
            : L.Format(
                "squads.flags_label",
                _selected.FlagsHex,
                string.Join(", ", _selected.FlagNames));
        TeamText.Text = L.Format("squads.team_label", _selected.TeamDisplay);
        ParentText.Text = L.Format("squads.parent_label", _selected.ParentDisplay);
        ZoneText.Text = L.Format("squads.zone_label", _selected.InitialZoneDisplay);
        ObjectiveText.Text = L.Format(
            "squads.objective_label",
            _selected.InitialObjectiveDisplay);
        TaskText.Text = L.Format("squads.task_label", _selected.InitialTaskDisplay);
        FolderText.Text = L.Format("squads.folder_label", _selected.EditorFolderDisplay);
        SpawnCountsText.Text = L.Format(
            "squads.spawn_counts",
            _selected.SpawnPointCount,
            _selected.SpawnFormationCount);
        bool hasSpawnPoints = _selected.SpawnPoints.Count > 0;
        SpawnPointsHeaderText.Visibility = Visibility.Visible;
        SpawnPointsList.ItemsSource = _selected.SpawnPoints;
        SpawnPointsList.Visibility =
            hasSpawnPoints ? Visibility.Visible : Visibility.Collapsed;
        SpawnPointsEmptyText.Visibility =
            hasSpawnPoints ? Visibility.Collapsed : Visibility.Visible;
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
        if (_busy) return;
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
        bool canAct =
            !_busy &&
            _game.IsConnected &&
            _selected is not null &&
            _selected.CanScript;
        ScanButton.IsEnabled = !_busy && _game.IsConnected;
        RefreshButton.IsEnabled = !_busy && _game.IsConnected && _hasScanned;
        PlaceButton.IsEnabled = canAct;
        EraseButton.IsEnabled = canAct;
        ConnectionText.Text = _hasScanned
            ? L.Format("squads.loaded_summary", _all.Count, _scenarioPath)
            : _game.IsConnected
                ? L.Get("squads.connected_scan_hint")
                : L.Get("squads.disconnected");
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _game.ConnectionChanged -= OnConnectionChanged;
        _squads.Dispose();
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }
}
