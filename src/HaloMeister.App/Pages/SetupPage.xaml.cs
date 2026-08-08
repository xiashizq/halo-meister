using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class SetupPage : Page, IActivatablePage
{
    private readonly RuntimeTagMemoryService _game = RuntimeTagMemoryService.Current;
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private CancellationTokenSource? _heartbeatWatchCts;
    private bool _languageComboReady;
    private bool _checkingBridge;

    public SetupPage()
    {
        InitializeComponent();
        _refreshTimer.Tick += OnRefreshTick;
    }

    public void OnActivated()
    {
        // Cached page trees do not reliably re-fire Loaded; refresh on show.
        _bridge.InvalidateStatusCaches();
        PopulateLanguageCombo();
        ApplyStatus(_bridge.GetStatus());
        StartHeartbeatWatch();
        _refreshTimer.Start();
    }

    public void OnDeactivated()
    {
        _refreshTimer.Stop();
        StopHeartbeatWatch();
    }

    private void OnRefreshTick(object? sender, object e)
    {
        // Heartbeat watch already polls GetStatus while waiting; avoid doubling work.
        if (_checkingBridge)
            return;
        ApplyStatus(_bridge.GetStatus());
    }

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

    private void StartHeartbeatWatch()
    {
        StopHeartbeatWatch();
        if (_bridge.GetStatus().IsRuntimeReady)
            return;

        _heartbeatWatchCts = new CancellationTokenSource();
        CancellationToken token = _heartbeatWatchCts.Token;
        _ = WatchHeartbeatAsync(token);
    }

    private void StopHeartbeatWatch()
    {
        CancellationTokenSource? cts = _heartbeatWatchCts;
        _heartbeatWatchCts = null;
        if (cts is null)
            return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
        cts.Dispose();
    }

    private async Task WatchHeartbeatAsync(CancellationToken cancellationToken)
    {
        if (_checkingBridge)
            return;
        _checkingBridge = true;
        try
        {
            BridgeStatusText.Text = L.Get("setup.waiting_for_bridge_heartbeat");
            ScriptingBridgeStatus status = await _bridge.WaitForHeartbeatAsync(cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
                ApplyStatus(status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Page unloaded or a newer watch replaced this one.
        }
        finally
        {
            _checkingBridge = false;
        }
    }

    private void ApplyStatus(ScriptingBridgeStatus status)
    {
        bool connected = _game.IsConnected;
        GameStatusText.Text = connected
            ? L.Format("setup.connected_ready_pid", _game.ProcessId)
            : L.Get("setup.not_connected");
        ConnectButton.Content = connected ? L.Get("common.reconnect") : L.Get("common.connect");

        if (!status.IsRuntimeReady && (_checkingBridge || status.IsGameProcessRunning))
            BridgeStatusText.Text = L.Get("setup.waiting_for_bridge_heartbeat");
        else if (status.IsRuntimeReady && !status.IsStale)
            BridgeStatusText.Text = L.Get("setup.bridge_ready");
        else
            BridgeStatusText.Text = status.Summary;

        bool busy = MainWindow.Instance?.IsInstallingLiveTools == true;
        SetBridgeBusy(busy);
        InstallButton.Content = status.IsInstalled
            ? L.Get("common.repair_update")
            : L.Get("common.install");
        InstallButton.IsEnabled = !busy;
        UninstallButton.IsEnabled = !busy && _bridge.HasRemovableInstall();
        ChangeFolderButton.IsEnabled = !busy;
    }

    private void SetBridgeBusy(bool busy)
    {
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetConnectBusy(bool busy)
    {
        ConnectBusyRing.IsActive = busy;
        ConnectBusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnLaunchGame(object sender, RoutedEventArgs e)
    {
        await (MainWindow.Instance?.LaunchGameAsync() ?? Task.CompletedTask);
        StartHeartbeatWatch();
        ApplyStatus(_bridge.GetStatus());
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        SetConnectBusy(true);
        try
        {
            await (MainWindow.Instance?.ConnectToGameAsync() ?? Task.CompletedTask);
        }
        finally
        {
            SetConnectBusy(false);
            ConnectButton.IsEnabled = true;
            StartHeartbeatWatch();
            ApplyStatus(_bridge.GetStatus());
        }
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        UninstallButton.IsEnabled = false;
        ChangeFolderButton.IsEnabled = false;
        SetBridgeBusy(true);
        InstallButton.Content = L.Get("common.working");
        await (MainWindow.Instance?.InstallLiveToolsAsync() ?? Task.CompletedTask);
        StartHeartbeatWatch();
        ApplyStatus(_bridge.GetStatus());
    }

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        UninstallButton.IsEnabled = false;
        ChangeFolderButton.IsEnabled = false;
        SetBridgeBusy(true);
        UninstallButton.Content = L.Get("common.working");
        await (MainWindow.Instance?.UninstallLiveToolsAsync() ?? Task.CompletedTask);
        UninstallButton.Content = L.Get("setup.uninstall");
        StartHeartbeatWatch();
        ApplyStatus(_bridge.GetStatus());
    }

    private async void OnChangeFolder(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        UninstallButton.IsEnabled = false;
        ChangeFolderButton.IsEnabled = false;
        SetBridgeBusy(true);
        ChangeFolderButton.Content = L.Get("common.working");
        await (MainWindow.Instance?.ChangeLiveToolsFolderAsync() ?? Task.CompletedTask);
        ChangeFolderButton.Content = L.Get("setup.change_folder");
        ApplyStatus(_bridge.GetStatus());
    }

    private void OnOpenLiveTools(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("live-gameplay");
    private void OnOpenHelp(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("help");
}
