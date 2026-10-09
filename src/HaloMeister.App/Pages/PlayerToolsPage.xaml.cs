using System.Globalization;
using System.Text.RegularExpressions;
using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace HaloMeister.App.Pages;

public sealed partial class PlayerToolsPage : Page, IActivatablePage
{
    private static readonly Regex CoordinateSeparators = new(@"[,，;；\s]+", RegexOptions.Compiled);
    private static readonly SolidColorBrush ReadyBrush = new(Color.FromArgb(255, 108, 203, 95));
    private static readonly SolidColorBrush NotReadyBrush = new(Color.FromArgb(255, 252, 225, 0));
    private readonly PlayerToolsService _tools = new();
    private readonly PlayerLocationStore _locations = new();
    private readonly PlayerCameraService _camera = PlayerCameraService.Current;
    private readonly LiveSkullsService _skulls = new();
    private readonly SuperPunchService _superPunch = SuperPunchService.Current;
    private readonly WeaponActionTimingService _actionTiming =
        WeaponActionTimingService.Current;
    private PlayerCoordinates? _savedPosition;
    private bool _busy;
    private bool _updatingActionTimingToggle;
    private IReadOnlyList<UnitCameraPreset> _cameraPresets = [];
    private int _playerTagIndex = -1;

    public PlayerToolsPage()
    {
        InitializeComponent();
        ReloadSavedLocations();
        SyncWeaponInterruptionToggle();
        UpdateBridgeStatus();
        UpdateButtons();
    }

    public void OnActivated()
    {
        SyncWeaponInterruptionToggle();
        UpdateBridgeStatus();
        UpdateButtons();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            PlayerCoordinates position = await _tools.ReadPositionAsync();
            ShowCoordinates(position);
            SummaryText.Text = L.Format("player_tools.current_position", Format(position));
            ShowStatus(L.Get("player_tools.read_position"), InfoBarSeverity.Success);
        });
    }

    private async void OnSavePosition(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            _savedPosition = await _tools.ReadPositionAsync();
            ShowCoordinates(_savedPosition);
            SavedPositionText.Text = L.Format("player_tools.saved_return_position", Format(_savedPosition));
            ShowStatus(L.Get("player_tools.saved_session_position"), InfoBarSeverity.Success);
        });
    }

    private async void OnTeleport(object sender, RoutedEventArgs e)
    {
        PlayerCoordinates? destination = TryReadCoordinates();
        if (destination is null) return;
        await RunBusy(async () =>
        {
            await _tools.TeleportAsync(destination);
            SummaryText.Text = L.Format("player_tools.teleport_confirmed", Format(destination));
            ShowStatus(L.Get("player_tools.simulation_confirmed_teleport"), InfoBarSeverity.Success);
        });
    }

    private async void OnSaveLocation(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            PlayerCoordinates position = await _tools.ReadPositionAsync();
            SavedPlayerLocation saved = _locations.Save(LocationNameBox.Text, position);
            LocationNameBox.Text = string.Empty;
            ShowCoordinates(position);
            ReloadSavedLocations(saved.Id);
            ShowStatus(L.Format("player_tools.saved_at", saved.Name, Format(position)), InfoBarSeverity.Success);
        });
    }

    private async void OnTeleportSavedLocation(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SavedPlayerLocation location })
            await TeleportToSavedLocationAsync(location);
    }

    private async void OnSavedLocationDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Ignore double-clicks that land on the row's own buttons.
        if (e.OriginalSource is DependencyObject source && IsInsideButton(source))
            return;
        if (e.OriginalSource is FrameworkElement { DataContext: SavedPlayerLocation location })
            await TeleportToSavedLocationAsync(location);
    }

    private static bool IsInsideButton(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is Button) return true;
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private async Task TeleportToSavedLocationAsync(SavedPlayerLocation location)
    {
        if (_busy) return;
        if (!IsBridgeReady())
        {
            ShowStatus(_tools.BridgeStatus.Summary, InfoBarSeverity.Warning);
            return;
        }
        await RunBusy(async () =>
        {
            await _tools.TeleportAsync(location.Position);
            ShowCoordinates(location.Position);
            SummaryText.Text = L.Format("player_tools.teleport_saved_detail", location.Name, Format(location.Position));
            ShowStatus(L.Format("player_tools.teleport_saved", location.Name), InfoBarSeverity.Success);
        });
    }

    /// <summary>Deleting asks for an inline confirmation so a misclick cannot lose a location.</summary>
    private void OnDeleteSavedLocation(object sender, RoutedEventArgs e)
    {
        if (_busy ||
            sender is not FrameworkElement { DataContext: SavedPlayerLocation location } anchor)
        {
            return;
        }

        var confirm = new Button
        {
            Content = L.Get("player_tools.delete"),
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 12,
                MinWidth = 220,
                Children =
                {
                    new TextBlock
                    {
                        Text = L.Format("player_tools.confirm_delete", location.Name),
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 300,
                    },
                    confirm,
                },
            },
        };
        confirm.Click += (_, _) =>
        {
            flyout.Hide();
            try
            {
                _locations.Delete(location.Id);
                ReloadSavedLocations();
                ShowStatus(L.Format("player_tools.deleted_location", location.Name), InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                ShowStatus(UserFacingErrors.Format(ex), InfoBarSeverity.Error);
            }
        };
        flyout.ShowAt(anchor);
    }

    private void OnLocationNameChanged(object sender, TextChangedEventArgs e) => UpdateButtons();

    private void OnLocationNameKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        if (SaveLocationButton.IsEnabled)
            OnSaveLocation(sender, e);
    }

    private void OnBoxGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
            box.SelectAll();
    }

    private void OnCoordinateKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        if (TeleportButton.IsEnabled)
            OnTeleport(sender, e);
    }

    /// <summary>
    /// Pasting "x, y, z" (the format shown elsewhere on this page) into any
    /// coordinate box fills all three; anything else pastes as plain text.
    /// </summary>
    private async void OnCoordinatePaste(object sender, TextControlPasteEventArgs e)
    {
        if (sender is not TextBox box) return;
        DataPackageView content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text)) return;

        e.Handled = true;
        string text;
        try
        {
            text = await content.GetTextAsync();
        }
        catch
        {
            return;
        }

        string[] parts = CoordinateSeparators.Split(text.Trim());
        if (parts.Length == 3 &&
            TryParseCoordinate(parts[0], out float x) &&
            TryParseCoordinate(parts[1], out float y) &&
            TryParseCoordinate(parts[2], out float z))
        {
            ShowCoordinates(new PlayerCoordinates(x, y, z));
            return;
        }

        box.SelectedText = text.Trim();
        box.SelectionStart += box.SelectionLength;
        box.SelectionLength = 0;
    }

    private void OnCopyCoordinates(object sender, RoutedEventArgs e)
    {
        if (!TryParseCoordinate(XBox.Text, out float x) ||
            !TryParseCoordinate(YBox.Text, out float y) ||
            !TryParseCoordinate(ZBox.Text, out float z))
        {
            ShowStatus(L.Get("player_tools.enter_finite_xyz"), InfoBarSeverity.Error);
            return;
        }

        var package = new DataPackage();
        package.SetText(Format(new PlayerCoordinates(x, y, z)));
        Clipboard.SetContent(package);
        ShowStatus(L.Get("player_tools.coordinates_copied"), InfoBarSeverity.Success);
    }

    private async void OnReturn(object sender, RoutedEventArgs e)
    {
        if (_savedPosition is not { } destination) return;
        await RunBusy(async () =>
        {
            await _tools.TeleportAsync(destination);
            ShowCoordinates(destination);
            SummaryText.Text = L.Format("player_tools.returned_to", Format(destination));
            ShowStatus(L.Get("player_tools.returned_position"), InfoBarSeverity.Success);
        });
    }

    private async void OnEnableNoClip(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            await _tools.SetNoClipAsync(true);
            SummaryText.Text = L.Get("player_tools.no_clip_on_summary");
            ShowStatus(L.Get("player_tools.no_clip_enabled"), InfoBarSeverity.Warning);
        });
    }

    private async void OnDisableNoClip(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            await _tools.SetNoClipAsync(false);
            SummaryText.Text = L.Get("player_tools.normal_physics_restored");
            ShowStatus(L.Get("player_tools.no_clip_off"), InfoBarSeverity.Success);
        });
    }

    private async void OnSuppressPlayerInput(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            await _tools.SetInputSuppressedAsync(true);
            SummaryText.Text = L.Get("player_tools.player_input_suppressed_summary");
            ShowStatus(L.Get("player_tools.player_input_suppressed"), InfoBarSeverity.Warning);
        });
    }

    private async void OnRestorePlayerInput(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            await _tools.SetInputSuppressedAsync(false);
            SummaryText.Text = L.Get("player_tools.normal_input_restored");
            ShowStatus(L.Get("player_tools.player_input_restored"), InfoBarSeverity.Success);
        });
    }

    private async void OnRefreshCameras(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            int? selectedIndex =
                (CameraPresetBox.SelectedItem as UnitCameraPreset)?.UnitTag.Index;
            _playerTagIndex = await _tools.ReadActivePlayerTagIndexAsync();
            PlayerCameraSession session = await Task.Run(
                () => _camera.Load(_playerTagIndex));
            _cameraPresets = session.Presets;
            CameraPresetBox.ItemsSource = _cameraPresets;
            CameraPresetBox.SelectedItem = _cameraPresets.FirstOrDefault(
                preset => preset.UnitTag.Index == selectedIndex);
            if (CameraPresetBox.SelectedItem is null && _cameraPresets.Count > 0)
                CameraPresetBox.SelectedItem = _cameraPresets.FirstOrDefault(
                    preset => preset.Category == "Vehicle") ?? _cameraPresets[0];
            ShowCameraValues(session.CustomCamera);

            SummaryText.Text = L.Format(
                "player_tools.found_camera_presets",
                _cameraPresets.Count,
                session.PlayerUnit.LeafName);
            ShowStatus(L.Get("player_tools.refresh_cameras_status"), InfoBarSeverity.Success);
        });
    }

    private async void OnApplyCameraPreset(object sender, RoutedEventArgs e)
    {
        if (CameraPresetBox.SelectedItem is not UnitCameraPreset preset ||
            _playerTagIndex < 0)
        {
            ShowStatus(L.Get("player_tools.refresh_cameras_first"), InfoBarSeverity.Error);
            return;
        }
        await RunBusy(async () =>
        {
            PlayerCameraPatchResult result = await ApplyCameraAndReloadAsync(
                () => _camera.ApplyPreset(_playerTagIndex, preset));
            SummaryText.Text = result.Description;
            ShowStatus(L.Format("player_tools.authored_camera_active", preset.Name), InfoBarSeverity.Warning);
        });
    }

    private async void OnApplyCustomCamera(object sender, RoutedEventArgs e)
    {
        if (_playerTagIndex < 0)
        {
            ShowStatus(L.Get("player_tools.refresh_unit_cameras"), InfoBarSeverity.Error);
            return;
        }
        if (!TryParseCoordinate(CameraXBox.Text, out float x) ||
            !TryParseCoordinate(CameraYBox.Text, out float y) ||
            !TryParseCoordinate(CameraZBox.Text, out float z) ||
            !TryParseCoordinate(CameraFovBox.Text, out float fov) ||
            fov is < 30f or > 150f)
        {
            ShowStatus(L.Get("player_tools.enter_finite_camera_values"), InfoBarSeverity.Error);
            return;
        }
        await RunBusy(async () =>
        {
            PlayerCameraPatchResult result = await ApplyCameraAndReloadAsync(
                () => _camera.ApplyCustom(_playerTagIndex, x, y, z, fov));
            SummaryText.Text = result.Description;
            ShowStatus(L.Get("player_tools.custom_camera_active"), InfoBarSeverity.Warning);
        });
    }

    private async void OnRestoreCamera(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            bool thirdPersonEnabled = await IsThirdPersonEnabledAsync();
            if (thirdPersonEnabled)
                await _skulls.SetAsync("skull_third_person", false);

            int restored;
            try
            {
                restored = await Task.Run(_camera.Restore);
            }
            finally
            {
                if (thirdPersonEnabled)
                    await _skulls.SetAsync("skull_third_person", true);
            }
            SummaryText.Text = restored == 0
                ? L.Get("player_tools.no_camera_restore_needed")
                : L.Format("player_tools.restored_camera_values", restored);
            ShowStatus(L.Get("player_tools.authored_camera_restored"), InfoBarSeverity.Success);
        });
    }

    private async Task<PlayerCameraPatchResult> ApplyCameraAndReloadAsync(
        Func<PlayerCameraPatchResult> apply)
    {
        bool thirdPersonEnabled = await IsThirdPersonEnabledAsync();
        if (thirdPersonEnabled)
            await _skulls.SetAsync("skull_third_person", false);

        try
        {
            PlayerCameraPatchResult result = await Task.Run(apply);
            await _skulls.SetAsync("skull_third_person", true);
            return result;
        }
        catch
        {
            try
            {
                await Task.Run(_camera.Restore);
            }
            catch
            {
                // Preserve the original exception from the failed apply/reload.
            }

            try
            {
                await _skulls.SetAsync(
                    "skull_third_person",
                    thirdPersonEnabled);
            }
            catch
            {
                // Preserve the original exception from the failed apply/reload.
            }
            throw;
        }
    }

    private async Task<bool> IsThirdPersonEnabledAsync()
    {
        IReadOnlyList<LiveSkullItem> skulls = await _skulls.ReadAsync();
        return skulls.Single(item =>
            item.Name.Equals(
                "skull_third_person",
                StringComparison.Ordinal)).IsEnabled;
    }

    private void OnCameraPresetSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        CameraPresetDetailText.Text =
            (CameraPresetBox.SelectedItem as UnitCameraPreset)?.Detail ??
            L.Get("player_tools.no_camera_preset_selected");
        UpdateButtons();
    }

    private void ShowCameraValues(CustomPlayerCamera camera)
    {
        CameraXBox.Text = camera.X.ToString("R", CultureInfo.InvariantCulture);
        CameraYBox.Text = camera.Y.ToString("R", CultureInfo.InvariantCulture);
        CameraZBox.Text = camera.Z.ToString("R", CultureInfo.InvariantCulture);
        CameraFovBox.Text =
            camera.FieldOfViewDegrees.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private async void OnEnableSuperPunch(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            float strength = SelectedSuperPunchStrength();
            SuperPunchResult result = await Task.Run(() => _superPunch.Enable(strength));
            SummaryText.Text = L.Format(
                "player_tools.super_punch_active",
                result.Multiplier,
                result.EffectCount);
            ShowStatus(
                L.Format("player_tools.super_punch_enabled", result.Multiplier),
                InfoBarSeverity.Warning);
        });
    }

    private async void OnRestoreSuperPunch(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            int restored = await Task.Run(_superPunch.Restore);
            SummaryText.Text = restored == 0
                ? L.Get("player_tools.super_punch_no_restore")
                : L.Format("player_tools.restored_melee_effects", restored);
            ShowStatus(L.Get("player_tools.normal_punch_restored"), InfoBarSeverity.Success);
        });
    }

    private async void OnWeaponInterruptionToggled(object sender, RoutedEventArgs e)
    {
        if (_updatingActionTimingToggle ||
            _busy ||
            sender is not ToggleSwitch toggle)
        {
            return;
        }

        bool requested = toggle.IsOn;
        _busy = true;
        BusyRing.IsActive = true;
        UpdateButtons();
        try
        {
            if (requested)
            {
                WeaponActionTimingResult result =
                    await Task.Run(_actionTiming.Enable);
                SummaryText.Text = L.Format(
                    "player_tools.moved_interruption_markers",
                    result.EventCount,
                    result.GraphCount);
                ShowStatus(
                    L.Get("player_tools.immediate_interruption_active"),
                    InfoBarSeverity.Warning);
            }
            else
            {
                int restored = await Task.Run(_actionTiming.Restore);
                SummaryText.Text = L.Format(
                    "player_tools.restored_interruption_markers",
                    restored);
                ShowStatus(
                    L.Get("player_tools.authored_timing_restored"),
                    InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            _updatingActionTimingToggle = true;
            toggle.IsOn = !requested;
            _updatingActionTimingToggle = false;
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

    private void SyncWeaponInterruptionToggle()
    {
        _updatingActionTimingToggle = true;
        ImmediateWeaponInterruptionToggle.IsOn = _actionTiming.IsActive;
        _updatingActionTimingToggle = false;
    }

    private float SelectedSuperPunchStrength()
    {
        if (SuperPunchStrengthBox.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string tag ||
            !float.TryParse(
                tag,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float strength))
        {
            return 50f;
        }
        return strength;
    }

    private PlayerCoordinates? TryReadCoordinates()
    {
        if (!TryParseCoordinate(XBox.Text, out float x) ||
            !TryParseCoordinate(YBox.Text, out float y) ||
            !TryParseCoordinate(ZBox.Text, out float z))
        {
            ShowStatus(L.Get("player_tools.enter_finite_xyz"), InfoBarSeverity.Error);
            return null;
        }
        return new PlayerCoordinates(x, y, z);
    }

    private static bool TryParseCoordinate(string text, out float value) =>
        float.TryParse(
            text,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value) &&
        float.IsFinite(value);

    private void ShowCoordinates(PlayerCoordinates position)
    {
        XBox.Text = position.X.ToString("R", CultureInfo.InvariantCulture);
        YBox.Text = position.Y.ToString("R", CultureInfo.InvariantCulture);
        ZBox.Text = position.Z.ToString("R", CultureInfo.InvariantCulture);
    }

    private async Task RunBusy(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        BusyRing.IsActive = true;
        UpdateButtons();
        try
        {
            await action();
        }
        catch (Exception ex)
        {
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

    private void UpdateBridgeStatus()
    {
        ScriptingBridgeStatus status = _tools.BridgeStatus;
        BridgeStatusText.Text = status.Summary;
        BridgeDot.Fill = status.IsRuntimeReady && !status.IsStale ? ReadyBrush : NotReadyBrush;
    }

    private bool IsBridgeReady()
    {
        ScriptingBridgeStatus status = _tools.BridgeStatus;
        return status.IsRuntimeReady && !status.IsStale;
    }

    private void ReloadSavedLocations(Guid? selectedId = null)
    {
        IReadOnlyList<SavedPlayerLocation> locations = _locations.Load();
        SavedLocationList.ItemsSource = locations;
        bool any = locations.Count > 0;
        SavedLocationList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        SavedLocationsEmptyPanel.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        if (locations.FirstOrDefault(location => location.Id == selectedId) is { } selected)
            SavedLocationList.ScrollIntoView(selected);
    }

    private void UpdateButtons()
    {
        ScriptingBridgeStatus status = _tools.BridgeStatus;
        bool ready = !_busy && status.IsRuntimeReady && !status.IsStale;
        RefreshButton.IsEnabled = ready;
        EnableNoClipButton.IsEnabled = ready;
        DisableNoClipButton.IsEnabled = ready;
        SuppressPlayerInputButton.IsEnabled = ready;
        RestorePlayerInputButton.IsEnabled = ready;
        bool cameraConnected = !_busy && _camera.IsConnected;
        bool cameraReady = ready && cameraConnected;
        RefreshCamerasButton.IsEnabled = cameraReady;
        CameraPresetBox.IsEnabled = !_busy && _cameraPresets.Count > 0;
        ApplyCameraPresetButton.IsEnabled =
            cameraReady && CameraPresetBox.SelectedItem is UnitCameraPreset;
        CameraXBox.IsEnabled = cameraConnected;
        CameraYBox.IsEnabled = cameraConnected;
        CameraZBox.IsEnabled = cameraConnected;
        CameraFovBox.IsEnabled = cameraConnected;
        ApplyCustomCameraButton.IsEnabled =
            cameraReady && _playerTagIndex >= 0;
        RestoreCameraButton.IsEnabled = cameraConnected && _camera.IsActive;
        SuperPunchStrengthBox.IsEnabled = !_busy;
        EnableSuperPunchButton.IsEnabled = !_busy;
        RestoreSuperPunchButton.IsEnabled = !_busy && _superPunch.IsActive;
        ImmediateWeaponInterruptionToggle.IsEnabled = !_busy;
        TeleportButton.IsEnabled = ready;
        SavePositionButton.IsEnabled = ready;
        ReturnButton.IsEnabled = ready && _savedPosition is not null;
        LocationNameBox.IsEnabled = !_busy;
        SaveLocationButton.IsEnabled = ready && !string.IsNullOrWhiteSpace(LocationNameBox.Text);
        SavedLocationList.IsEnabled = !_busy;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
        => MainWindow.Instance?.Report(message, severity);

    private static string Format(PlayerCoordinates position) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{position.X:0.###}, {position.Y:0.###}, {position.Z:0.###}");
}
