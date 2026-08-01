using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class VehicleWorkshopPage : Page
{
    private readonly RuntimeTagMemoryService _game = RuntimeTagMemoryService.Current;
    private readonly VehicleWorkshopService _vehicles = new();
    private IReadOnlyList<LoadableVehicle> _all = [];
    private LoadableVehicle? _selected;
    private bool _busy;
    private bool _hasScanned;

    public VehicleWorkshopPage()
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
            _all = await Task.Run(_vehicles.Connect);
            _hasScanned = true;
            SearchBox.IsEnabled = true;
            ApplyFilter();
            ShowStatus(
                L.Format("vehicle_workshop.found_vehicles", _all.Count),
                InfoBarSeverity.Success);
        });
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        int selectedIndex = _selected?.Tag.Index ?? -1;
        await RunBusy(async () =>
        {
            _all = await Task.Run(_vehicles.Refresh);
            _selected = _all.FirstOrDefault(item => item.Tag.Index == selectedIndex);
            ApplyFilter();
            ShowSelection();
        });
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string query = SearchBox.Text.Trim();
        LoadableVehicle[] filtered = _all
            .Where(vehicle =>
                query.Length == 0 ||
                vehicle.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                vehicle.TagPath.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        VehicleList.ItemsSource = filtered;
        VehicleList.SelectedItem = _selected;
        CountText.Text = L.Format(
            "vehicle_workshop.vehicles_shown_count",
            filtered.Length,
            _all.Count);
    }

    private void OnVehicleClicked(object sender, ItemClickEventArgs e)
    {
        _selected = e.ClickedItem as LoadableVehicle;
        ShowSelection();
    }

    private void ShowSelection()
    {
        bool selected = _selected is not null;
        EmptyState.Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
        SelectionDetails.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        SelectedVehicleText.Text = _selected?.Name ?? "";
        SelectedPathText.Text = _selected?.TagPath ?? "";
        SelectedDatumText.Text = _selected?.Detail ?? "";
        EnablePlayerControlButton.Visibility =
            VehicleWorkshopService.IsPelican(_selected)
                ? Visibility.Visible
                : Visibility.Collapsed;
        UpdateControls();
    }

    private async void OnSpawn(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } vehicle) return;
        await RunBusy(async () =>
        {
            ScriptExecutionResult result = await _vehicles.SpawnAsync(vehicle);
            ShowStatus(
                L.Format("vehicle_workshop.spawned_ahead", vehicle.Name, result.Message),
                InfoBarSeverity.Success);
        });
    }

    private async void OnEnablePlayerControl(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } vehicle) return;
        await RunBusy(async () =>
        {
            VehiclePlayerControlResult result = await Task.Run(
                () => _vehicles.EnablePelicanPlayerControl(vehicle));
            ShowStatus(result.Message, InfoBarSeverity.Success);
        });
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
        ScriptingBridgeStatus bridge = _vehicles.BridgeStatus;
        ScanButton.IsEnabled = !_busy && _game.IsConnected;
        RefreshButton.IsEnabled = !_busy && _game.IsConnected && _hasScanned;
        SpawnButton.IsEnabled =
            !_busy && _selected is not null && _game.IsConnected &&
            bridge.IsRuntimeReady && !bridge.IsStale;
        EnablePlayerControlButton.IsEnabled =
            !_busy && _game.IsConnected &&
            VehicleWorkshopService.IsPelican(_selected);
        ConnectionText.Text = _hasScanned
            ? L.Format("vehicle_workshop.loaded_summary", _all.Count, bridge.Summary)
            : bridge.Summary;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _game.ConnectionChanged -= OnConnectionChanged;
        _vehicles.Dispose();
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }
}
