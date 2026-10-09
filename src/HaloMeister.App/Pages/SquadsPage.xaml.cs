using System.ComponentModel;
using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class SquadsPage : Page, IActivatablePage
{
    private readonly RuntimeTagMemoryService _game = RuntimeTagMemoryService.Current;
    private readonly ScenarioSquadsService _squads = new();
    private readonly PlayerToolsService _playerTools = new();
    private IReadOnlyList<ScenarioSquadInfo> _all = [];
    private SquadRow[] _rows = [];
    private bool _busy;
    private bool _hasScanned;

    public SquadsPage()
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
        await RunBusy(async () =>
        {
            ScenarioSquadsSession session = await Task.Run(_squads.Scan);
            _all = FilterScaffoldSquads(session.Squads);
            ApplyFilter();
            ShowStatus(
                L.Format("squads.refreshed_squads", _all.Count),
                InfoBarSeverity.Success);
        });
    }

    private async void OnPlace(object sender, RoutedEventArgs e)
    {
        if (SquadOf(sender) is not { } squad) return;
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
        if (SquadOf(sender) is not { } squad) return;
        await RunBusy(async () =>
        {
            ScriptExecutionResult result = await _squads.EraseAsync(squad);
            ShowScriptResult(
                result,
                L.Format("squads.erased_submitted", squad.Name));
        });
    }

    private async void OnTeleport(object sender, RoutedEventArgs e)
    {
        if (SquadOf(sender) is not { } squad) return;
        if (squad.SpawnPoints.Count == 0)
        {
            ShowStatus(L.Get("squads.error_no_spawn_points"), InfoBarSeverity.Warning);
            return;
        }

        SpawnPointOption? choice = await ShowTeleportDialogAsync(squad);
        if (choice is null)
            return;

        await RunBusy(async () =>
        {
            await _playerTools.TeleportAsync(
                new PlayerCoordinates(choice.Point.X, choice.Point.Y, choice.Point.Z));
            ShowStatus(
                L.Format("squads.teleported_to_point", choice.Number),
                InfoBarSeverity.Success);
        });
    }

    private async Task<SpawnPointOption?> ShowTeleportDialogAsync(ScenarioSquadInfo squad)
    {
        SpawnPointOption[] options = squad.SpawnPoints
            .Select((point, index) => new SpawnPointOption(index + 1, point))
            .ToArray();
        var list = new ListView
        {
            ItemsSource = options,
            MinWidth = 280,
            MaxHeight = 360,
            SelectionMode = ListViewSelectionMode.Single,
            SelectedIndex = 0,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = L.Get("squads.teleport_dialog_title"),
            Content = list,
            PrimaryButtonText = L.Get("squads.teleport"),
            CloseButtonText = L.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogMaxHeight"] = 560.0;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;
        return list.SelectedItem as SpawnPointOption ?? options[0];
    }

    private static ScenarioSquadInfo? SquadOf(object sender) =>
        (sender as FrameworkElement)?.DataContext is SquadRow row ? row.Squad : null;

    private async void OnInvincible(object sender, RoutedEventArgs e) =>
        await SetInvincibleAsync(SquadOf(sender), true);

    private async void OnMortal(object sender, RoutedEventArgs e) =>
        await SetInvincibleAsync(SquadOf(sender), false);

    private async void OnAlly(object sender, RoutedEventArgs e) =>
        await SetAllegianceAsync(SquadOf(sender), true);

    private async void OnHostile(object sender, RoutedEventArgs e) =>
        await SetAllegianceAsync(SquadOf(sender), false);

    private async Task SetInvincibleAsync(ScenarioSquadInfo? squad, bool invincible)
    {
        if (squad is null) return;
        await RunBusy(async () =>
        {
            ScriptExecutionResult result = await _squads.SetInvincibleAsync(
                squad,
                invincible);
            ShowScriptResult(
                result,
                L.Format(
                    invincible
                        ? "squads.invincible_submitted"
                        : "squads.invincible_off_submitted",
                    squad.Name));
        });
    }

    private async Task SetAllegianceAsync(ScenarioSquadInfo? squad, bool allied)
    {
        if (squad is null) return;
        await RunBusy(async () =>
        {
            ScriptExecutionResult result = await _squads.SetAllegianceAsync(
                squad,
                allied);
            ShowScriptResult(
                result,
                L.Format(
                    allied ? "squads.ally_submitted" : "squads.hostile_submitted",
                    squad.Name));
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
        _rows = filtered.Select(squad => new SquadRow(squad)).ToArray();
        SquadList.ItemsSource = _rows;
        CountText.Text = L.Format(
            "squads.shown_count",
            filtered.Length,
            _all.Count);
        UpdateControls();
    }

    private void ShowScriptResult(ScriptExecutionResult result, string submittedMessage)
    {
        if (result.Outcome == ScriptOutcome.Failed)
        {
            ShowStatus(UserFacingErrors.FromBridge(result.Message), InfoBarSeverity.Error);
            return;
        }

        ShowStatus(
            result.Outcome == ScriptOutcome.Submitted
                ? submittedMessage
                : submittedMessage,
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
        catch (Exception ex) { ShowStatus(UserFacingErrors.Format(ex), InfoBarSeverity.Error); }
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
        bool ready = !_busy && _game.IsConnected;
        ScanButton.IsEnabled = ready;
        RefreshButton.IsEnabled = ready && _hasScanned;
        foreach (SquadRow row in _rows)
            row.SetReady(ready);
        BusyRing.IsActive = _busy;
        ConnectionText.Text = _hasScanned
            ? L.Format("squads.loaded_summary", _all.Count)
            : _game.IsConnected
                ? L.Get("squads.connected_scan_hint")
                : L.Get("squads.disconnected");
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
        => MainWindow.Instance?.Report(message, severity);

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

    private sealed class SquadRow : INotifyPropertyChanged
    {
        private bool _ready;

        public SquadRow(ScenarioSquadInfo squad) => Squad = squad;

        public ScenarioSquadInfo Squad { get; }
        public string ListTitle => Squad.ListTitle;
        public string TeamDisplay => Squad.TeamDisplay;
        public bool CanAct => _ready && Squad.CanScript;
        public bool CanTeleport => _ready && Squad.SpawnPoints.Count > 0;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void SetReady(bool ready)
        {
            if (_ready == ready)
                return;
            _ready = ready;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanAct)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanTeleport)));
        }
    }

    private sealed record SpawnPointOption(int Number, ScenarioSquadSpawnPoint Point)
    {
        public string Title => L.Format("squads.spawn_point_option", Number);
        public override string ToString() => Title;
    }
}
