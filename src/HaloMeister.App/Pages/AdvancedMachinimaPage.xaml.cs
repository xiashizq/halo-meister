using System.Globalization;
using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class AdvancedMachinimaPage : Page, IActivatablePage
{
    private readonly AdvancedMachinimaService _machinima =
        AdvancedMachinimaService.Current;
    private MachinimaState? _state;
    private bool _busy;

    public AdvancedMachinimaPage()
    {
        InitializeComponent();
        ReloadSavedLocations();
        UpdateDisplay();
    }

    public void OnActivated()
    {
        UpdateDisplay();
        if (!_busy)
            _ = RunBusy(() => RefreshAsync(showSuccess: false));
    }

    private async void OnEnter(object sender, RoutedEventArgs e)
        => await RunBusy(async () =>
        {
            _state = await _machinima.EnterAsync();
            await RefreshNodesAsync();
            ShowStatus(
                L.Get("advanced_machinima.mode_active"),
                InfoBarSeverity.Success);
        });

    private async void OnExit(object sender, RoutedEventArgs e)
        => await RunBusy(async () =>
        {
            _state = await _machinima.ExitAsync();
            LiveNodeBox.ItemsSource = null;
            LiveNodeDetailText.Text = L.Get("advanced_machinima.no_authored_camera_node_selected");
            ShowStatus(
                L.Get("advanced_machinima.exit_restored"),
                InfoBarSeverity.Success);
        });

    private async void OnRefresh(object sender, RoutedEventArgs e)
        => await RunBusy(() => RefreshAsync(showSuccess: true));

    private async void OnMoveCameraToLive(object sender, RoutedEventArgs e)
    {
        if (LiveNodeBox.SelectedItem is not MachinimaNode node)
            return;
        await MoveCameraAsync(node.Transform, node.Name);
    }

    private async void OnMoveSpartanToLive(object sender, RoutedEventArgs e)
    {
        if (LiveNodeBox.SelectedItem is not MachinimaNode node)
            return;
        await TeleportSpartanAsync(node.Transform, node.Name);
    }

    private async void OnMoveCameraToSaved(object sender, RoutedEventArgs e)
    {
        if (SavedLocationBox.SelectedItem is not SavedMachinimaLocation location)
            return;
        await UseSavedLocationAsync(
            location,
            transform => _machinima.MoveCameraAsync(transform),
            L.Get("advanced_machinima.moved_camera_label"));
    }

    private async void OnMoveSpartanToSaved(object sender, RoutedEventArgs e)
    {
        if (SavedLocationBox.SelectedItem is not SavedMachinimaLocation location)
            return;
        await UseSavedLocationAsync(
            location,
            async transform =>
            {
                await _machinima.TeleportSpartanAsync(transform);
                return _state!;
            },
            L.Get("advanced_machinima.teleported_spartan_label"));
    }

    private async void OnSaveLocation(object sender, RoutedEventArgs e)
        => await RunBusy(async () =>
        {
            _state = await _machinima.ReadStateAsync();
            SavedMachinimaLocation saved =
                _machinima.SaveLocation(LocationNameBox.Text, _state);
            LocationNameBox.Text = "";
            ReloadSavedLocations(saved.Id);
            ShowStatus(
                L.Format("advanced_machinima.saved_location_with_transform", saved.Name),
                InfoBarSeverity.Success);
        });

    private void OnDeleteSaved(object sender, RoutedEventArgs e)
    {
        if (_busy ||
            SavedLocationBox.SelectedItem is not SavedMachinimaLocation location)
        {
            return;
        }
        try
        {
            _machinima.DeleteLocation(location.Id);
            ReloadSavedLocations();
            ShowStatus(
                L.Format("advanced_machinima.deleted_saved_location", location.Name),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OnLiveNodeSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        LiveNodeDetailText.Text =
            (LiveNodeBox.SelectedItem as MachinimaNode)?.Detail ??
            L.Get("advanced_machinima.no_authored_camera_node_selected");
        UpdateButtons();
    }

    private void OnSavedLocationSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        SavedLocationDetailText.Text =
            (SavedLocationBox.SelectedItem as SavedMachinimaLocation)?.Detail ??
            L.Get("advanced_machinima.no_saved_location_selected");
        UpdateButtons();
    }

    private async Task MoveCameraAsync(
        MachinimaTransform transform,
        string name)
        => await RunBusy(async () =>
        {
            _state = await _machinima.MoveCameraAsync(transform);
            ShowStatus(
                L.Format("advanced_machinima.moved_camera_to", name),
                InfoBarSeverity.Success);
        });

    private async Task TeleportSpartanAsync(
        MachinimaTransform transform,
        string name)
        => await RunBusy(async () =>
        {
            await _machinima.TeleportSpartanAsync(transform);
            ShowStatus(
                L.Format("advanced_machinima.teleported_spartan_to", name),
                InfoBarSeverity.Success);
        });

    private async Task UseSavedLocationAsync(
        SavedMachinimaLocation location,
        Func<MachinimaTransform, Task<MachinimaState>> action,
        string actionLabel)
        => await RunBusy(async () =>
        {
            if (_state is null)
                _state = await _machinima.ReadStateAsync();
            AdvancedMachinimaService.EnsureSameWorld(
                _state.WorldName,
                location);
            _state = await action(location.Transform);
            ShowStatus(
                L.Format("advanced_machinima.action_to_location", actionLabel, location.Name),
                InfoBarSeverity.Success);
        });

    private async Task RefreshAsync(bool showSuccess)
    {
        _state = await _machinima.ReadStateAsync();
        if (_state.IsEnabled)
            await RefreshNodesAsync();
        else
            LiveNodeBox.ItemsSource = null;
        ReloadSavedLocations(
            (SavedLocationBox.SelectedItem as SavedMachinimaLocation)?.Id);
        if (showSuccess)
        {
            ShowStatus(
                _state.IsEnabled
                    ? L.Get("advanced_machinima.refreshed_nodes")
                    : L.Get("advanced_machinima.not_active"),
                InfoBarSeverity.Success);
        }
    }

    private async Task RefreshNodesAsync()
    {
        if (_state is null)
            return;
        string? selectedId =
            (LiveNodeBox.SelectedItem as MachinimaNode)?.Id;
        IReadOnlyList<MachinimaNode> nodes =
            await _machinima.ReadLiveNodesAsync();
        LiveNodeBox.ItemsSource = nodes;
        LiveNodeBox.SelectedItem =
            nodes.FirstOrDefault(node => node.Id == selectedId) ??
            nodes.FirstOrDefault();
    }

    private void ReloadSavedLocations(Guid? selectedId = null)
    {
        IReadOnlyList<SavedMachinimaLocation> locations =
            _machinima.LoadSavedLocations();
        SavedLocationBox.ItemsSource = locations;
        SavedLocationBox.SelectedItem =
            locations.FirstOrDefault(location => location.Id == selectedId) ??
            locations.FirstOrDefault();
    }

    private async Task RunBusy(Func<Task> action)
    {
        if (_busy)
            return;
        _busy = true;
        BusyRing.IsActive = true;
        UpdateButtons();
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            UpdateDisplay();
        }
    }

    private void UpdateDisplay()
    {
        ScriptingBridgeStatus bridge = _machinima.BridgeStatus;
        ModeStatusText.Text = _state?.IsEnabled == true
            ? L.Format("advanced_machinima.mode_active_status", bridge.Summary)
            : L.Format("advanced_machinima.mode_inactive_status", bridge.Summary);
        if (_state is null)
        {
            PositionText.Text = "—";
            RotationText.Text = "—";
            WorldText.Text = "—";
        }
        else
        {
            MachinimaTransform transform = _state.Transform;
            PositionText.Text = string.Create(
                CultureInfo.InvariantCulture,
                $"{transform.X:F2}, {transform.Y:F2}, {transform.Z:F2}");
            RotationText.Text = string.Create(
                CultureInfo.InvariantCulture,
                $"{transform.Pitch:F1}°, {transform.Yaw:F1}°, {transform.Roll:F1}°");
            WorldText.Text = _state.WorldName;
        }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        ScriptingBridgeStatus bridge = _machinima.BridgeStatus;
        bool ready = bridge.IsRuntimeReady && !bridge.IsStale;
        bool active = _state?.IsEnabled == true;
        bool liveSelected = LiveNodeBox.SelectedItem is MachinimaNode;
        bool savedSelected =
            SavedLocationBox.SelectedItem is SavedMachinimaLocation;

        EnterButton.IsEnabled = !_busy && ready && !active;
        ExitButton.IsEnabled = !_busy && ready && active;
        RefreshButton.IsEnabled = !_busy && ready;
        SaveLocationButton.IsEnabled = !_busy && ready && active;
        // Camera teleport waits on verified Blam free-camera position writes.
        MoveCameraToLiveButton.IsEnabled = false;
        MoveSpartanToLiveButton.IsEnabled =
            !_busy && ready && active && liveSelected;
        MoveCameraToSavedButton.IsEnabled = false;
        MoveSpartanToSavedButton.IsEnabled =
            !_busy && ready && active && savedSelected;
        DeleteSavedButton.IsEnabled = !_busy && savedSelected;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }
}
