using HaloMeister.App.Localization;
using HaloMeister.App.Models;
using HaloMeister.App.Pages;
using HaloMeister.App.Services;
using HaloMeister.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    private bool _connectingToGame;
    private bool _installingBridge;
    private bool _cloudBusy;
    private bool _awaitingAuthCapture;
    private bool _authSavedDuringCapture;
    private readonly DispatcherTimer _statusDismissTimer = new()
    {
        Interval = TimeSpan.FromSeconds(4),
    };

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;
        SetWindowIcon();
        ApplyBuildPolicy();

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
        _statusDismissTimer.Tick += (_, _) =>
        {
            _statusDismissTimer.Stop();
            Status.IsOpen = false;
        };

        TryLoadSavedPlayFabSession();
        Nav.SelectedItem = HomeNavItem;
        ContentFrame.Navigate(typeof(HomePage));
        UpdateChrome();
        UpdateGameConnectionChrome();
        UpdateCloudActions();
    }

    public static MainWindow? Instance { get; private set; }

    public void SetLanguage(string language)
        => LocalizationService.Current.SetLanguage(language);

    private nint Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

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

            if (Nav.SelectedItem is not NavigationViewItem item)
                return;

            string? tag = item.Tag as string;
            if (tag is null)
                return;

            bool isCloudContext = tag is "campaign-progress" or "profile" or "raw";
            CloudActionsBar.Visibility = isCloudContext
                ? Visibility.Visible
                : Visibility.Collapsed;

            Type page = ResolvePageType(tag);
            ContentFrame.Content = null;
            if (page == typeof(LiveToolsHubPage))
                ContentFrame.Navigate(page, tag);
            else
                ContentFrame.Navigate(page);
        });
    }

    private void ApplyShellLocalization()
    {
        AppTaglineText.Text = L.Get("shell.meteorite_saves_settings_live_tools");
        CloudTitleText.Text = L.Get("shell.playfab_cloud_save");
        GetUserDataButton.Label = L.Get("shell.download_save");
        PatchSettingsButton.Label = L.Get("shell.upload_changes");

        HomeNavItem.Content = L.Get("shell.home");
        ProgressProfileNavItem.Content = L.Get("shell.progress_profile");
        CampaignProgressNavItem.Content = L.Get("shell.campaign_progress");
        ProfileNavItem.Content = L.Get("shell.profile_entitlements");
        RawNavItem.Content = L.Get("shell.raw_save_data");
        GameFilesNavItem.Content = L.Get("shell.game_files");
        CustomizationNavItem.Content = L.Get("shell.customization");
        ConfigNavItem.Content = L.Get("shell.game_settings");
        GameSavesNavItem.Content = L.Get("shell.game_saves");
        LiveToolsNavItem.Content = L.Get("shell.live_tools");
        GameplayNavItem.Content = L.Get("shell.gameplay");
        SpawnEquipNavItem.Content = L.Get("shell.spawn_equip");
        PlayerAppearanceNavItem.Content = L.Get("shell.player_appearance");
        CameraWorldNavItem.Content = L.Get("shell.camera_world");
        AdvancedNavItem.Content = L.Get("shell.advanced");
        RuntimeTagsNavItem.Content = L.Get("shell.realtime_tags");
        ScriptingNavItem.Content = L.Get("shell.scripting");
        RemoteNavItem.Content = L.Get("shell.phone_remote");
        SetupNavItem.Content = L.Get("shell.setup");
        HelpNavItem.Content = L.Get("shell.help");
        CommunityNavItem.Content = L.Get("shell.community_links");
    }

    private void UpdateChrome()
    {
        UpdateCloudActions();

        try
        {
            Volatile.Write(ref _patchPayload, _state.Save?.Document.Serialize());
        }
        catch (Exception ex)
        {
            Report(L.Format("shell.patch_snapshot_failed", ex.Message), InfoBarSeverity.Error);
        }
    }

    private async void OnConnectGame(object sender, RoutedEventArgs e)
        => await ConnectToGameAsync();

    public async Task ConnectToGameAsync()
    {
        if (_connectingToGame) return;

        _connectingToGame = true;
        UpdateGameConnectionChrome();
        try
        {
            await Task.Run(_game.Connect);
            Report(
                L.Format("shell.game_connected_msg", _game.ProcessId),
                InfoBarSeverity.Success,
                L.Get("shell.game_connected_title"));
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error, L.Get("shell.could_not_connect"));
        }
        finally
        {
            _connectingToGame = false;
            UpdateGameConnectionChrome();
        }
    }

    private void OnGameConnectionChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(UpdateGameConnectionChrome);

    private void UpdateGameConnectionChrome()
    {
        bool connected = _game.IsConnected;
        GameConnectionProgress.IsActive = _connectingToGame;
        GameConnectionProgress.Visibility =
            _connectingToGame ? Visibility.Visible : Visibility.Collapsed;
        GameConnectionIndicator.Visibility =
            _connectingToGame ? Visibility.Collapsed : Visibility.Visible;
        GameConnectionIndicator.Fill = connected
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.Colors.LimeGreen)
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                "TextFillColorTertiaryBrush"];
        GameConnectionText.Text = connected
            ? L.Format("shell.connected_pid", _game.ProcessId)
            : L.Get("shell.game_disconnected");
        GameConnectionButton.Content = connected
            ? L.Get("common.reconnect")
            : L.Get("common.connect");
        GameConnectionButton.IsEnabled = !_connectingToGame;
    }

    public async Task LaunchGameAsync()
    {
        try
        {
            bool steam = GamePlatformPreference.Current.IsSteam;
            bool launched = await GamePlatformPreference.Current.LaunchGameAsync();
            Report(
                launched
                    ? L.Get(steam
                        ? "shell.launch_requested_steam"
                        : "shell.launch_requested")
                    : L.Get("shell.launch_rejected"),
                launched ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                launched ? L.Get("shell.launching_game") : L.Get("shell.could_not_launch"));
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error, L.Get("shell.could_not_launch"));
        }
    }

    public async Task InstallLiveToolsAsync()
    {
        if (_installingBridge) return;

        string? selectedRoot = _loaderInstaller.FindInstalledBinaryDirectory();
        try
        {
            if (_bridge.FindInstalledMainPath() is null && selectedRoot is null)
            {
                var picker = new FolderPicker
                {
                    SuggestedStartLocation = PickerLocationId.ComputerFolder,
                };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
                StorageFolder? folder = await picker.PickSingleFolderAsync();
                if (folder is null) return;
                selectedRoot = folder.Path;
            }

            bool installLoader =
                selectedRoot is not null && !_loaderInstaller.IsInstalled(selectedRoot);
            if (installLoader)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = L.Get("shell.install_bridge_title"),
                    Content = L.Format(
                        "shell.install_bridge_body",
                        Ue4ssLoaderInstaller.Version),
                    PrimaryButtonText = L.Get("shell.download_and_install"),
                    CloseButtonText = L.Get("common.cancel"),
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    return;
            }

            _installingBridge = true;
            Ue4ssLoaderInstallResult? loaderResult = null;
            if (installLoader)
            {
                loaderResult = await Task.Run(
                    () => _loaderInstaller.InstallAsync(selectedRoot!));
                selectedRoot = loaderResult.BinaryDirectory;
            }

            string installedPath = await Task.Run(
                () => _bridge.InstallOrUpdateBridge(selectedRoot));
            Report(
                loaderResult is null
                    ? L.Format("shell.bridge_installed_msg", installedPath)
                    : L.Format(
                        "shell.live_tools_installed_msg",
                        loaderResult.Version,
                        loaderResult.BackupDirectory),
                InfoBarSeverity.Success,
                loaderResult is null
                    ? L.Get("shell.bridge_installed_title")
                    : L.Get("shell.live_tools_installed_title"));
        }
        catch (Exception ex)
        {
            Report(ex.Message, InfoBarSeverity.Error, L.Get("shell.could_not_install_bridge"));
        }
        finally
        {
            _installingBridge = false;
        }
    }

    public bool IsInstallingLiveTools => _installingBridge;

    private byte[]? GetPatchPayload()
    {
        byte[]? snapshot = Volatile.Read(ref _patchPayload);
        return snapshot?.ToArray();
    }

    public void ReportCrash(Exception ex)
        => Report(ex.Message, InfoBarSeverity.Error, L.Get("common.something_went_wrong"));

    private void Report(string message, InfoBarSeverity severity = InfoBarSeverity.Informational, string? title = null)
    {
        _statusDismissTimer.Stop();
        Status.Title = title ?? severity switch
        {
            InfoBarSeverity.Error => L.Get("common.something_went_wrong"),
            InfoBarSeverity.Warning => L.Get("common.careful"),
            InfoBarSeverity.Success => L.Get("common.done"),
            _ => L.Get("common.info"),
        };
        Status.Message = message;
        Status.Severity = severity;
        Status.IsOpen = true;
        if (severity == InfoBarSeverity.Success)
            _statusDismissTimer.Start();
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
        "live-gameplay" or "live-spawn" or "live-player" or "live-world" => typeof(LiveToolsHubPage),
        "runtime-tags" => typeof(RuntimeTagsPage),
        "scripting" => typeof(ScriptingPage),
        _ => typeof(MissionsPage),
    };

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;

        string? tag = item.Tag as string;
        // Parent section headers have no tag and must not force a page navigation.
        if (string.IsNullOrEmpty(tag))
            return;

        bool isCloudContext = tag is "campaign-progress" or "profile" or "raw";
        CloudActionsBar.Visibility = isCloudContext
            ? Visibility.Visible
            : Visibility.Collapsed;

        Type page = ResolvePageType(tag);

        try
        {
            if (page == typeof(LiveToolsHubPage))
                ContentFrame.Navigate(page, tag);
            else if (ContentFrame.CurrentSourcePageType != page)
                ContentFrame.Navigate(page);
        }
        catch (Exception ex)
        {
            App.LogCrash("Navigate", ex);
            Report(ex.Message, InfoBarSeverity.Error);
        }
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
            "live-gameplay" => GameplayNavItem,
            "live-spawn" => SpawnEquipNavItem,
            "live-player" => PlayerAppearanceNavItem,
            "live-world" => CameraWorldNavItem,
            "runtime-tags" => RuntimeTagsNavItem,
            "scripting" => ScriptingNavItem,
            "phone-remote" => RemoteNavItem,
            "setup" => SetupNavItem,
            "help" => HelpNavItem,
            "community" => CommunityNavItem,
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
            Report(ex.Message, InfoBarSeverity.Error);
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
            Report(ex.Message, InfoBarSeverity.Error);
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
            Report(ex.Message, InfoBarSeverity.Error);
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
            Report(ex.Message, InfoBarSeverity.Error);
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
            Report(ex.Message, InfoBarSeverity.Error);
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
            Report(ex.Message, InfoBarSeverity.Error);
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
                L.Format("shell.auth_load_failed", ex.Message),
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
                Report(ex.Message, InfoBarSeverity.Error, L.Get("shell.could_not_save_auth"));
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
            Report(ex.Message, InfoBarSeverity.Error, L.Get("shell.could_not_start_capture"));
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
            Report(ex.Message, InfoBarSeverity.Error, L.Get("shell.playfab_failed"));
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
                    Report(ex.Message, InfoBarSeverity.Error, L.Get("shell.could_not_save_auth"));
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
        RemoteControlService.Current.StopForShutdown(TimeSpan.FromSeconds(3));
        LocalizationService.Current.LanguageChanged -= OnAppLanguageChanged;
        _proxy.Error -= OnProxyError;
        _proxy.SessionChanged -= OnPlayFabSessionChanged;
        _proxy.TrafficObserved -= OnPlayFabTraffic;
        _proxy.PatchPayloadProvider = null;
        _proxy.Stop();
        _game.ConnectionChanged -= OnGameConnectionChanged;
        _game.Dispose();
    }
}
