using System.Collections.ObjectModel;
using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class CinematicsPage : Page, IActivatablePage
{
    private readonly RuntimeTagMemoryService _memory = RuntimeTagMemoryService.Current;
    private readonly CinematicsService _cinematics = new();
    private readonly ObservableCollection<LevelCinematic> _items = [];
    private int _busy;
    private int _scanVersion;

    public CinematicsPage()
    {
        InitializeComponent();
        CinematicList.ItemsSource = _items;
        LevelText.Text = L.Get("cinematics.scanning");
    }

    public void OnActivated() => _ = ScanAsync();

    private async void OnRefresh(object sender, RoutedEventArgs e) => await ScanAsync();

    private async void OnPlay(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LevelCinematic cinematic } || !TryEnter())
            return;

        try
        {
            ScriptExecutionResult result = await _cinematics.PlayAsync(cinematic);
            if (result.Outcome == ScriptOutcome.Failed)
            {
                ShowStatus(UserFacingErrors.FromBridge(result.Message), InfoBarSeverity.Error);
                return;
            }

            ShowStatus(
                L.Format("cinematics.played", cinematic.DisplayName),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowStatus(UserFacingErrors.Format(ex), InfoBarSeverity.Error);
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
        LevelText.Text = L.Get("cinematics.scanning");
        EmptyState.Visibility = Visibility.Collapsed;
        try
        {
            LevelCinematicsSession session = await Task.Run(() =>
            {
                if (!_memory.IsConnected)
                    _memory.Connect();
                return _cinematics.Scan();
            });
            if (version != _scanVersion)
                return;

            _items.Clear();
            foreach (LevelCinematic cinematic in session.Cinematics)
                _items.Add(cinematic);
            LevelText.Text = L.Format(
                "cinematics.count",
                session.Cinematics.Count.ToString(),
                session.ScenarioPath);
            EmptyState.Visibility = _items.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (_items.Count > 0)
                MainWindow.Instance?.DismissStatus();
        }
        catch (Exception ex)
        {
            if (version != _scanVersion)
                return;
            _items.Clear();
            EmptyState.Visibility = Visibility.Visible;
            LevelText.Text = "";
            ShowStatus(UserFacingErrors.Format(ex), InfoBarSeverity.Error);
        }
        finally
        {
            if (version == _scanVersion)
                Exit();
        }
    }

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
        CinematicList.IsEnabled = !busy;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
        => MainWindow.Instance?.Report(message, severity);
}
