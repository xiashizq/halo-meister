using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class BoundaryVolumesPage : Page
{
    private readonly RuntimeBoundaryService _boundaries = new();
    private readonly SoftCeilingService _softCeilings = new();
    private RuntimeBoundaryState? _state;
    private bool? _physicalWallsDisabled;
    private bool _busy;

    public BoundaryVolumesPage()
    {
        InitializeComponent();
        UpdatePhysicalStatus();
        UpdateButtons();
    }

    private async void OnScan(object sender, RoutedEventArgs e)
        => await RunBoundaryBusy(
            _boundaries.ReadAsync,
            L.Get("boundary_volumes.read_runtime_state_success"));

    private async void OnRefresh(object sender, RoutedEventArgs e)
        => await RunBoundaryBusy(
            _boundaries.ReadAsync,
            L.Get("boundary_volumes.refreshed_runtime_state"));

    private async void OnDisable(object sender, RoutedEventArgs e)
        => await RunBoundaryBusy(
            _boundaries.DisableAsync,
            L.Get("boundary_volumes.disabled_all_triggers"));

    private async void OnRestore(object sender, RoutedEventArgs e)
        => await RunBoundaryBusy(
            _boundaries.RestoreAsync,
            L.Get("boundary_volumes.restored_bitset"));

    private async void OnRefreshPhysical(object sender, RoutedEventArgs e)
        => await RunPhysicalBusy(
            _softCeilings.ReadDisabledAsync,
            L.Get("boundary_volumes.read_physical_override"));

    private async void OnDisablePhysical(object sender, RoutedEventArgs e)
        => await RunPhysicalBusy(
            cancellationToken => _softCeilings.SetDisabledAsync(
                true, cancellationToken),
            L.Get("boundary_volumes.disabled_physical_walls_success"));

    private async void OnEnablePhysical(object sender, RoutedEventArgs e)
        => await RunPhysicalBusy(
            cancellationToken => _softCeilings.SetDisabledAsync(
                false, cancellationToken),
            L.Get("boundary_volumes.restored_physical_walls_success"));

    private async Task RunBoundaryBusy(
        Func<CancellationToken, Task<RuntimeBoundaryState>> action,
        string successMessage)
    {
        if (_busy) return;
        _busy = true;
        BusyRing.IsActive = true;
        UpdateButtons();
        try
        {
            RuntimeBoundaryState state =
                await action(CancellationToken.None);
            ShowState(state);
            ShowStatus(successMessage, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            UpdateButtons();
        }
    }

    private async Task RunPhysicalBusy(
        Func<CancellationToken, Task<bool>> action,
        string successMessage)
    {
        if (_busy) return;
        _busy = true;
        BusyRing.IsActive = true;
        UpdateButtons();
        try
        {
            _physicalWallsDisabled = await action(CancellationToken.None);
            UpdatePhysicalStatus();
            ShowStatus(successMessage, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            UpdatePhysicalStatus();
            UpdateButtons();
        }
    }

    private void ShowState(RuntimeBoundaryState state)
    {
        _state = state;
        ScenarioList.ItemsSource = new[] { state };
        SummaryText.Text = state.IsDisabled
            ? L.Get("boundary_volumes.all_triggers_disabled")
            : L.Format(
                "boundary_volumes.active_triggers_summary",
                state.ActiveCount.ToString("N0"),
                state.TotalCount.ToString("N0"));
        DetailText.Text =
            L.Format(
                "boundary_volumes.detail_summary",
                state.DisabledCount.ToString("N0"),
                state.ActiveCount.ToString("N0")) +
            (state.CanRestore ? L.Get("boundary_volumes.snapshot_available") : "");
    }

    private void UpdateButtons()
    {
        ScriptingBridgeStatus bridge = _boundaries.BridgeStatus;
        bool bridgeReady =
            !_busy && bridge.IsRuntimeReady && !bridge.IsStale;
        bool scanned = _state is not null;
        ScanButton.IsEnabled = bridgeReady;
        RefreshButton.IsEnabled = bridgeReady && scanned;
        DisableButton.IsEnabled =
            bridgeReady && scanned && !_state!.IsDisabled;
        RestoreButton.IsEnabled =
            bridgeReady && _state?.CanRestore == true;
        RefreshPhysicalButton.IsEnabled = bridgeReady;
        DisablePhysicalButton.IsEnabled =
            bridgeReady && _physicalWallsDisabled != true;
        EnablePhysicalButton.IsEnabled =
            bridgeReady && _physicalWallsDisabled == true;
    }

    private void UpdatePhysicalStatus()
    {
        ScriptingBridgeStatus bridge = _softCeilings.BridgeStatus;
        PhysicalStatusText.Text = _physicalWallsDisabled switch
        {
            true => L.Format("boundary_volumes.physical_disabled_session", bridge.Summary),
            false => L.Format("boundary_volumes.physical_enabled_authored", bridge.Summary),
            null => bridge.Summary,
        };
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }
}
