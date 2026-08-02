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
    private CancellationTokenSource? _heartbeatWatchCts;
    private bool _languageComboReady;
    private bool _checkingBridge;

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
        ApplyStatus(_bridge.GetStatus());
        StartHeartbeatWatch();
        _refreshTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer.Stop();
        StopHeartbeatWatch();
    }

    private void OnRefreshTick(object? sender, object e) => ApplyStatus(_bridge.GetStatus());

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

        InstallButton.Content = status.IsInstalled
            ? L.Get("common.repair_update")
            : L.Get("common.install");
        InstallButton.IsEnabled = MainWindow.Instance?.IsInstallingLiveTools != true;
    }

    private async void OnLaunchGame(object sender, RoutedEventArgs e)
    {
        await (MainWindow.Instance?.LaunchGameAsync() ?? Task.CompletedTask);
        StartHeartbeatWatch();
        ApplyStatus(_bridge.GetStatus());
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        await (MainWindow.Instance?.ConnectToGameAsync() ?? Task.CompletedTask);
        StartHeartbeatWatch();
        ApplyStatus(_bridge.GetStatus());
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        InstallButton.Content = L.Get("common.working");
        await (MainWindow.Instance?.InstallLiveToolsAsync() ?? Task.CompletedTask);
        StartHeartbeatWatch();
        ApplyStatus(_bridge.GetStatus());
    }

    private void OnOpenLiveTools(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("live-gameplay");
    private void OnOpenHelp(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("help");
}
