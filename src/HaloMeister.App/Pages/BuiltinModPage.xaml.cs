using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace HaloMeister.App.Pages;

public sealed partial class BuiltinModPage : Page
{
    private readonly FullPalettesOverlayService _mod = new();
    private bool _busy;

    public BuiltinModPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        RefreshStatus();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => RefreshStatus();

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            if (_mod.IsGameRunning)
            {
                ShowStatus(
                    L.Get("builtin_mod.close_game"),
                    InfoBarSeverity.Warning);
                return;
            }

            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = L.Get("builtin_mod.install"),
                Content = L.Get("builtin_mod.install_confirm"),
                PrimaryButtonText = L.Get("builtin_mod.install"),
                CloseButtonText = L.Get("common.cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return;

            FullPalettesOverlayResult result = await Task.Run(_mod.Install);
            RefreshStatus();
            ShowStatus(result.Message, InfoBarSeverity.Success);
        });
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            if (_mod.IsGameRunning)
            {
                ShowStatus(
                    L.Get("builtin_mod.close_game"),
                    InfoBarSeverity.Warning);
                return;
            }

            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = L.Get("builtin_mod.remove"),
                Content = L.Get("builtin_mod.remove_confirm"),
                PrimaryButtonText = L.Get("builtin_mod.remove"),
                CloseButtonText = L.Get("common.cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return;

            FullPalettesOverlayResult result = await Task.Run(_mod.Remove);
            RefreshStatus();
            ShowStatus(result.Message, InfoBarSeverity.Success);
        });
    }

    private void RefreshStatus()
    {
        bool bundled = _mod.IsBundledAvailable();
        bool installed = false;
        int present = 0;
        int total = FullPalettesOverlayService.RequiredFileNames.Count;
        try
        {
            installed = _mod.IsInstalled();
            present = _mod.GetInstalledFileStatus().Count(file => file.Present);
        }
        catch (DirectoryNotFoundException)
        {
            // Keep UI usable; install will surface the same error.
        }

        StatusText.Text = !bundled
            ? L.Get("builtin_mod.bundle_missing")
            : installed
                ? L.Get("builtin_mod.status_ready")
                : present == 0
                    ? L.Get("builtin_mod.status_not_installed")
                    : L.Format("builtin_mod.status_incomplete", present, total);

        InstallButton.IsEnabled = !_busy && bundled && !installed;
        RemoveButton.IsEnabled = !_busy && present > 0;
        RefreshButton.IsEnabled = !_busy;
        RestartBanner.IsOpen = true;
    }

    private async Task RunBusy(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        BusyRing.IsActive = true;
        InstallButton.IsEnabled = false;
        RemoveButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
            RefreshStatus();
        }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            RefreshStatus();
        }
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }
}
