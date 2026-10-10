using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using HaloMeister.App.Localization;
using HaloMeister.App.Models;
using HaloMeister.App.Pages;
using HaloMeister.App.Services;
using HaloMeister.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace HaloMeister.App;

public sealed partial class MainWindow : Window
{
    private readonly AppState _state = AppState.Current;
    private readonly PlayFabProxyService _proxy = PlayFabProxyService.Current;
    private readonly RuntimeTagMemoryService _game = RuntimeTagMemoryService.Current;
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private readonly Ue4ssLoaderInstaller _loaderInstaller = new();
    private byte[]? _patchPayload;
    private bool _installingBridge;
    private bool _liveToolsGateOpen;
    private bool _suppressAutoLiveTools;
    private bool _windowClosed;
    private bool _liveToolsIsUpdate;
    private LiveToolsCardKind _liveToolsCardKind = LiveToolsCardKind.Hidden;
    private string? _liveToolsDetailKey;
    private string? _liveToolsDetailLiteral;
    private double? _liveToolsCardProgress;
    private Ue4ssDownloadProgress? _liveToolsDownload;
    private int _liveToolsDownloadGeneration;
    private bool _cloudBusy;
    private bool _awaitingAuthCapture;
    private bool _authSavedDuringCapture;
    private int _navigationGeneration;
    private readonly GameSessionWatcher _sessionWatcher;
    private readonly Dictionary<Type, Page> _pageCache = new();
    private Page? _activePage;
    private readonly DispatcherTimer _statusDismissTimer = new()
    {
        Interval = TimeSpan.FromSeconds(4),
    };
    private readonly DispatcherTimer _patchSerializeTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(300),
    };
    private readonly DispatcherTimer _liveToolsCardTimer = new()
    {
        Interval = TimeSpan.FromSeconds(5),
    };

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;
        SetWindowIcon();
        ApplyBuildPolicy();
        // File-based tools must know the installation while the game is closed.
        _ = GameInstallationService.Current.BinaryDirectory;

        _state.DirtyChanged += UpdateChrome;
        _state.SaveLoaded += UpdateChrome;
        if (!BuildPolicy.IsRetail)
            _proxy.PatchPayloadProvider = GetPatchPayload;
        _proxy.Error += OnProxyError;
        _proxy.SessionChanged += OnPlayFabSessionChanged;
        _proxy.TrafficObserved += OnPlayFabTraffic;
        _game.ConnectionChanged += OnGameConnectionChanged;
        LocalizationService.Current.LanguageChanged += OnAppLanguageChanged;
        Closed += OnClosed;
        Status.Closed += (_, _) => Status.Visibility = Visibility.Collapsed;
        _statusDismissTimer.Tick += (_, _) =>
        {
            _statusDismissTimer.Stop();
            Status.IsOpen = false;
            Status.Visibility = Visibility.Collapsed;
        };
        _patchSerializeTimer.Tick += OnPatchSerializeTick;
        _liveToolsCardTimer.Tick += OnLiveToolsCardTimerTick;
        RootGrid.Loaded += OnRootGridLoaded;

        _sessionWatcher = new GameSessionWatcher(
            _game,
            () => !_windowClosed && !IsLiveToolsBlockingConnect);
        _sessionWatcher.PhaseChanged += OnGameConnectionChanged;
        TryLoadSavedPlayFabSession();
        Nav.SelectedItem = HomeNavItem;
        PresentPage(GetOrCreatePage(typeof(HomePage), out _));
        UpdateChrome();
        UpdateGameConnectionChrome();
        UpdateCloudActions();
        DispatcherQueue.TryEnqueue(() => _ = RunLiveToolsMaintenanceAsync(automatic: true, forcePickFolder: false));
        DispatcherQueue.TryEnqueue(() => _ = CheckPublishedVersionsAsync());
    }

    public event EventHandler? LiveToolsMaintenanceChanged;

    /// <summary>
    /// True while an automatic or manual live-tools install/update still has to finish.
    /// Game-session connect stays disabled until this is false.
    /// </summary>
    public bool IsLiveToolsBlockingConnect => !_liveToolsGateOpen || _installingBridge;

    public string? LiveToolsActivityText
    {
        get
        {
            if (_liveToolsCardKind is LiveToolsCardKind.Hidden or LiveToolsCardKind.Succeeded)
                return null;
            if (_liveToolsDownload is { } progress)
                return FormatDownloadDetail(progress);
            if (_liveToolsDetailLiteral is not null)
                return _liveToolsDetailLiteral;
            return _liveToolsDetailKey is null ? null : L.Get(_liveToolsDetailKey);
        }
    }

    public static MainWindow? Instance { get; private set; }

    public void SetLanguage(string language)
        => LocalizationService.Current.SetLanguage(language);

    /// <summary>
    /// Restores and focuses this window when another launch is redirected here.
    /// </summary>
    public void BringToForeground()
    {
        AppWindow.Show();
        Activate();
        SetForegroundWindow(Hwnd);
    }

    private nint Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    private void ApplyBuildPolicy()
    {
        if (!BuildPolicy.IsRetail)
            return;

        ToolTipService.SetToolTip(
            PatchSettingsButton,
            L.Get("shell.tip_retail_readonly"));
    }

    private void SetWindowIcon()
    {
        string iconPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "HaloMeisterIcon.ico");

        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);
    }

    private void OnAppLanguageChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyShellLocalization();
            UpdateGameConnectionChrome();
            UpdateCloudActions();
            ApplyBuildPolicy();
            ApplyLiveToolsCard();

            if (Nav.SelectedItem is not NavigationViewItem item)
                return;

            string? tag = item.Tag as string;
            if (tag is null)
                return;

            Type page = ResolvePageType(tag);
            _ = NavigateContentAsync(
                page,
                page == typeof(LiveToolsHubPage) ? tag : null,
                forceReload: true);
        });
    }

    private void ApplyShellLocalization()
    {
        AppTaglineText.Text = L.Get("shell.meteorite_saves_settings_live_tools");
        CloudTitleText.Text = L.Get("shell.playfab_cloud_save");
        GetUserDataButton.Label = L.Get("shell.download_save");
        PatchSettingsButton.Label = L.Get("shell.upload_changes");
        NavigationLoadingText.Text = L.Get("common.loading");

        HomeNavItem.Content = L.Get("shell.home");
        ProgressProfileNavItem.Content = L.Get("shell.progress_profile");
        CampaignProgressNavItem.Content = L.Get("shell.campaign_progress");
        ProfileNavItem.Content = L.Get("shell.profile_entitlements");
        RawNavItem.Content = L.Get("shell.raw_save_data");
        GameFilesNavItem.Content = L.Get("shell.game_files");
        CustomizationNavItem.Content = L.Get("shell.customization");
        ConfigNavItem.Content = L.Get("shell.game_settings");
        GameSavesNavItem.Content = L.Get("shell.game_saves");
        BuiltinModNavItem.Content = L.Get("shell.builtin_mod");
        LiveToolsNavItem.Content = L.Get("shell.live_tools");
        GameplayNavItem.Content = L.Get("shell.gameplay");
        SpawnEquipNavItem.Content = L.Get("shell.spawn_equip");
        AllegianceNavItem.Content = L.Get("shell.allegiance");
        // AiBattleNavItem.Content = L.Get("shell.ai_battle");
        PlayerAppearanceNavItem.Content = L.Get("shell.player_appearance");
        CameraWorldNavItem.Content = L.Get("shell.camera_world");
        CinematicsNavItem.Content = L.Get("shell.cinematics");
        GameResourcesNavItem.Content = L.Get("shell.game_resources");
        MusicNavItem.Content = L.Get("shell.music");
        CharacterModelsNavItem.Content = L.Get("shell.character_models");
        GameTextNavItem.Content = L.Get("shell.game_text");
        SubtitlesNavItem.Content = L.Get("shell.subtitles");
        ChangeBipedNavItem.Content = L.Get("shell.change_character");
        AdvancedNavItem.Content = L.Get("shell.advanced");
        // RuntimeTagsNavItem.Content = L.Get("shell.realtime_tags");
        ScenarioPropsNavItem.Content = L.Get("shell.scenario_props");
        ScenarioPaletteNavItem.Content = L.Get("shell.scenario_palette");
        ScriptingNavItem.Content = L.Get("shell.scripting");
        RemoteNavItem.Content = L.Get("shell.phone_remote");
        SetupNavItem.Content = L.Get("shell.setup");
        HelpNavItem.Content = L.Get("shell.help");
        // CommunityNavItem.Content = L.Get("shell.community_links");
    }

    private void UpdateChrome()
    {
        UpdateCloudActions();
        // DirtyChanged can fire per field edit; debounce the full-document serialize.
        _patchSerializeTimer.Stop();
        _patchSerializeTimer.Start();
    }

    private void OnPatchSerializeTick(object? sender, object e)
    {
        _patchSerializeTimer.Stop();
        try
        {
            Volatile.Write(ref _patchPayload, _state.Save?.Document.Serialize());
        }
        catch (Exception ex)
        {
            Report(L.Format("shell.patch_snapshot_failed", UserFacingErrors.Format(ex)), InfoBarSeverity.Error);
        }
    }

    private void OnGameConnectionChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(UpdateGameConnectionChrome);

    private void AllowGameSessionConnect()
    {
        _liveToolsGateOpen = true;
        _sessionWatcher.RequestProbe();
    }

    private void UpdateGameConnectionChrome()
    {
        bool connected = _game.IsConnected;
        GameSessionPhase phase = connected ? GameSessionPhase.Connected : _sessionWatcher.Phase;
        GameConnectionIndicator.Fill = connected
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.Colors.LimeGreen)
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                "TextFillColorTertiaryBrush"];
        GameConnectionText.Text = connected
            ? L.Format("shell.connected_pid", _game.ProcessId)
            : IsLiveToolsBlockingConnect
                ? L.Get("shell.game_tools_pending")
                : phase switch
                {
                    GameSessionPhase.WaitingForMission => L.Get("shell.game_waiting"),
                    GameSessionPhase.Failed => L.Get("shell.game_connect_failed"),
                    _ => L.Get("shell.game_idle"),
                };
    }

    public async Task LaunchGameAsync()
    {
        try
        {
            // bool steam = GamePlatformPreference.Current.IsSteam;
            bool launched = await GamePlatformPreference.Current.LaunchGameAsync();
            Report(
                launched
                    ? L.Get("shell.launch_requested_steam")
                    // ? L.Get(steam
                    //     ? "shell.launch_requested_steam"
                    //     : "shell.launch_requested")
                    : L.Get("shell.launch_rejected"),
                launched ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                launched ? L.Get("shell.launching_game") : L.Get("shell.could_not_launch"));
        }
        catch (Exception ex)
        {
            Report(UserFacingErrors.Format(ex), InfoBarSeverity.Error, L.Get("shell.could_not_launch"));
        }
    }

    public Task InstallLiveToolsAsync(bool forcePickFolder = false)
        => RunLiveToolsMaintenanceAsync(automatic: false, forcePickFolder: forcePickFolder);

    private async Task RunLiveToolsMaintenanceAsync(bool automatic, bool forcePickFolder)
    {
        if (_installingBridge || _windowClosed)
            return;
        if (automatic && _suppressAutoLiveTools && !forcePickFolder)
        {
            AllowGameSessionConnect();
            SetLiveToolsCard(LiveToolsCardKind.Hidden);
            return;
        }

        _installingBridge = true;
        _liveToolsGateOpen = false;
        _liveToolsCardTimer.Stop();
        _liveToolsDownload = null;
        if (!automatic)
            _suppressAutoLiveTools = false;
        PublishLiveToolsMaintenance();

        bool succeeded = false;
        try
        {
            Task<LiveToolsPlan> inspectTask = Task.Run(InspectLiveTools);
            if (await Task.WhenAny(inspectTask, Task.Delay(400)) != inspectTask)
            {
                SetLiveToolsCard(
                    LiveToolsCardKind.Checking,
                    detailKey: "shell.live_tools_card_checking_detail");
            }

            LiveToolsPlan plan = await inspectTask;
            if (forcePickFolder || (!automatic && plan.NeedsFolder))
            {
                string? picked = await PickGameFolderAsync(forcePickFolder);
                if (picked is null)
                {
                    if (plan.NeedsWork)
                    {
                        SetLiveToolsCard(
                            LiveToolsCardKind.NeedFolder,
                            detailKey: "shell.live_tools_card_need_folder");
                    }
                    else
                    {
                        succeeded = true;
                        SetLiveToolsCard(LiveToolsCardKind.Hidden);
                    }

                    return;
                }

                plan = await Task.Run(InspectLiveTools);
            }

            if (automatic && !plan.NeedsWork)
            {
                succeeded = true;
                SetLiveToolsCard(LiveToolsCardKind.Hidden);
                return;
            }

            if (plan.NeedsFolder)
            {
                SetLiveToolsCard(
                    LiveToolsCardKind.NeedFolder,
                    detailKey: "shell.live_tools_card_need_folder");
                return;
            }

            _liveToolsIsUpdate = plan.BridgeInstalled && !plan.NeedsLoader;
            if (!automatic && plan.NeedsLoader)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = L.Get("shell.install_bridge_title"),
                    Content = L.Format(
                        "shell.install_bridge_body",
                        Ue4ssLoaderInstaller.Version),
                    PrimaryButtonText = L.Get("common.install"),
                    CloseButtonText = L.Get("common.cancel"),
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    SetLiveToolsCard(
                        LiveToolsCardKind.Failed,
                        detailKey: "shell.live_tools_card_cancelled");
                    return;
                }
            }

            await PerformLiveToolsInstallAsync(plan);
            succeeded = true;
        }
        catch (OperationCanceledException) when (_windowClosed)
        {
        }
        catch (Exception ex)
        {
            SetLiveToolsCard(
                LiveToolsCardKind.Failed,
                detailLiteral: ex.Message);
            Report(UserFacingErrors.Format(ex), InfoBarSeverity.Error, L.Get("shell.could_not_install_bridge"));
        }
        finally
        {
            _installingBridge = false;
            if (succeeded)
                AllowGameSessionConnect();
            PublishLiveToolsMaintenance();
        }
    }

    private async Task PerformLiveToolsInstallAsync(LiveToolsPlan plan)
    {
        string? root = plan.GameDirectory;
        bool needsLoader = plan.NeedsLoader;
        SetLiveToolsCard(
            _liveToolsIsUpdate ? LiveToolsCardKind.Updating : LiveToolsCardKind.Installing,
            detailKey: "shell.live_tools_card_preparing");
        while (!_windowClosed)
        {
            if (IsCampaignGameRunning())
            {
                SetLiveToolsCard(
                    LiveToolsCardKind.WaitingForGame,
                    detailKey: "shell.live_tools_card_wait_for_game");
                while (!_windowClosed && IsCampaignGameRunning())
                    await Task.Delay(1000);
                if (_windowClosed)
                    throw new OperationCanceledException();
            }

            try
            {
                Ue4ssLoaderInstallResult? loaderResult = null;
                if (needsLoader)
                {
                    if (root is null)
                    {
                        throw new DirectoryNotFoundException(
                            "Could not find HaloCampaignEvolved.exe under the selected folder.");
                    }

                    SetLiveToolsCard(
                        LiveToolsCardKind.Installing,
                        detailKey: "shell.live_tools_card_preparing");
                    int downloadGeneration = _liveToolsDownloadGeneration;
                    var downloadProgress = new Progress<Ue4ssDownloadProgress>(
                        progress => OnUe4ssDownloadProgress(progress, downloadGeneration));
                    loaderResult = await _loaderInstaller.InstallAsync(root, downloadProgress);
                    root = loaderResult.BinaryDirectory;
                    needsLoader = false;
                }

                SetLiveToolsCard(
                    _liveToolsIsUpdate ? LiveToolsCardKind.Updating : LiveToolsCardKind.Installing,
                    detailKey: _liveToolsIsUpdate
                        ? "shell.live_tools_card_updating_bridge"
                        : "shell.live_tools_card_writing_bridge");
                string installedPath = await Task.Run(() => _bridge.InstallOrUpdateBridge(root));
                SetLiveToolsCard(
                    LiveToolsCardKind.Succeeded,
                    detailKey: _liveToolsIsUpdate
                        ? "shell.live_tools_card_update_done_detail"
                        : "shell.live_tools_card_install_done_detail");
                ScheduleHideLiveToolsCard();
                Report(
                    loaderResult is null
                        ? L.Format("shell.bridge_installed_msg", installedPath)
                        : L.Format(
                            "shell.live_tools_installed_msg",
                            loaderResult.Version,
                            loaderResult.BackupDirectory),
                    InfoBarSeverity.Success,
                    loaderResult is null
                        ? L.Get(_liveToolsIsUpdate
                            ? "shell.live_tools_card_update_done_title"
                            : "shell.bridge_installed_title")
                        : L.Get("shell.live_tools_installed_title"));
                return;
            }
            catch (InvalidOperationException) when (IsCampaignGameRunning())
            {
            }
            catch (IOException) when (IsCampaignGameRunning())
            {
            }
        }

        throw new OperationCanceledException();
    }

    private LiveToolsPlan InspectLiveTools()
    {
        string? directory = _loaderInstaller.FindGameBinaryDirectory()
            ?? _loaderInstaller.FindInstalledBinaryDirectory();
        ScriptingBridgeStatus status = _bridge.GetStatus();
        bool loaderInstalled = directory is not null && _loaderInstaller.IsInstalled(directory);
        BridgeVersion packaged = _bridge.PackagedVersion;
        bool bridgeStale = status.IsInstalled &&
            (status.InstalledVersion is not BridgeVersion installed ||
             installed < packaged);
        return new LiveToolsPlan(directory, loaderInstalled, status.IsInstalled, bridgeStale);
    }

    private async Task<string?> PickGameFolderAsync(bool clearRememberedBridge)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is null)
            return null;

        GameInstallationService.Current.Remember(folder.Path);
        if (clearRememberedBridge)
            _bridge.ClearRememberedInstallLocation();
        _bridge.InvalidateStatusCaches();
        return GameInstallationService.Current.BinaryDirectory ?? folder.Path;
    }

    private void OnUe4ssDownloadProgress(Ue4ssDownloadProgress progress, int generation)
    {
        RunOnUi(() =>
        {
            if (generation != _liveToolsDownloadGeneration)
                return;
            _liveToolsDownload = progress;
            _liveToolsCardKind = LiveToolsCardKind.Installing;
            _liveToolsDetailKey = null;
            _liveToolsDetailLiteral = null;
            _liveToolsCardProgress = progress.TotalBytes is { } total && total > 0
                ? 100.0 * progress.BytesReceived / total
                : null;
            ApplyLiveToolsCard();
        });
    }

    private string FormatDownloadDetail(Ue4ssDownloadProgress progress)
    {
        string speed = FormatTransferSpeed(progress.BytesPerSecond);
        if (progress.TotalBytes is { } total && total > 0)
        {
            double percent = 100.0 * progress.BytesReceived / total;
            return L.Format(
                "shell.ue4ss_download_progress",
                FormatTransferBytes(progress.BytesReceived),
                FormatTransferBytes(total),
                percent.ToString("0.0"),
                speed);
        }

        return L.Format(
            "shell.ue4ss_download_progress_unknown",
            FormatTransferBytes(progress.BytesReceived),
            speed);
    }

    private void OnLiveToolsCardAction(object sender, RoutedEventArgs e)
    {
        if (_installingBridge)
            return;
        if (_liveToolsCardKind == LiveToolsCardKind.NeedFolder)
            _ = RunLiveToolsMaintenanceAsync(automatic: true, forcePickFolder: true);
        else if (_liveToolsCardKind == LiveToolsCardKind.Failed)
            _ = RunLiveToolsMaintenanceAsync(automatic: true, forcePickFolder: false);
    }

    private async Task CheckPublishedVersionsAsync()
    {
        try
        {
            PublishedVersionReport? report = await PublishedVersionService.Current.CheckAsync();
            if (report is not { HasUpdate: true } || _windowClosed)
                return;
            Report(report.Message, InfoBarSeverity.Warning, L.Get("version.manifest_title"));
        }
        catch (Exception ex)
        {
            App.LogCrash("PublishedVersion", ex);
        }
    }

    private void OnRootGridLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var shadow = new Microsoft.UI.Xaml.Media.ThemeShadow();
            LiveToolsStatusCard.Shadow = shadow;
            LiveToolsStatusCard.Translation = new Vector3(0, 0, 32);
            Status.Shadow = shadow;
            Status.Translation = new Vector3(0, 0, 32);
        }
        catch (Exception ex)
        {
            App.LogCrash("LiveToolsCardShadow", ex);
        }
    }

    private void OnLiveToolsCardTimerTick(object? sender, object e)
    {
        _liveToolsCardTimer.Stop();
        if (_liveToolsCardKind == LiveToolsCardKind.Succeeded)
            SetLiveToolsCard(LiveToolsCardKind.Hidden);
    }

    private void ScheduleHideLiveToolsCard()
    {
        RunOnUi(() =>
        {
            _liveToolsCardTimer.Stop();
            _liveToolsCardTimer.Start();
        });
    }

    private void SetLiveToolsCard(
        LiveToolsCardKind kind,
        string? detailKey = null,
        string? detailLiteral = null,
        double? progress = null,
        bool notify = true)
    {
        _liveToolsDownloadGeneration++;
        _liveToolsDownload = null;
        _liveToolsCardKind = kind;
        _liveToolsDetailKey = detailKey;
        _liveToolsDetailLiteral = detailLiteral;
        _liveToolsCardProgress = progress;
        RunOnUi(() =>
        {
            ApplyLiveToolsCard();
            if (notify)
                PublishLiveToolsMaintenanceCore();
        });
    }

    private void ApplyLiveToolsCard()
    {
        if (_liveToolsCardKind == LiveToolsCardKind.Hidden)
        {
            LiveToolsStatusCard.Visibility = Visibility.Collapsed;
            LiveToolsCardRing.IsActive = false;
            return;
        }

        bool busy = _liveToolsCardKind is LiveToolsCardKind.Checking
            or LiveToolsCardKind.Installing
            or LiveToolsCardKind.Updating
            or LiveToolsCardKind.WaitingForGame;
        LiveToolsStatusCard.Visibility = Visibility.Visible;
        LiveToolsCardTitle.Text = _liveToolsCardKind switch
        {
            LiveToolsCardKind.Checking => L.Get("shell.live_tools_card_checking_title"),
            LiveToolsCardKind.Updating => L.Get("shell.live_tools_card_updating_title"),
            LiveToolsCardKind.WaitingForGame => L.Get(_liveToolsIsUpdate
                ? "shell.live_tools_card_updating_title"
                : "shell.live_tools_card_installing_title"),
            LiveToolsCardKind.NeedFolder => L.Get("shell.live_tools_card_need_folder_title"),
            LiveToolsCardKind.Succeeded => L.Get(_liveToolsIsUpdate
                ? "shell.live_tools_card_update_done_title"
                : "shell.live_tools_card_install_done_title"),
            LiveToolsCardKind.Failed => L.Get("shell.live_tools_card_failed_title"),
            _ => L.Get("shell.live_tools_card_installing_title"),
        };
        LiveToolsCardDetail.Text = _liveToolsDownload is { } progress
            ? FormatDownloadDetail(progress)
            : _liveToolsDetailLiteral
                ?? (_liveToolsDetailKey is null ? "" : L.Get(_liveToolsDetailKey));

        LiveToolsCardRing.IsActive = busy;
        LiveToolsCardRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        LiveToolsCardIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        LiveToolsCardIcon.Symbol = _liveToolsCardKind switch
        {
            LiveToolsCardKind.NeedFolder => Symbol.Folder,
            LiveToolsCardKind.Failed => Symbol.Important,
            _ => Symbol.Accept,
        };
        string brushKey = _liveToolsCardKind switch
        {
            LiveToolsCardKind.Failed => "SystemFillColorCriticalBrush",
            LiveToolsCardKind.Succeeded => "SystemFillColorSuccessBrush",
            _ => "TextFillColorPrimaryBrush",
        };
        if (Application.Current.Resources.TryGetValue(brushKey, out object resource) &&
            resource is Microsoft.UI.Xaml.Media.Brush brush)
        {
            LiveToolsCardIcon.Foreground = brush;
        }

        if (_liveToolsCardProgress is { } value)
        {
            LiveToolsCardProgress.Visibility = Visibility.Visible;
            LiveToolsCardProgress.Value = value;
        }
        else
        {
            LiveToolsCardProgress.Visibility = Visibility.Collapsed;
        }

        if (_liveToolsCardKind == LiveToolsCardKind.NeedFolder)
        {
            LiveToolsCardAction.Visibility = Visibility.Visible;
            LiveToolsCardAction.Content = L.Get("shell.live_tools_card_pick_folder");
        }
        else if (_liveToolsCardKind == LiveToolsCardKind.Failed)
        {
            LiveToolsCardAction.Visibility = Visibility.Visible;
            LiveToolsCardAction.Content = L.Get("shell.live_tools_card_retry");
        }
        else
        {
            LiveToolsCardAction.Visibility = Visibility.Collapsed;
        }
    }

    private void PublishLiveToolsMaintenance()
        => RunOnUi(PublishLiveToolsMaintenanceCore);

    private void PublishLiveToolsMaintenanceCore()
    {
        UpdateGameConnectionChrome();
        LiveToolsMaintenanceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RunOnUi(Action action)
    {
        if (_windowClosed)
            return;
        if (DispatcherQueue.HasThreadAccess)
            action();
        else
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_windowClosed)
                    action();
            });
    }

    private static bool IsCampaignGameRunning()
    {
        Process[] processes = Process.GetProcessesByName("HaloCampaignEvolved");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }
    }

    private readonly record struct LiveToolsPlan(
        string? GameDirectory,
        bool LoaderInstalled,
        bool BridgeInstalled,
        bool BridgeStale)
    {
        public bool NeedsFolder => GameDirectory is null && !BridgeInstalled;
        public bool NeedsLoader => GameDirectory is not null && !LoaderInstalled;
        public bool NeedsWork => NeedsFolder || NeedsLoader || !BridgeInstalled || BridgeStale;
    }

    private enum LiveToolsCardKind
    {
        Hidden,
        Checking,
        Installing,
        Updating,
        WaitingForGame,
        NeedFolder,
        Succeeded,
        Failed,
    }

    private static string FormatTransferBytes(long bytes)
    {
        const double kib = 1024;
        const double mib = kib * 1024;
        if (bytes >= mib)
            return $"{bytes / mib:0.00} MiB";
        if (bytes >= kib)
            return $"{bytes / kib:0.0} KiB";
        return $"{bytes} B";
    }

    private static string FormatTransferSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
            return "—";
        return $"{FormatTransferBytes((long)bytesPerSecond)}/s";
    }

    public async Task UninstallLiveToolsAsync()
    {
        if (_installingBridge) return;

        try
        {
            if (!_bridge.HasRemovableInstall())
            {
                Report(
                    L.Get("shell.bridge_not_installed_msg"),
                    InfoBarSeverity.Informational,
                    L.Get("shell.bridge_uninstalled_title"));
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = L.Get("setup.uninstall"),
                Content = L.Get("setup.uninstall_confirm"),
                PrimaryButtonText = L.Get("setup.uninstall"),
                CloseButtonText = L.Get("common.cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            _installingBridge = true;
            PublishLiveToolsMaintenance();
            string removedPath = await Task.Run(_bridge.UninstallBridge);
            _suppressAutoLiveTools = true;
            AllowGameSessionConnect();
            SetLiveToolsCard(LiveToolsCardKind.Hidden);
            Report(
                string.IsNullOrEmpty(removedPath)
                    ? L.Get("shell.bridge_uninstalled_cleared_msg")
                    : L.Format("shell.bridge_uninstalled_msg", removedPath),
                InfoBarSeverity.Success,
                L.Get("shell.bridge_uninstalled_title"));
        }
        catch (Exception ex)
        {
            Report(UserFacingErrors.Format(ex), InfoBarSeverity.Error, L.Get("shell.could_not_uninstall_bridge"));
        }
        finally
        {
            _installingBridge = false;
            PublishLiveToolsMaintenance();
        }
    }

    public async Task ChangeLiveToolsFolderAsync()
    {
        if (_installingBridge) return;

        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
            };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            GameInstallationService.Current.Remember(folder.Path);
            _bridge.ClearRememberedInstallLocation();
            _bridge.InvalidateStatusCaches();
            _suppressAutoLiveTools = false;
            Report(
                L.Format("shell.bridge_folder_updated_msg", folder.Path),
                InfoBarSeverity.Success,
                L.Get("shell.bridge_folder_updated_title"));
            await RunLiveToolsMaintenanceAsync(automatic: true, forcePickFolder: false);
        }
        catch (Exception ex)
        {
            Report(UserFacingErrors.Format(ex), InfoBarSeverity.Error, L.Get("shell.could_not_change_bridge_folder"));
        }
    }

    public bool IsInstallingLiveTools => _installingBridge;

    private byte[]? GetPatchPayload()
    {
        if (_patchSerializeTimer.IsEnabled)
            OnPatchSerializeTick(null, EventArgs.Empty);
        byte[]? snapshot = Volatile.Read(ref _patchPayload);
        return snapshot?.ToArray();
    }

    public void ReportCrash(Exception ex)
        => Report(UserFacingErrors.Format(ex), InfoBarSeverity.Error, L.Get("common.something_went_wrong"));

    public void Report(
        string message,
        InfoBarSeverity severity = InfoBarSeverity.Informational,
        string? title = null,
        bool sanitize = true)
    {
        _statusDismissTimer.Stop();
        Status.Title = title ?? severity switch
        {
            InfoBarSeverity.Error => L.Get("common.something_went_wrong"),
            InfoBarSeverity.Warning => L.Get("common.careful"),
            InfoBarSeverity.Success => L.Get("common.done"),
            _ => L.Get("common.info"),
        };
        Status.Message = sanitize
            ? UserFacingErrors.ForDisplay(message, severity)
            : message;
        Status.Severity = severity;
        Status.Visibility = Visibility.Visible;
        Status.IsOpen = true;
        if (severity == InfoBarSeverity.Success)
            _statusDismissTimer.Start();
    }

    public void DismissStatus()
    {
        _statusDismissTimer.Stop();
        Status.IsOpen = false;
        Status.Visibility = Visibility.Collapsed;
    }

    private static Type ResolvePageType(string? tag) => tag switch
    {
        "home" => typeof(HomePage),
        "phone-remote" => typeof(RemoteControlPage),
        "setup" => typeof(SetupPage),
        "help" => typeof(ReadmePage),
        "community" => typeof(CommunityPage),
        "campaign-progress" => typeof(CampaignProgressPage),
        "customization" => typeof(CustomizationPage),
        "profile" => typeof(ProfilePage),
        "raw" => typeof(RawPage),
        "config" => typeof(ConfigPage),
        "game-saves" => typeof(GameSavesPage),
        "builtin-mod" => typeof(BuiltinModPage),
        "live-gameplay" or "live-spawn" or "live-player" or "live-world" => typeof(LiveToolsHubPage),
        "live-allegiance" => typeof(AllegianceDemoPage),
        // "live-ai-battle" => typeof(AiBattlePage),
        "change-biped" => typeof(ChangeBipedPage),
        "runtime-tags" => typeof(RuntimeTagsPage),
        "scenario-props" => typeof(ScenarioPropsPage),
        "scenario-palette" => typeof(ScenarioPalettePage),
        "scripting" => typeof(ScriptingPage),
        "cinematics" => typeof(CinematicsPage),
        "music" => typeof(MusicPage),
        "character-models" => typeof(CharacterModelsPage),
        "game-text" => typeof(GameTextPage),
        "subtitles" => typeof(SubtitlesPage),
        _ => typeof(MissionsPage),
    };

    private async void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;

        string? tag = item.Tag as string;
        // Parent section headers have no tag and must not force a page navigation.
        if (string.IsNullOrEmpty(tag))
            return;

        // Return to the dispatcher first so the NavigationView can paint the
        // selected item before any page create / swap work runs.
        await Task.Yield();

        Type page = ResolvePageType(tag);
        await NavigateContentAsync(
            page,
            page == typeof(LiveToolsHubPage) ? tag : null);
    }

    private async Task NavigateContentAsync(
        Type page,
        object? parameter = null,
        bool forceReload = false)
    {
        if (forceReload)
            ResetPageCache();

        bool isCloudContext =
            page == typeof(CampaignProgressPage) ||
            page == typeof(ProfilePage) ||
            page == typeof(RawPage);
        CloudActionsBar.Visibility = isCloudContext
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Same live-tools shell: swap section without recreating the hub page.
        if (!forceReload &&
            page == typeof(LiveToolsHubPage) &&
            parameter is string section &&
            ContentFrame.Content is LiveToolsHubPage hub)
        {
            try
            {
                await hub.ShowSectionAsync(section);
            }
            catch (Exception ex)
            {
                App.LogCrash("Navigate", ex);
                Report(UserFacingErrors.Format(ex), InfoBarSeverity.Error);
            }
            return;
        }

        if (!forceReload && ContentFrame.Content?.GetType() == page)
        {
            // Already visible: still refresh volatile status (bridge install, etc.).
            ActivatePage(ContentFrame.Content as Page);
            return;
        }

        bool coldStart = !_pageCache.ContainsKey(page);
        int generation = ++_navigationGeneration;
        if (coldStart)
        {
            SetNavigationLoading(true);
            await Task.Yield();
            if (generation != _navigationGeneration)
                return;
        }

        try
        {
            Page instance = GetOrCreatePage(page, out _);
            PresentPage(instance);

            if (page == typeof(LiveToolsHubPage) &&
                parameter is string liveSection &&
                instance is LiveToolsHubPage liveHub)
            {
                await liveHub.ShowSectionAsync(liveSection);
            }
        }
        catch (Exception ex)
        {
            App.LogCrash("Navigate", ex);
            Report(UserFacingErrors.Format(ex), InfoBarSeverity.Error);
        }
        finally
        {
            if (generation == _navigationGeneration)
                SetNavigationLoading(false);
        }
    }

    private Page GetOrCreatePage(Type pageType, out bool created)
    {
        if (_pageCache.TryGetValue(pageType, out Page? existing))
        {
            created = false;
            return existing;
        }

        Page page = (Page)Activator.CreateInstance(pageType)!;
        page.NavigationCacheMode = NavigationCacheMode.Required;
        _pageCache[pageType] = page;
        created = true;
        return page;
    }

    private void PresentPage(Page page)
    {
        if (!ReferenceEquals(_activePage, page))
        {
            DeactivatePage(_activePage);
            ContentFrame.Content = page;
            _activePage = page;
        }

        ActivatePage(page);
    }

    private static void ActivatePage(Page? page)
    {
        if (page is IActivatablePage activatable)
            activatable.OnActivated();
    }

    private static void DeactivatePage(Page? page)
    {
        if (page is IActivatablePage activatable)
            activatable.OnDeactivated();
    }

    private void ResetPageCache()
    {
        DeactivatePage(_activePage);
        _activePage = null;
        ContentFrame.Content = null;
        _pageCache.Clear();
        _navigationGeneration++;
    }

    private void SetNavigationLoading(bool loading)
    {
        NavigationLoadingOverlay.Visibility =
            loading ? Visibility.Visible : Visibility.Collapsed;
        NavigationLoadingRing.IsActive = loading;
    }

    public void NavigateTo(string tag)
    {
        NavigationViewItem? item = tag switch
        {
            "home" => HomeNavItem,
            "campaign-progress" => CampaignProgressNavItem,
            "profile" => ProfileNavItem,
            "raw" => RawNavItem,
            "customization" => CustomizationNavItem,
            "config" => ConfigNavItem,
            "game-saves" => GameSavesNavItem,
            "builtin-mod" => BuiltinModNavItem,
            "live-gameplay" => GameplayNavItem,
            "live-spawn" => SpawnEquipNavItem,
            "live-allegiance" => AllegianceNavItem,
            // "live-ai-battle" => AiBattleNavItem,
            "live-player" => PlayerAppearanceNavItem,
            "live-world" => CameraWorldNavItem,
            "cinematics" => CinematicsNavItem,
            "music" => MusicNavItem,
            "character-models" => CharacterModelsNavItem,
            "game-text" => GameTextNavItem,
            "subtitles" => SubtitlesNavItem,
            "change-biped" => ChangeBipedNavItem,
            // "runtime-tags" => RuntimeTagsNavItem,
            "scenario-props" => ScenarioPropsNavItem,
            "scenario-palette" => ScenarioPaletteNavItem,
            "scripting" => ScriptingNavItem,
            "phone-remote" => RemoteNavItem,
            "setup" => SetupNavItem,
            "help" => HelpNavItem,
            // "community" => CommunityNavItem,
            _ => null,
        };

        if (item is not null)
            Nav.SelectedItem = item;
    }

    private async void OnOpen(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);

            foreach (string extension in new[] { ".json", ".sav", ".dat", ".bin", ".txt", ".b64" })
                picker.FileTypeFilter.Add(extension);
            picker.FileTypeFilter.Add("*");

            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null) return;

            LoadFrom(file.Path);
        }
        catch (Exception ex)
        {
            Report(UserFacingErrors.Format(ex), InfoBarSeverity.Error);
        }
    }

    public void LoadFrom(string path)
    {
        try
        {
            HaloSave save = HaloSave.LoadFile(path);
            _state.Load(save);

            bool exact = save.VerifyRoundTrip(out string detail);
            IReadOnlyList<string> unknown = save.UnknownTags();

            string note = exact
                ? L.Format("shell.loaded_tags_verified", save.Tags.Count, detail)
                : L.Format("shell.loaded_tags_unverified", detail);

            if (unknown.Count > 0)
                note += " " + L.Format("shell.unknown_tags_note", unknown.Count);

            Report(note, exact ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            _state.Unload();
            Report(UserFacingErrors.Format(ex), InfoBarSeverity.Error);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_state.Save is not { } save) { Report(L.Get("shell.open_save_first"), InfoBarSeverity.Warning); return; }

        if (string.IsNullOrEmpty(save.Envelope.SourcePath))
        {
            OnSaveAs(sender, e);
            return;
        }

        try
        {
            save.Save(save.Envelope.SourcePath!);
            _state.MarkClean();
            UpdateChrome();
            Report(L.Format("shell.written_with_bak", save.Envelope.SourcePath),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Report(UserFacingErrors.Format(ex), InfoBarSeverity.Error);
        }
    }

    private async void OnSaveAs(object sender, RoutedEventArgs e)
    {
        if (_state.Save is not { } save) { Report(L.Get("shell.open_save_first"), InfoBarSeverity.Warning); return; }

        try
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);

            picker.FileTypeChoices.Add(L.Get("shell.same_format"), new List<string> { System.IO.Path.GetExtension(save.Envelope.SourcePath ?? ".json") is { Length: > 0 } ext ? ext : ".json" });
            picker.FileTypeChoices.Add(L.Get("shell.json"), new List<string> { ".json" });
            picker.FileTypeChoices.Add(L.Get("shell.binary_save"), new List<string> { ".sav" });
            picker.FileTypeChoices.Add(L.Get("shell.base64_text"), new List<string> { ".txt" });
            picker.SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(save.Envelope.SourcePath ?? "halo-save") + "-edited";

            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null) return;

            save.Save(file.Path, backup: false);
            _state.MarkClean();
            UpdateChrome();
            Report(L.Format("shell.written_to", file.Path), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Report(UserFacingErrors.Format(ex), InfoBarSeverity.Error);
        }
    }

    private void OnCopyBase64(object sender, RoutedEventArgs e)
    {
        if (_state.Save is not { } save) { Report(L.Get("shell.open_save_first"), InfoBarSeverity.Warning); return; }

        try
        {
            var package = new DataPackage();
            package.SetText(save.BuildBase64());
            Clipboard.SetContent(package);
            Report(L.Get("shell.base64_clipboard"), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Report(UserFacingErrors.Format(ex), InfoBarSeverity.Error);
        }
    }

    private async void OnPasteBase64(object sender, RoutedEventArgs e)
    {
        try
        {
            DataPackageView view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Text))
            {
                Report(L.Get("shell.clipboard_no_text"), InfoBarSeverity.Warning);
                return;
            }

            string text = await view.GetTextAsync();
            HaloSave save = HaloSave.LoadText(text);
            _state.Load(save);
            UpdateChrome();
            Report(L.Format("shell.loaded_from_clipboard", save.Tags.Count),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Report(UserFacingErrors.Format(ex), InfoBarSeverity.Error);
        }
    }

    private void OnVerify(object sender, RoutedEventArgs e)
    {
        if (_state.Save is not { } save) { Report(L.Get("shell.open_save_first"), InfoBarSeverity.Warning); return; }

        bool exact = save.VerifyRoundTrip(out string detail);
        Report(exact
                ? L.Format("shell.verify_ok", detail)
                : L.Format("shell.verify_diff", detail),
            exact ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private void OnReload(object sender, RoutedEventArgs e)
    {
        if (_state.Save?.Envelope.SourcePath is not { } path)
        {
            Report(L.Get("shell.nothing_to_reload"), InfoBarSeverity.Warning);
            return;
        }

        LoadFrom(path);
    }

    private void TryLoadSavedPlayFabSession()
    {
        if (!_proxy.HasSavedSession || _proxy.HasCapturedSession)
            return;

        try
        {
            _proxy.LoadSessionFromCredentialLocker();
        }
        catch (Exception ex)
        {
            Report(
                L.Format("shell.auth_load_failed", UserFacingErrors.Format(ex)),
                InfoBarSeverity.Warning,
                L.Get("shell.auth_unavailable"));
        }
    }

    private async void OnGetUserData(object sender, RoutedEventArgs e)
    {
        if (_state.IsDirty)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = L.Get("shell.replace_unsaved_title"),
                Content = L.Get("shell.replace_unsaved_body"),
                PrimaryButtonText = L.Get("shell.get_cloud_data"),
                CloseButtonText = L.Get("common.cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
        }

        await RunCloudOperation(async () =>
        {
            PlayFabGetResult result = await _proxy.GetSaveFromPlayFabAsync();
            _state.Load(result.Save);
            Report(
                L.Format(
                    "shell.cloud_loaded_msg",
                    result.Save.Tags.Count,
                    result.DataVersion?.ToString() ?? L.Get("common.unknown"),
                    result.BackupPath),
                InfoBarSeverity.Success,
                L.Get("shell.user_data_loaded"));
        });
    }

    private void OnSaveAuth(object sender, RoutedEventArgs e)
    {
        if (_awaitingAuthCapture)
        {
            _awaitingAuthCapture = false;
            _authSavedDuringCapture = false;
            _proxy.Stop();
            UpdateCloudActions();
            Report(
                L.Get("shell.capture_cancelled_msg"),
                InfoBarSeverity.Informational,
                L.Get("shell.capture_cancelled"));
            return;
        }

        if (_proxy.HasCapturedSession && !_proxy.HasSavedSession)
        {
            try
            {
                string host = _proxy.SaveSessionToCredentialLocker();
                UpdateCloudActions();
                Report(
                    L.Format("shell.auth_saved_host", host),
                    InfoBarSeverity.Success,
                    L.Get("shell.auth_saved"));
            }
            catch (Exception ex)
            {
                Report(UserFacingErrors.Format(ex), InfoBarSeverity.Error, L.Get("shell.could_not_save_auth"));
            }
            return;
        }

        try
        {
            _authSavedDuringCapture = false;
            _awaitingAuthCapture = true;
            _proxy.Start();
            UpdateCloudActions();
            Report(
                L.Get("shell.waiting_auth_msg"),
                InfoBarSeverity.Informational,
                L.Get("shell.waiting_auth_title"));
        }
        catch (Exception ex)
        {
            _awaitingAuthCapture = false;
            _authSavedDuringCapture = false;
            UpdateCloudActions();
            Report(UserFacingErrors.Format(ex), InfoBarSeverity.Error, L.Get("shell.could_not_start_capture"));
        }
    }

    private async void OnPatchSettings(object sender, RoutedEventArgs e)
    {
        if (BuildPolicy.IsRetail)
        {
            Report(
                L.Get("shell.retail_readonly_msg"),
                InfoBarSeverity.Informational,
                L.Get("shell.readonly_title"));
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = L.Get("shell.patch_title"),
            Content = L.Get("shell.patch_body"),
            PrimaryButtonText = L.Get("shell.backup_and_patch"),
            CloseButtonText = L.Get("common.cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        await RunCloudOperation(async () =>
        {
            PlayFabTestFlowResult result = await _proxy.RunGetPatchGetAsync();
            if (!result.Verified)
                throw new InvalidOperationException(
                    L.Format("shell.patch_verify_failed", result.Before.BackupPath));

            _state.Load(result.After.Save);
            Report(
                L.Format(
                    "shell.settings_patched_msg",
                    result.After.DataVersion?.ToString() ?? L.Get("common.unknown"),
                    result.Before.BackupPath),
                InfoBarSeverity.Success,
                L.Get("shell.settings_patched"));
        });
    }

    private async Task RunCloudOperation(Func<Task> operation)
    {
        if (_cloudBusy)
            return;

        _cloudBusy = true;
        UpdateCloudActions();
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            Report(UserFacingErrors.Format(ex), InfoBarSeverity.Error, L.Get("shell.playfab_failed"));
        }
        finally
        {
            _cloudBusy = false;
            UpdateCloudActions();
        }
    }

    private void OnPlayFabSessionChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_awaitingAuthCapture && !_authSavedDuringCapture)
            {
                try
                {
                    _proxy.SaveSessionToCredentialLocker();
                    _authSavedDuringCapture = true;
                    Report(
                        L.Get("shell.auth_captured_finishing"),
                        InfoBarSeverity.Success,
                        L.Get("shell.auth_saved"));
                }
                catch (Exception ex)
                {
                    Report(UserFacingErrors.Format(ex), InfoBarSeverity.Error, L.Get("shell.could_not_save_auth"));
                }
            }
            UpdateCloudActions();
        });
    }

    private void OnPlayFabTraffic(TrafficEntry entry)
    {
        if (!_awaitingAuthCapture ||
            !_authSavedDuringCapture ||
            !entry.IsPlayFab ||
            entry.StatusCode is null)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_awaitingAuthCapture || !_authSavedDuringCapture)
                return;
            _awaitingAuthCapture = false;
            _authSavedDuringCapture = false;
            _proxy.Stop();
            UpdateCloudActions();
            Report(
                BuildPolicy.IsRetail
                    ? L.Get("shell.auth_ready_retail")
                    : L.Get("shell.auth_ready_full"),
                InfoBarSeverity.Success,
                L.Get("shell.cloud_actions_ready"));
        });
    }

    private void UpdateCloudActions()
    {
        bool hasAuth = _proxy.HasCapturedSession;
        GetUserDataButton.IsEnabled = !_cloudBusy && !_awaitingAuthCapture && hasAuth;
        PatchSettingsButton.IsEnabled =
            !BuildPolicy.IsRetail &&
            !_cloudBusy && !_awaitingAuthCapture && hasAuth && _state.IsLoaded;
        SaveAuthButton.IsEnabled = !_cloudBusy;
        SaveAuthButton.Label = _awaitingAuthCapture
            ? L.Get("shell.cancel_authentication")
            : L.Get("shell.authenticate");
        CloudContextText.Text = _state.IsLoaded
            ? _state.IsDirty
                ? L.Get("shell.cloud_dirty")
                : L.Get("shell.cloud_clean")
            : hasAuth
                ? L.Get("shell.cloud_auth_ready")
                : L.Get("shell.cloud_need_auth");

        ToolTipService.SetToolTip(
            GetUserDataButton,
            hasAuth
                ? L.Format("shell.tip_load_blam", _proxy.SessionHost)
                : L.Get("shell.tip_save_auth_first"));
        ToolTipService.SetToolTip(
            SaveAuthButton,
            _awaitingAuthCapture
                ? L.Get("shell.tip_stop_capture")
                : _proxy.HasSavedSession
                    ? L.Get("shell.tip_refresh_session")
                    : L.Get("shell.tip_save_session"));
        ToolTipService.SetToolTip(
            PatchSettingsButton,
            BuildPolicy.IsRetail
                ? L.Get("shell.tip_retail_readonly")
                : hasAuth
                ? L.Get("shell.tip_patch_flow")
                : L.Get("shell.tip_save_auth_first"));
    }

    private void OnProxyError(string message)
        => DispatcherQueue.TryEnqueue(() => Report(message, InfoBarSeverity.Error));

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _windowClosed = true;
        _patchSerializeTimer.Stop();
        _statusDismissTimer.Stop();
        _liveToolsCardTimer.Stop();
        RemoteControlService.Current.StopForShutdown(TimeSpan.FromSeconds(3));
        MusicLibraryService.Current.Shutdown();
        LocalizationService.Current.LanguageChanged -= OnAppLanguageChanged;
        _proxy.Error -= OnProxyError;
        _proxy.SessionChanged -= OnPlayFabSessionChanged;
        _proxy.TrafficObserved -= OnPlayFabTraffic;
        _proxy.PatchPayloadProvider = null;
        _proxy.Stop();
        _sessionWatcher.PhaseChanged -= OnGameConnectionChanged;
        _sessionWatcher.Dispose();
        _game.ConnectionChanged -= OnGameConnectionChanged;
        _game.Dispose();
    }
}
