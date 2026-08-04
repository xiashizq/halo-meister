using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
                    L.Get("vehicle_workshop.full_palettes_close_game"),
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
                    L.Get("vehicle_workshop.full_palettes_close_game"),
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
        string? paks = null;
        try
        {
            paks = FullPalettesOverlayService.ResolvePaksDirectory();
            installed = _mod.IsInstalled();
        }
        catch (DirectoryNotFoundException)
        {
            // Keep UI usable; install will surface the same error.
        }

        IReadOnlyList<BuiltinModFileStatus> files = _mod.GetInstalledFileStatus();
        int present = files.Count(file => file.Present);

        StatusText.Text = !bundled
            ? L.Get("vehicle_workshop.full_palettes_bundle_missing")
            : installed
                ? L.Get("builtin_mod.status_ready")
                : L.Format("builtin_mod.status_incomplete", present, files.Count);

        PaksPathText.Text = paks is null
            ? L.Get("vehicle_workshop.error_paks_not_found")
            : L.Format("builtin_mod.paks_path", paks);

        Brush presentBrush = TryThemeBrush("SystemFillColorSuccessBrush")
            ?? TryThemeBrush("AccentFillColorDefaultBrush")
            ?? new SolidColorBrush(Microsoft.UI.Colors.ForestGreen);
        Brush missingBrush = TryThemeBrush("TextFillColorSecondaryBrush")
            ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);
        FileList.ItemsSource = files
            .Select(file => new FileRow(
                file.Present ? "\uE73E" : "\uE711",
                file.Present ? presentBrush : missingBrush,
                file.Present
                    ? L.Format("builtin_mod.file_present", file.FileName)
                    : L.Format("builtin_mod.file_missing", file.FileName)))
            .ToArray();

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

    private static Brush? TryThemeBrush(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out object? value) &&
            value is Brush brush)
            return brush;
        return null;
    }

    private sealed record FileRow(string Glyph, Brush Brush, string Label);
}
