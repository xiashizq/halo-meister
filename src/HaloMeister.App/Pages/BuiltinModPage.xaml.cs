using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace HaloMeister.App.Pages;

public sealed partial class BuiltinModPage : Page, IActivatablePage
{
    private bool _busy;
    private int _refreshGeneration;
    private int _installButtonWave;
    private int _installButtonsPending;
    private TaskCompletionSource? _installButtonsReady;
    private BuiltinModListItem[] _items = [];

    public BuiltinModPage() => InitializeComponent();

    public void OnActivated() => _ = RefreshStatusAsync(showLoading: true);

    public void OnDeactivated()
    {
        _refreshGeneration++;
        LoadingRing.IsActive = false;
    }

    private void OnRefresh(object sender, RoutedEventArgs e) =>
        _ = RefreshStatusAsync(showLoading: true);

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BuiltinModListItem item })
            return;

        await RunBusy(async () =>
        {
            var service = new FullPalettesOverlayService(item.Definition);
            if (service.IsGameRunning)
            {
                ShowStatus(
                    L.Get("builtin_mod.close_game"),
                    InfoBarSeverity.Warning);
                return;
            }

            bool update = item.State is
                BuiltinModSyncState.Outdated or BuiltinModSyncState.Incomplete;
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = update
                    ? L.Get("builtin_mod.update")
                    : L.Get("builtin_mod.install"),
                Content = L.Format(
                    update
                        ? "builtin_mod.item_update_confirm"
                        : "builtin_mod.item_install_confirm",
                    item.Title),
                PrimaryButtonText = update
                    ? L.Get("builtin_mod.update")
                    : L.Get("builtin_mod.install"),
                CloseButtonText = L.Get("common.cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return;

            FullPalettesOverlayResult result = await Task.Run(service.Install);
            await RefreshStatusAsync();
            ShowStatus(result.Message, InfoBarSeverity.Success);
        });
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BuiltinModListItem item })
            return;

        await RunBusy(async () =>
        {
            var service = new FullPalettesOverlayService(item.Definition);
            if (service.IsGameRunning)
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
                Content = L.Format("builtin_mod.item_remove_confirm", item.Title),
                PrimaryButtonText = L.Get("builtin_mod.remove"),
                CloseButtonText = L.Get("common.cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return;

            FullPalettesOverlayResult result = await Task.Run(service.Remove);
            await RefreshStatusAsync();
            ShowStatus(result.Message, InfoBarSeverity.Success);
        });
    }

    private async Task RefreshStatusAsync(bool showLoading = false)
    {
        int generation = ++_refreshGeneration;
        if (showLoading)
            SetPageLoading(true);
        else
            BusyRing.IsActive = true;
        RefreshButton.IsEnabled = false;
        try
        {
            IReadOnlyList<BuiltinModCatalogStatus> catalog =
                await Task.Run(FullPalettesOverlayService.GetCatalogStatuses);
            if (generation != _refreshGeneration)
                return;

            BuiltinModListItem[] next = catalog
                .Select(entry => CreateListItem(entry, _busy))
                .ToArray();
            bool changed = !next.SequenceEqual(_items);
            _items = next;
            if (changed)
            {
                if (showLoading)
                    ArmInstallButtonGate(_items.Length);
                ModList.ItemsSource = _items;
            }

            BuiltinModCatalogStatus? prompt = catalog.FirstOrDefault(entry =>
                entry.Sync.NeedsUpdatePrompt);
            if (prompt is not null)
            {
                ShowStatus(prompt.Sync.Message, InfoBarSeverity.Warning);
            }
            else if (catalog.Any(entry =>
                entry.Sync.State == BuiltinModSyncState.BundleTampered))
            {
                BuiltinModCatalogStatus tampered = catalog.First(entry =>
                    entry.Sync.State == BuiltinModSyncState.BundleTampered);
                ShowStatus(tampered.Sync.Message, InfoBarSeverity.Error);
            }

            if (showLoading && changed)
                await WaitForInstallButtonsAsync();
        }
        catch (Exception ex)
        {
            if (generation != _refreshGeneration)
                return;

            ShowStatus(ex.Message, InfoBarSeverity.Error);
            _items = [];
            ModList.ItemsSource = _items;
        }
        finally
        {
            if (generation == _refreshGeneration)
            {
                SetPageLoading(false);
                BusyRing.IsActive = _busy;
                RefreshButton.IsEnabled = !_busy;
            }
        }
    }

    private void SetPageLoading(bool loading)
    {
        LoadingOverlay.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        LoadingRing.IsActive = loading;
        ModListHost.Opacity = loading ? 0 : 1;
    }

    private void ArmInstallButtonGate(int count)
    {
        _installButtonWave++;
        _installButtonsPending = count;
        _installButtonsReady = count > 0 ? new TaskCompletionSource() : null;
    }

    private async Task WaitForInstallButtonsAsync()
    {
        if (_installButtonsReady is null)
            return;

        Task ready = _installButtonsReady.Task;
        Task timeout = Task.Delay(400);
        await Task.WhenAny(ready, timeout);
    }

    private void OnInstallButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Control control)
            return;

        int wave = _installButtonWave;
        control.DispatcherQueue.TryEnqueue(() =>
        {
            if (control.XamlRoot is not null)
            {
                VisualStateManager.GoToState(
                    control,
                    control.IsEnabled ? "Normal" : "Disabled",
                    false);
            }

            if (wave != _installButtonWave || _installButtonsPending <= 0)
                return;

            _installButtonsPending--;
            if (_installButtonsPending == 0)
                _installButtonsReady?.TrySetResult();
        });
    }

    private static BuiltinModListItem CreateListItem(
        BuiltinModCatalogStatus entry,
        bool busy)
    {
        bool update = entry.Sync.State is
            BuiltinModSyncState.Outdated or BuiltinModSyncState.Incomplete;
        string notes = string.Join(
            Environment.NewLine,
            entry.Definition.NoteKeys.Select(L.Get));
        return new BuiltinModListItem(
            Definition: entry.Definition,
            Title: L.Get(entry.Definition.TitleKey),
            Description: L.Get(entry.Definition.DescriptionKey),
            Notes: notes,
            NotesVisibility: string.IsNullOrWhiteSpace(notes)
                ? Visibility.Collapsed
                : Visibility.Visible,
            Status: entry.Sync.Message,
            Stem: entry.Definition.Stem,
            InstallLabel: update
                ? L.Get("builtin_mod.update")
                : L.Get("builtin_mod.install"),
            CanInstall: !busy && entry.Sync.CanInstall,
            CanRemove: !busy && entry.Sync.CanRemove,
            State: entry.Sync.State);
    }

    private async Task RunBusy(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        BusyRing.IsActive = true;
        RefreshButton.IsEnabled = false;
        if (_items.Length > 0)
        {
            _items = _items
                .Select(item => item with { CanInstall = false, CanRemove = false })
                .ToArray();
            ModList.ItemsSource = _items;
        }
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
            await RefreshStatusAsync();
        }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            RefreshButton.IsEnabled = true;
            await RefreshStatusAsync();
        }
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }
}

public sealed class BuiltinModPosterConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string uri || string.IsNullOrWhiteSpace(uri))
            return null!;

        return new ImageBrush
        {
            ImageSource = new BitmapImage(new Uri(uri)),
            Stretch = Stretch.UniformToFill,
            AlignmentY = AlignmentY.Center,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed record BuiltinModListItem(
    BuiltinModDefinition Definition,
    string Title,
    string Description,
    string Notes,
    Visibility NotesVisibility,
    string Status,
    string Stem,
    string InstallLabel,
    bool CanInstall,
    bool CanRemove,
    BuiltinModSyncState State);
