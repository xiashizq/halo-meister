using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class SquadsPage : Page
{
    private readonly RuntimeTagMemoryService _game = RuntimeTagMemoryService.Current;
    private readonly ScenarioSquadsService _squads = new();
    private readonly PlayerToolsService _playerTools = new();
    private IReadOnlyList<ScenarioSquadInfo> _all = [];
    private ScenarioSquadInfo? _selected;
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
            _all = FilterScaffoldSquads(session.Squads);
            _hasScanned = true;
            SearchBox.IsEnabled = true;
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
            _all = FilterScaffoldSquads(session.Squads);
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
                L.Format("squads.placed_submitted", squad.Name));
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
                L.Format("squads.erased_submitted", squad.Name));
        });
    }

    private async void OnTeleportToSpawnPoint(object sender, RoutedEventArgs e)
    {
        if (_busy ||
            sender is not FrameworkElement { Tag: ScenarioSquadSpawnPoint point })
            return;

        await RunBusy(async () =>
        {
            await _playerTools.TeleportAsync(
                new PlayerCoordinates(point.X, point.Y, point.Z));
            ShowStatus(
                L.Format("squads.teleported_to_spawn", point.Display),
                InfoBarSeverity.Success);
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
        TeamText.Text = L.Format("squads.team_label", _selected.TeamDisplay);
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
            ? L.Format("squads.loaded_summary", _all.Count)
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

    private static IReadOnlyList<ScenarioSquadInfo> FilterScaffoldSquads(
        IReadOnlyList<ScenarioSquadInfo> squads) =>
        squads
            .Where(squad => !IsScaffoldSquad(squad))
            .ToArray();

    private static bool IsScaffoldSquad(ScenarioSquadInfo squad) =>
        IsScaffoldName(squad.Name) || IsScaffoldName(squad.ScriptName);

    private static bool IsScaffoldName(string name) =>
        string.Equals(
            name.Trim(),
            EnemySpawnerService.DedicatedAllySquadName,
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            name.Trim(),
            EnemySpawnerService.DedicatedHostileSquadName,
            StringComparison.OrdinalIgnoreCase);
}
