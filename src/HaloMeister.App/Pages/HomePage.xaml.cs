using System.Diagnostics;
using HaloMeister.App.Localization;
using HaloMeister.App.Models;
using HaloMeister.App.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HaloMeister.App.Pages;

public sealed partial class HomePage : Page
{
    private readonly RuntimeTagMemoryService _game = RuntimeTagMemoryService.Current;
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private readonly PlayFabProxyService _cloud = PlayFabProxyService.Current;
    private readonly AppState _state = AppState.Current;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? _gameDirectory;

    public HomePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _refreshTimer.Tick += OnRefreshTick;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshStatus();
        _refreshTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _refreshTimer.Stop();
    private void OnRefreshTick(object? sender, object e) => RefreshStatus();

    private void RefreshStatus()
    {
        bool connected = _game.IsConnected;
        GameStatusDot.Fill = StatusBrush(connected);
        GameStatusText.Text = connected
            ? L.Format("home.game_connected_pid", _game.ProcessId)
            : L.Get("home.game_not_connected");
        ConnectButton.Content = connected ? L.Get("common.reconnect") : L.Get("common.connect");

        ScriptingBridgeStatus bridge = _bridge.GetStatus();
        BridgeStatusDot.Fill = StatusBrush(bridge.IsRuntimeReady);
        BridgeStatusText.Text = bridge.IsRuntimeReady
            ? L.Get("home.bridge_ready")
            : bridge.IsInstalled
                ? L.Get("home.bridge_installed")
                : L.Get("home.bridge_missing");

        bool cloudReady = _cloud.HasCapturedSession;
        CloudStatusDot.Fill = StatusBrush(cloudReady || _state.IsLoaded);
        CloudStatusText.Text = _state.IsLoaded
            ? _state.IsDirty ? L.Get("home.cloud_dirty") : L.Get("home.cloud_clean")
            : cloudReady ? L.Get("home.cloud_auth") : L.Get("home.cloud_need_auth");

        _gameDirectory = connected ? ResolveGameDirectory(_game.ModulePath) : null;
        bool hasGamePath = !string.IsNullOrWhiteSpace(_gameDirectory) && Directory.Exists(_gameDirectory);
        GamePathStatusDot.Fill = StatusBrush(hasGamePath);
        GamePathText.Text = hasGamePath
            ? _gameDirectory!
            : connected
                ? L.Get("home.game_path_unavailable")
                : L.Get("home.game_path_disconnected");
        OpenGamePathButton.IsEnabled = hasGamePath;
    }

    private static string? ResolveGameDirectory(string? modulePath)
    {
        if (string.IsNullOrWhiteSpace(modulePath))
            return null;

        string? current = Path.GetDirectoryName(Path.GetFullPath(modulePath));
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "HaloCampaignEvolved.exe")))
                return current;
            current = Path.GetDirectoryName(current);
        }

        string? moduleDirectory = Path.GetDirectoryName(modulePath);
        return Directory.Exists(moduleDirectory) ? moduleDirectory : null;
    }

    private static Brush StatusBrush(bool ready) => new SolidColorBrush(
        ready ? Colors.LimeGreen : Colors.Gray);

    private async void OnLaunchGame(object sender, RoutedEventArgs e)
        => await (MainWindow.Instance?.LaunchGameAsync() ?? Task.CompletedTask);

    private async void OnConnectGame(object sender, RoutedEventArgs e)
    {
        await (MainWindow.Instance?.ConnectToGameAsync() ?? Task.CompletedTask);
        RefreshStatus();
    }

    private void OnOpenProgress(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("campaign-progress");
    private void OnOpenCustomization(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("customization");
    private void OnOpenLiveTools(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("live-gameplay");
    private void OnOpenSetup(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("setup");
    private void OnOpenHelp(object sender, RoutedEventArgs e) => MainWindow.Instance?.NavigateTo("help");

    private void OnOpenGamePath(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_gameDirectory) || !Directory.Exists(_gameDirectory))
            return;

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_gameDirectory}\"")
        {
            UseShellExecute = true,
        });
    }
}
