using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class SetupPage : Page
{
    private readonly RuntimeTagMemoryService _game = RuntimeTagMemoryService.Current;
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _languageComboReady;
    private bool _checkingBridge;
    private bool _bridgeWasGameRunning;

    public SetupPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _refreshTimer.Tick += OnRefreshTick;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PopulateLanguageCombo();
        _ = RefreshStatusAsync(retryForHeartbeat: true);
        _refreshTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _refreshTimer.Stop();
    private void OnRefreshTick(object? sender, object e) => _ = RefreshStatusAsync();

    private void PopulateLanguageCombo()
    {
        _languageComboReady = false;
        LanguageCombo.SelectionChanged -= OnLanguageChanged;
        LanguageCombo.Items.Clear();

        string current = LocalizationService.Current.Language;
        ComboBoxItem? selected = null;
        foreach ((string code, string nativeName) in LocalizationService.Current.Languages)
        {
            var item = new ComboBoxItem
            {
                Content = nativeName,
                Tag = code,
            };
            LanguageCombo.Items.Add(item);
            if (string.Equals(code, current, StringComparison.OrdinalIgnoreCase))
                selected = item;
        }

        LanguageCombo.SelectedItem = selected ?? LanguageCombo.Items.OfType<ComboBoxItem>().FirstOrDefault();
        LanguageCombo.SelectionChanged += OnLanguageChanged;
        _languageComboReady = true;
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_languageComboReady)
            return;
        if (LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string code })
            return;
        if (string.Equals(code, LocalizationService.Current.Language, StringComparison.OrdinalIgnoreCase))
            return;

        MainWindow.Instance?.SetLanguage(code);
    }

    private async Task RefreshStatusAsync(bool retryForHeartbeat = false)
    {
        bool connected = _game.IsConnected;
        GameStatusText.Text = connected
            ? L.Format("setup.connected_ready_pid", _game.ProcessId)
            : L.Get("setup.not_connected");
        ConnectButton.Content = connected ? L.Get("common.reconnect") : L.Get("common.connect");

        ScriptingBridgeStatus status = _bridge.GetStatus();
        bool startedSinceLastCheck = status.IsGameProcessRunning && !_bridgeWasGameRunning;
        _bridgeWasGameRunning = status.IsGameProcessRunning;
        if (!_checkingBridge && !status.IsRuntimeReady &&
            status.IsGameProcessRunning && (retryForHeartbeat || startedSinceLastCheck))
        {
            _checkingBridge = true;
            RecheckBridgeButton.IsEnabled = false;
            BridgeStatusText.Text = L.Get("setup.waiting_for_bridge_heartbeat");
            try
            {
                status = await _bridge.WaitForHeartbeatAsync();
            }
            finally
            {
                _checkingBridge = false;
                RecheckBridgeButton.IsEnabled = true;
            }
        }

        BridgeStatusText.Text = status.IsRuntimeReady && !status.IsStale
            ? L.Get("setup.bridge_ready")
            : status.Summary;
        InstallButton.Content = status.IsInstalled
            ? L.Get("common.repair_update")
            : L.Get("common.install");
        InstallButton.IsEnabled = MainWindow.Instance?.IsInstallingLiveTools != true;
    }

    private async void OnLaunchGame(object sender, RoutedEventArgs e)
        => await (MainWindow.Instance?.LaunchGameAsync() ?? Task.CompletedTask);

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        await (MainWindow.Instance?.ConnectToGameAsync() ?? Task.CompletedTask);
        await RefreshStatusAsync(retryForHeartbeat: true);
    }

    private async void OnRecheckBridge(object sender, RoutedEventArgs e)
        => await RefreshStatusAsync(retryForHeartbeat: true);

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        InstallButton.Content = L.Get("common.working");
        await (MainWindow.Instance?.InstallLiveToolsAsync() ?? Task.CompletedTask);
        await RefreshStatusAsync(retryForHeartbeat: true);
    }

    private void OnOpenLiveTools(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("live-gameplay");
    private void OnOpenHelp(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("help");
}
