using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class ScenarioPalettePage : Page, IActivatablePage
{
    private readonly RuntimeTagMemoryService _memory = RuntimeTagMemoryService.Current;
    private readonly ScenarioPaletteService _palettes = new(RuntimeTagMemoryService.Current);
    private IReadOnlyList<ScenarioPaletteEntry> _scenery = [];
    private IReadOnlyList<ScenarioPaletteEntry> _machines = [];
    private ScenarioPlayerPose? _playerPose;
    private bool _scanned;
    private int _busy;
    private int _scanVersion;

    public ScenarioPalettePage()
    {
        InitializeComponent();
        LevelText.Text = L.Get("scenario_palette.disconnected");
        PoseText.Text = L.Get("scenario_palette.pose_unavailable");
        PlaceXBox.Value = double.NaN;
        PlaceYBox.Value = double.NaN;
        PlaceZBox.Value = double.NaN;
        PlaceYawBox.Value = double.NaN;
        PlacePitchBox.Value = double.NaN;
    }

    public void OnActivated() => _ = ScanAsync();

    private async void OnRefresh(object sender, RoutedEventArgs e) => await ScanAsync();

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async void OnPlace(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ScenarioPaletteEntry entry } || !TryEnter())
            return;

        try
        {
            ScenarioPlacementResult placement = await _palettes.PlaceAsync(entry);
            ApplyFilter();
            if (placement.Execution.Outcome != ScriptOutcome.Confirmed)
            {
                ShowStatus(placement.Execution.Message, InfoBarSeverity.Warning);
                return;
            }

            if (placement.Placed is null)
            {
                ShowStatus(L.Get("scenario_palette.error_no_datum"), InfoBarSeverity.Warning);
                return;
            }

            ShowStatus(
                L.Format("scenario_palette.placed", entry.Title),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            Exit();
        }
    }

    private async void OnPlaceAt(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ScenarioPaletteEntry entry } || !TryEnter())
            return;

        try
        {
            if (!TryReadPlacement(out ScenarioPlayerPose pose, out string? error))
            {
                ShowStatus(error ?? L.Get("scenario_palette.error_pose"), InfoBarSeverity.Warning);
                return;
            }

            ScenarioPlacementResult placement = await _palettes.PlaceAtAsync(entry, pose);
            ApplyFilter();
            if (placement.Execution.Outcome != ScriptOutcome.Confirmed)
            {
                ShowStatus(placement.Execution.Message, InfoBarSeverity.Warning);
                return;
            }

            if (placement.Placed is null)
            {
                ShowStatus(L.Get("scenario_palette.error_no_datum"), InfoBarSeverity.Warning);
                return;
            }

            ShowStatus(
                L.Format("scenario_palette.placed_at", entry.Title),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            Exit();
        }
    }

    private async void OnRefreshPose(object sender, RoutedEventArgs e)
    {
        if (!TryEnter())
            return;

        try
        {
            await RefreshPoseAsync();
        }
        finally
        {
            Exit();
        }
    }

    private void OnUsePose(object sender, RoutedEventArgs e)
    {
        if (_playerPose is not ScenarioPlayerPose pose)
        {
            ShowStatus(L.Get("scenario_palette.pose_unavailable"), InfoBarSeverity.Warning);
            return;
        }

        PlaceXBox.Value = pose.X;
        PlaceYBox.Value = pose.Y;
        PlaceZBox.Value = pose.Z;
        if (pose.HasView)
        {
            PlaceYawBox.Value = pose.YawDegrees!.Value;
            PlacePitchBox.Value = pose.PitchDegrees!.Value;
            return;
        }

        ShowStatus(L.Get("scenario_palette.pose_no_view"), InfoBarSeverity.Warning);
    }

    private async void OnDestroy(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PlacedScenarioObject placed } || !TryEnter())
            return;

        try
        {
            await _palettes.DestroyAsync(placed);
            ApplyFilter();
            ShowStatus(
                L.Format("scenario_palette.destroyed", placed.Title),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ApplyFilter();
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            Exit();
        }
    }

    private async void OnDestroyAll(object sender, RoutedEventArgs e)
    {
        if (_palettes.Placed.Count == 0 || !TryEnter())
            return;

        try
        {
            int destroyed = await _palettes.DestroyAllAsync();
            ApplyFilter();
            ShowStatus(
                L.Format("scenario_palette.destroyed_all", destroyed),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ApplyFilter();
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            Exit();
        }
    }

    private async Task ScanAsync()
    {
        if (!TryEnter())
            return;

        int version = ++_scanVersion;
        LevelText.Text = L.Get("scenario_palette.scanning");
        try
        {
            if (!_memory.IsConnected)
            {
                _scanned = false;
                _scenery = [];
                _machines = [];
                _palettes.NoteScenario(null);
                ApplyFilter();
                _playerPose = null;
                PoseText.Text = L.Get("scenario_palette.disconnected");
                LevelText.Text = L.Get("scenario_palette.disconnected");
                ShowStatus(L.Get("scenario_palette.disconnected"), InfoBarSeverity.Informational);
                return;
            }

            ScenarioPaletteSession session = await Task.Run(_palettes.Scan);
            if (version != _scanVersion)
                return;

            _scanned = true;
            _scenery = session.Scenery;
            _machines = session.Machines;
            _palettes.NoteScenario(session.ScenarioName);
            ApplyFilter();
            LevelText.Text = L.Format("scenario_palette.level", session.ScenarioName);
            StatusBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            if (version != _scanVersion)
                return;

            _scanned = false;
            _scenery = [];
            _machines = [];
            ApplyFilter();
            LevelText.Text = "";
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (version == _scanVersion)
            {
                if (_memory.IsConnected)
                    await RefreshPoseAsync();
                Exit();
            }
        }
    }

    private async Task RefreshPoseAsync()
    {
        try
        {
            ScenarioPlayerPose pose = await _palettes.ReadPlayerPoseAsync();
            _playerPose = pose;
            PoseText.Text = pose.HasView
                ? L.Format(
                    "scenario_palette.pose",
                    pose.X,
                    pose.Y,
                    pose.Z,
                    pose.YawDegrees!.Value,
                    pose.PitchDegrees!.Value)
                : L.Format(
                    "scenario_palette.pose_position",
                    pose.X,
                    pose.Y,
                    pose.Z);
        }
        catch (Exception ex)
        {
            _playerPose = null;
            PoseText.Text = ex.Message;
        }
    }

    private bool TryReadPlacement(out ScenarioPlayerPose pose, out string? error)
    {
        pose = default;
        error = null;
        if (!TryBox(PlaceXBox, out float x) ||
            !TryBox(PlaceYBox, out float y) ||
            !TryBox(PlaceZBox, out float z) ||
            !TryBox(PlaceYawBox, out float yaw) ||
            !TryBox(PlacePitchBox, out float pitch))
        {
            error = L.Get("scenario_palette.error_pose");
            return false;
        }

        if (pitch is < -90f or > 90f)
        {
            error = L.Get("scenario_palette.error_pitch");
            return false;
        }

        if (Math.Abs(x) > 100_000f || Math.Abs(y) > 100_000f || Math.Abs(z) > 100_000f)
        {
            error = L.Get("scenario_palette.error_coordinates");
            return false;
        }

        pose = new ScenarioPlayerPose(x, y, z, yaw, pitch);
        return true;
    }

    private static bool TryBox(NumberBox box, out float value)
    {
        value = (float)box.Value;
        return !double.IsNaN(box.Value) && float.IsFinite(value);
    }

    private void ApplyFilter()
    {
        string query = SearchBox.Text.Trim();
        ScenarioPaletteEntry[] scenery = Filter(_scenery, query);
        ScenarioPaletteEntry[] machines = Filter(_machines, query);
        PlacedScenarioObject[] placed = FilterPlaced(_palettes.Placed, query);
        SceneryList.ItemsSource = scenery;
        MachineList.ItemsSource = machines;
        PlacedList.ItemsSource = placed;
        SceneryEmpty.Visibility = _scanned && scenery.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        MachineEmpty.Visibility = _scanned && machines.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        PlacedEmpty.Visibility = _palettes.Placed.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        PlacedTab.Header = _palettes.Placed.Count == 0
            ? L.Get("scenario_palette.tab_placed")
            : L.Format("scenario_palette.tab_placed_count", _palettes.Placed.Count);
        CountText.Text = L.Format(
            "scenario_palette.shown",
            scenery.Length,
            machines.Length,
            placed.Length);
        SyncPlacedActions();
    }

    private static PlacedScenarioObject[] FilterPlaced(
        IReadOnlyList<PlacedScenarioObject> entries,
        string query) =>
        entries
            .Where(entry =>
                query.Length == 0 ||
                entry.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static ScenarioPaletteEntry[] Filter(
        IReadOnlyList<ScenarioPaletteEntry> entries,
        string query) =>
        entries
            .Where(entry =>
                query.Length == 0 ||
                entry.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private bool TryEnter()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return false;
        UpdateChrome();
        return true;
    }

    private void Exit()
    {
        Interlocked.Exchange(ref _busy, 0);
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        bool busy = Volatile.Read(ref _busy) == 1;
        BusyRing.IsActive = busy;
        RefreshButton.IsEnabled = !busy;
        SearchBox.IsEnabled = !busy;
        SceneryList.IsEnabled = !busy;
        MachineList.IsEnabled = !busy;
        PlacedList.IsEnabled = !busy;
        RefreshPoseButton.IsEnabled = !busy;
        UsePoseButton.IsEnabled = !busy && _playerPose is not null;
        PlaceXBox.IsEnabled = !busy;
        PlaceYBox.IsEnabled = !busy;
        PlaceZBox.IsEnabled = !busy;
        PlaceYawBox.IsEnabled = !busy;
        PlacePitchBox.IsEnabled = !busy;
        SyncPlacedActions();
    }

    private void SyncPlacedActions()
    {
        DestroyAllButton.IsEnabled =
            Volatile.Read(ref _busy) == 0 && _palettes.Placed.Count > 0;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }
}
