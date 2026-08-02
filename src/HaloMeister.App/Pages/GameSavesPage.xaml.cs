using System.Diagnostics;
using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using HaloMeister.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace HaloMeister.App.Pages;

public sealed partial class GameSavesPage : Page
{
    private readonly SteamGameSaveStore _steamStore = new();
    private readonly WgsGameSaveStore _storeStore = new();
    private IGameSaveStore _store;
    private IReadOnlyList<WgsSaveSlot> _slots = [];
    private IReadOnlyList<WgsBackupEntry> _backups = [];
    private bool _suppressPlatformChange;

    public GameSavesPage()
    {
        _store = _steamStore;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private nint Hwnd => MainWindow.Instance is { } window
        ? WinRT.Interop.WindowNative.GetWindowHandle(window)
        : 0;

    private WgsSaveSlot? Selected => SlotsList.SelectedItem as WgsSaveSlot;
    private WgsBackupEntry? SelectedBackup => BackupBox.SelectedItem as WgsBackupEntry;
    private bool IsSteamPlatform => _store.PlatformId == SteamGameSaveStore.Platform;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        SelectInitialPlatform();
        ApplyPlatformChrome();
        Refresh();
    }

    private void SelectInitialPlatform()
    {
        bool preferStore =
            GamePlatformPreference.Current.Platform == GamePlatformKind.MicrosoftStore;

        _suppressPlatformChange = true;
        try
        {
            SteamPlatformItem.IsSelected = !preferStore;
            StorePlatformItem.IsSelected = preferStore;
            _store = preferStore ? _storeStore : _steamStore;
        }
        finally
        {
            _suppressPlatformChange = false;
        }
    }

    private void OnPlatformChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_suppressPlatformChange) return;
        if (sender.SelectedItem?.Tag is not string platform) return;

        _store = platform == WgsGameSaveStore.Platform ? _storeStore : _steamStore;
        GamePlatformPreference.Current.Platform = platform == WgsGameSaveStore.Platform
            ? GamePlatformKind.MicrosoftStore
            : GamePlatformKind.Steam;
        ApplyPlatformChrome();
        Refresh();
    }

    private void ApplyPlatformChrome()
    {
        RootPathText.Text = _store.LiveRoot;
        StorageSourceText.Text = IsSteamPlatform
            ? L.Get("game_saves.stored_by_steam_savegames")
            : L.Get("game_saves.stored_by_windows_gaming_services");
        OpenLiveFolderItem.Text = IsSteamPlatform
            ? L.Get("game_saves.open_live_steam_folder")
            : L.Get("game_saves.open_live_wgs_folder");
        ContainerLabelText.Text = IsSteamPlatform
            ? L.Get("game_saves.slot_file")
            : L.Get("game_saves.container");
    }

    private void Refresh(string? selectContainer = null, string? selectBackupId = null)
    {
        try
        {
            _slots = _store.Discover();
            SlotsList.ItemsSource = _slots;
            SlotCountText.Text = L.Format(
                _slots.Count == 1 ? "game_saves.slot_count_one" : "game_saves.slot_count_many",
                _slots.Count);
            SlotsList.SelectedItem = selectContainer is null
                ? _slots.FirstOrDefault()
                : _slots.FirstOrDefault(slot =>
                    slot.ContainerId.Equals(selectContainer, StringComparison.OrdinalIgnoreCase));

            _backups = _store.DiscoverBackups();
            BackupBox.ItemsSource = _backups;
            BackupLibraryCountText.Text = _backups.Count == 0
                ? L.Get("game_saves.no_backups_available")
                : L.Format(
                    _backups.Count == 1
                        ? "game_saves.recoverable_checkpoint_one"
                        : "game_saves.recoverable_checkpoint_many",
                    _backups.Count);
            BackupBox.SelectedItem = selectBackupId is null
                ? _backups.FirstOrDefault(BackupMatchesSelectedSlot)
                : _backups.FirstOrDefault(backup =>
                    backup.Id.Equals(selectBackupId, StringComparison.OrdinalIgnoreCase));

            string running = _store.IsGameRunning
                ? L.Get("game_saves.game_running_restore_disabled")
                : "";
            LaunchGameButton.IsEnabled = !_store.IsGameRunning;
            LaunchGameButtonText.Text = _store.IsGameRunning
                ? L.Get("game_saves.game_running")
                : L.Get("game_saves.launch_game");
            Report(
                L.Format(
                    "game_saves.found_containers_and_backups",
                    _slots.Count,
                    _backups.Count,
                    running),
                _store.IsGameRunning ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            UpdateSelection();
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
        => Refresh(Selected?.ContainerId, SelectedBackup?.Id);

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BackupBox.SelectedItem is null)
            BackupBox.SelectedItem = _backups.FirstOrDefault(BackupMatchesSelectedSlot);
        UpdateSelection();
    }

    private void OnBackupSelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateSelection();

    private bool BackupMatchesSelectedSlot(WgsBackupEntry backup)
    {
        WgsSaveSlot? slot = Selected;
        if (slot is null) return false;
        return backup.ContainerId is not null &&
               backup.ContainerId.Equals(slot.ContainerId, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateSelection()
    {
        WgsSaveSlot? slot = Selected;
        WgsBackupEntry? backup = SelectedBackup;
        bool selected = slot is not null;

        ExportButton.IsEnabled = selected;
        EmptySelectionText.Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
        DetailsGrid.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        RestoreButton.IsEnabled =
            slot?.Save.Kind == WgsGameSaveKind.Checkpoint &&
            backup?.Save.Kind == WgsGameSaveKind.Checkpoint &&
            !_store.IsGameRunning;
        RecoveryHintText.Text = _store.IsGameRunning
            ? L.Get("game_saves.close_game_before_restore")
            : IsSteamPlatform
                ? L.Get("game_saves.restoring_steam_creates_safety_snapshot")
                : L.Get("game_saves.restoring_first_creates_a_complete_safety_6c55da");

        BackupMetadataText.Text = backup is null
            ? L.Get("game_saves.create_a_backup_or_import_an_archive_to_p_ac8fa1")
            : $"{backup.Save.ScenarioDisplay} · {backup.Save.Difficulty ?? L.Get("game_saves.unknown_difficulty")} · " +
              $"{backup.CreatedLocal:yyyy-MM-dd HH:mm:ss}\n{backup.OriginLabel} · {backup.Reason}";

        if (slot is null) return;

        SelectedSaveNameText.Text = slot.DisplayName;
        SelectedSaveSummaryText.Text = $"{slot.Save.KindLabel} · {slot.UpdatedDisplay} · {slot.SizeDisplay}";
        ScenarioText.Text = slot.Save.ScenarioDisplay;
        DifficultyText.Text = slot.Save.Difficulty ?? L.Get("game_saves.not_detected");
        CheckpointText.Text = slot.Save.InternalCheckpoint ?? L.Get("game_saves.name_not_detected");
        SkullsText.Text = slot.Save.ActiveSkulls.Count == 0
            ? L.Get("game_saves.none_detected")
            : string.Join(", ", slot.Save.ActiveSkulls.Select(Catalog.Humanize));
        KindText.Text = $"{slot.Save.KindLabel} · {slot.Save.FormatDetail}";
        BuildText.Text = slot.Save.Build ?? L.Get("game_saves.not_detected");
        ContainerText.Text = IsSteamPlatform
            ? slot.ContainerId
            : L.Format(
                "game_saves.container_metadata_revision",
                slot.ContainerId,
                slot.MetadataRevision);
        UpdatedText.Text = slot.UpdatedDisplay;
        FileText.Text = slot.DataPath;
    }

    private void OnBackupAll(object sender, RoutedEventArgs e)
    {
        try
        {
            WgsBackupResult result = _store.BackupAll("manual");
            Refresh(Selected?.ContainerId);
            Report(
                L.Format("game_saves.backup_created", result.FileCount),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } slot) return;

        try
        {
            WgsBackupEntry exported = _store.ExportToLibrary(slot);
            Refresh(slot.ContainerId, exported.Id);
            Report(
                L.Format(
                    "game_saves.exported_to_library",
                    slot.DisplayName,
                    Path.GetFileName(exported.SourcePath)),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
            picker.FileTypeFilter.Add(".halo-wgs");
            picker.FileTypeFilter.Add(".zip");
            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null) return;

            WgsBackupEntry imported = _store.ImportToLibrary(file.Path);
            Refresh(Selected?.ContainerId, imported.Id);
            Report(
                L.Format("game_saves.imported_review_restore", Path.GetFileName(file.Path)),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnRestoreBackup(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } slot || SelectedBackup is not { } backup) return;

        try
        {
            if (_store.IsGameRunning)
                throw new InvalidOperationException(L.Get("game_saves.game_using_save_cache"));

            var confirmation = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = L.Get("game_saves.restore_backup_title"),
                Content = L.Format(
                    IsSteamPlatform
                        ? "game_saves.restore_backup_content_steam"
                        : "game_saves.restore_backup_content",
                    slot.DisplayName,
                    backup.DisplayName,
                    backup.Detail),
                PrimaryButtonText = L.Get("game_saves.back_up_and_restore"),
                CloseButtonText = L.Get("common.cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

            WgsReplaceResult result = _store.RestoreBackup(slot, backup);
            Refresh(result.UpdatedSlot.ContainerId);
            Report(
                L.Format(
                    IsSteamPlatform
                        ? "game_saves.restored_with_snapshot_steam"
                        : "game_saves.restored_with_snapshot",
                    backup.DisplayName,
                    result.BackupPath),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OnOpenLiveFolder(object sender, RoutedEventArgs e)
        => TryOpenFolder(_store.LiveRoot);

    private void OnOpenBackups(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_store.BackupRoot);
        TryOpenFolder(_store.BackupRoot);
    }

    private void TryOpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Report(L.Format("game_saves.live_folder_missing", path), InfoBarSeverity.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnLaunchGame(object sender, RoutedEventArgs e)
    {
        try
        {
            bool launched = await _store.LaunchGameAsync();
            Report(
                launched
                    ? L.Get(IsSteamPlatform
                        ? "game_saves.launch_requested_steam"
                        : "game_saves.launch_requested")
                    : L.Get("game_saves.launch_not_accepted"),
                launched ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void Report(string message, InfoBarSeverity severity)
    {
        PageStatus.Title = severity == InfoBarSeverity.Error
            ? L.Get("game_saves.operation_failed")
            : L.Get("game_saves.game_saves");
        PageStatus.Message = message;
        PageStatus.Severity = severity;
        PageStatus.IsOpen = true;
    }
}
