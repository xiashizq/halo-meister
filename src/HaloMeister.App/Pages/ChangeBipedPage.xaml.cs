using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace HaloMeister.App.Pages;

public sealed partial class ChangeBipedPage : Page
{
    private readonly RuntimeTagMemoryService _game = RuntimeTagMemoryService.Current;
    private readonly PlayerBipedService _bipeds = new();
    private IReadOnlyList<PlayerBipedChoice> _allChoices = [];
    private PlayerBipedChoice? _selected;
    private bool _busy;
    private bool _hasScanned;
    private bool _collisionSwitchEnabled;

    public ChangeBipedPage()
    {
        InitializeComponent();
        _game.ConnectionChanged += OnGameConnectionChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateChrome();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_game.IsConnected && !_hasScanned)
            await ScanAsync(connectGame: false);
    }

    private async void OnConnectAndScan(object sender, RoutedEventArgs e)
        => await ScanAsync(connectGame: false);

    private async Task ScanAsync(bool connectGame)
    {
        await RunBusy(async () =>
        {
            if (connectGame && !_game.IsConnected)
                await Task.Run(_game.Connect);

            PlayerBipedSession session = await Task.Run(_bipeds.Connect);
            await _bipeds.WarmUpAsync();
            _hasScanned = true;
            ShowSession(session);
            ShowStatus(
                L.Format("change_biped.ready_found_bipeds", session.Choices.Count),
                InfoBarSeverity.Success);
        });
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            PlayerBipedSession session = await Task.Run(_bipeds.Refresh);
            ShowSession(session);
            ShowStatus(
                L.Format("change_biped.refreshed_bipeds", session.Choices.Count),
                InfoBarSeverity.Success);
        });
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnBipedSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _selected = BipedList.SelectedItem as PlayerBipedChoice;
        ShowSelection();
    }

    private async void OnSwitchNow(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } selected) return;

        await RunBusy(async () =>
        {
            ScriptExecutionResult result =
                await _bipeds.SpawnForBumpPossessionAsync(selected);
            _collisionSwitchEnabled = true;
            ShowStatus(
                L.Format("change_biped.spawned_switch_enabled", selected.Name),
                InfoBarSeverity.Success);
            DiagnosticText.Text =
                $"request={result.RequestId} · outcome={result.Outcome} · " +
                $"elapsed={result.Elapsed.TotalSeconds:F2}s · {result.Message}";
        });
    }

    private async void OnDisableSwitch(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            ScriptExecutionResult result =
                await _bipeds.DisableBumpPossessionAsync();
            _collisionSwitchEnabled = false;
            ShowStatus(L.Get("change_biped.collision_switch_off"), InfoBarSeverity.Success);
            DiagnosticText.Text =
                $"request={result.RequestId} · outcome={result.Outcome} · " +
                $"elapsed={result.Elapsed.TotalSeconds:F2}s · {result.Message}";
        });
    }

    private async void OnApplyOverride(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } selected) return;

        await RunBusy(async () =>
        {
            await Task.Run(() => _bipeds.Apply(selected));
            ShowStatus(
                L.Format("change_biped.next_checkpoint_will_create", selected.Name),
                InfoBarSeverity.Success);
        });
    }

    private async void OnRestoreOverride(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            await Task.Run(_bipeds.Restore);
            ShowStatus(
                L.Get("change_biped.restored_original_representation"),
                InfoBarSeverity.Success);
        });
    }

    private void ShowSession(PlayerBipedSession session)
    {
        _allChoices = session.Choices;
        SearchBox.IsEnabled = true;
        ApplyFilter();

        PlayerBipedChoice? active = session.Choices.FirstOrDefault(choice =>
            choice.BipedTag.Index == session.ActiveBiped.Index);
        _selected = active ?? session.Choices.FirstOrDefault();
        BipedList.SelectedItem = _selected;
        ShowSelection();
    }

    private void ApplyFilter()
    {
        string query = SearchBox.Text.Trim();
        PlayerBipedChoice[] filtered = _allChoices
            .Where(choice =>
                query.Length == 0 ||
                choice.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                choice.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                choice.TagPath.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        BipedList.ItemsSource = filtered;
        CountText.Text = L.Format(
            "change_biped.count_filtered",
            filtered.Length,
            _allChoices.Count);
        if (_selected is not null &&
            filtered.Any(choice => choice.BipedTag.Index == _selected.BipedTag.Index))
            BipedList.SelectedItem = _selected;
    }

    private void ShowSelection()
    {
        SelectedNameText.Text = _selected?.Name ?? L.Get("change_biped.select_a_character");
        SelectedCategoryText.Text = _selected is null
            ? L.Get("change_biped.choose_loaded_biped")
            : $"{_selected.Category}{(_selected.IsOriginal ? L.Get("change_biped.current_player_suffix") : string.Empty)}";
        SelectedPathText.Text = _selected?.TagPath ?? string.Empty;
        UpdateChrome();
    }

    private async Task RunBusy(Func<Task> action)
    {
        if (_busy) return;

        _busy = true;
        BusyRing.IsActive = true;
        UpdateChrome();
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
            DiagnosticText.Text =
                $"{DateTimeOffset.Now:HH:mm:ss} · {ex.GetType().Name} · {ex.Message}";
        }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            UpdateChrome();
        }
    }

    private void OnGameConnectionChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(UpdateChrome);

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _game.ConnectionChanged -= OnGameConnectionChanged;
        _bipeds.Dispose();
    }

    private void UpdateChrome()
    {
        ScriptingBridgeStatus bridge = _bipeds.BridgeStatus;
        bool gameReady = _game.IsConnected;
        bool bridgeReady = bridge.IsRuntimeReady && !bridge.IsStale;

        SetState(
            GameStateDot,
            GameStateText,
            gameReady,
            gameReady
                ? L.Format("change_biped.connected_pid", _game.ProcessId)
                : L.Get("change_biped.disconnected"));
        SetState(
            BridgeStateDot,
            BridgeStateText,
            bridgeReady,
            bridgeReady
                ? L.Format("change_biped.ready_version", bridge.RunningVersion)
                : L.Get("change_biped.not_ready"));
        SetState(
            ScanStateDot,
            ScanStateText,
            _hasScanned,
            _hasScanned
                ? L.Format("change_biped.bipeds_loaded", _allChoices.Count)
                : L.Get("change_biped.not_scanned"));

        ConnectScanButton.Content = L.Get("change_biped.scan_mission");
        ConnectScanButton.IsEnabled = !_busy && gameReady;
        RefreshButton.IsEnabled = !_busy && gameReady && _hasScanned;

        SwitchNowButton.IsEnabled =
            !_busy && _selected is not null && gameReady && bridgeReady;
        ApplyOverrideButton.IsEnabled =
            !_busy && _selected is not null && gameReady && _hasScanned;
        RestoreOverrideButton.IsEnabled = !_busy && _bipeds.CanRestore;
        DisableSwitchButton.IsEnabled =
            !_busy && gameReady && bridgeReady && _collisionSwitchEnabled;

        SwitchAvailabilityText.Text = !gameReady
            ? L.Get("change_biped.connect_and_scan_first")
            : !bridgeReady
                ? bridge.Summary
                : _selected is null
                    ? L.Get("change_biped.select_from_mission_list")
                    : L.Get("change_biped.ready_to_switch");
    }

    private static void SetState(
        Ellipse indicator,
        TextBlock label,
        bool ready,
        string text)
    {
        indicator.Fill = new SolidColorBrush(
            ready ? Colors.LimeGreen : Colors.Gray);
        label.Text = text;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Title = severity switch
        {
            InfoBarSeverity.Error => L.Get("change_biped.switch_failed"),
            InfoBarSeverity.Success => L.Get("change_biped.switch_success"),
            _ => L.Get("change_biped.change_character"),
        };
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }
}
