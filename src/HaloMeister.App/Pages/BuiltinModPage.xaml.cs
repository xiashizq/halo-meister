using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace HaloMeister.App.Pages;

public sealed partial class BuiltinModPage : Page, IActivatablePage
{
    private bool _busy;
    private int _refreshGeneration;
    private int _installButtonWave;
    private int _installButtonsPending;
    private TaskCompletionSource? _installButtonsReady;
    private BuiltinModListItem[] _coreItems = [];
    private BuiltinModListItem[] _enhanceItems = [];

    public BuiltinModPage() => InitializeComponent();

    /// <summary>Rounds the poster's left corners to match the card.</summary>
    private void OnPosterSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Image image || e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
            return;

        Visual visual = ElementCompositionPreview.GetElementVisual(image);
        RectangleClip rounded = visual.Compositor.CreateRectangleClip(
            0,
            0,
            (float)e.NewSize.Width,
            (float)e.NewSize.Height,
            new Vector2(9, 9),
            new Vector2(0, 0),
            new Vector2(0, 0),
            new Vector2(9, 9));
        visual.Clip = rounded;
    }

    private void OnModHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0)
            return;
        if (ReferenceEquals(sender, CoreHost))
            CoreModList.Width = e.NewSize.Width;
        else if (ReferenceEquals(sender, EnhanceHost))
            EnhanceModList.Width = e.NewSize.Width;
    }

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

            BuiltinModListItem[] nextCore = catalog
                .Where(entry => BuiltinModCatalog.IsCore(entry.Definition.Id))
                .Select(entry => CreateListItem(entry, _busy))
                .ToArray();
            BuiltinModListItem[] nextEnhance = catalog
                .Where(entry => !BuiltinModCatalog.IsCore(entry.Definition.Id))
                .Select(entry => CreateListItem(entry, _busy))
                .ToArray();
            bool changed = !nextCore.SequenceEqual(_coreItems) ||
                           !nextEnhance.SequenceEqual(_enhanceItems);
            _coreItems = nextCore;
            _enhanceItems = nextEnhance;
            if (changed)
            {
                if (showLoading)
                    ArmInstallButtonGate(_coreItems.Length + _enhanceItems.Length);
                BindLists();
            }

            PublishedVersionReport? published = await PublishedVersionService.Current.CheckAsync();
            BuiltinModCatalogStatus? prompt = catalog.FirstOrDefault(entry =>
                entry.Sync.NeedsUpdatePrompt);
            if (published is { HasUpdate: true } || prompt is not null)
            {
                string message = string.Join(
                    Environment.NewLine,
                    new[] { published?.HasUpdate == true ? published.Message : null, prompt?.Sync.Message }
                        .Where(line => !string.IsNullOrWhiteSpace(line)));
                ShowStatus(message, InfoBarSeverity.Warning);
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

            ShowStatus(UserFacingErrors.Format(ex), InfoBarSeverity.Error);
            _coreItems = [];
            _enhanceItems = [];
            BindLists();
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

    private void BindLists()
    {
        CoreModList.ItemsSource = _coreItems;
        EnhanceModList.ItemsSource = _enhanceItems;
    }

    private static BuiltinModListItem[] DisableActions(BuiltinModListItem[] items) =>
        items.Select(item => item with { CanInstall = false, CanRemove = false }).ToArray();

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

    private static readonly Dictionary<string, BitmapImage> PosterCache = new(StringComparer.Ordinal);

    private static BitmapImage? LoadPoster(BuiltinModDefinition definition)
    {
        if (PosterCache.TryGetValue(definition.Id, out BitmapImage? cached))
            return cached;

        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "BuiltinMods",
            $"{definition.Id}.jpg");
        if (!File.Exists(path))
            return null;

        var bitmap = new BitmapImage
        {
            CreateOptions = BitmapCreateOptions.IgnoreImageCache,
            UriSource = new Uri(path, UriKind.Absolute),
        };
        PosterCache[definition.Id] = bitmap;
        return bitmap;
    }

    // Shared brush instances keep list-item record equality stable across refreshes.
    private static readonly SolidColorBrush SuccessBrush = new(Color.FromArgb(255, 108, 203, 95));
    private static readonly SolidColorBrush CautionBrush = new(Color.FromArgb(255, 252, 225, 0));
    private static readonly SolidColorBrush CriticalBrush = new(Color.FromArgb(255, 255, 153, 164));
    private static readonly SolidColorBrush NeutralBrush = new(Color.FromArgb(255, 160, 160, 160));

    private static string StatusGlyphFor(BuiltinModSyncState state) => state switch
    {
        BuiltinModSyncState.UpToDate => "\uE73E",
        BuiltinModSyncState.Outdated or BuiltinModSyncState.Incomplete => "\uE7BA",
        BuiltinModSyncState.BundleMissing or BuiltinModSyncState.BundleTampered => "\uEA39",
        BuiltinModSyncState.GameFolderMissing => "\uE7BA",
        _ => "\uE946",
    };

    private static Brush StatusBrushFor(BuiltinModSyncState state) => state switch
    {
        BuiltinModSyncState.UpToDate => SuccessBrush,
        BuiltinModSyncState.Outdated or
            BuiltinModSyncState.Incomplete or
            BuiltinModSyncState.GameFolderMissing => CautionBrush,
        BuiltinModSyncState.BundleMissing or BuiltinModSyncState.BundleTampered => CriticalBrush,
        _ => NeutralBrush,
    };

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
            Poster: LoadPoster(entry.Definition),
            Title: L.Get(entry.Definition.TitleKey),
            Description: L.Get(entry.Definition.DescriptionKey),
            Notes: notes,
            NotesVisibility: string.IsNullOrWhiteSpace(notes)
                ? Visibility.Collapsed
                : Visibility.Visible,
            Status: entry.Sync.Message,
            Version: entry.Sync.VersionText,
            VersionVisibility: string.IsNullOrWhiteSpace(entry.Sync.VersionText)
                ? Visibility.Collapsed
                : Visibility.Visible,
            Stem: entry.Definition.Stem,
            InstallLabel: update
                ? L.Get("builtin_mod.update")
                : L.Get("builtin_mod.install"),
            StatusGlyph: StatusGlyphFor(entry.Sync.State),
            StatusBrush: StatusBrushFor(entry.Sync.State),
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
        if (_coreItems.Length + _enhanceItems.Length > 0)
        {
            _coreItems = DisableActions(_coreItems);
            _enhanceItems = DisableActions(_enhanceItems);
            BindLists();
        }
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowStatus(UserFacingErrors.Format(ex), InfoBarSeverity.Error);
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
        => MainWindow.Instance?.Report(message, severity);
}

public sealed record BuiltinModListItem(
    BuiltinModDefinition Definition,
    BitmapImage? Poster,
    string Title,
    string Description,
    string Notes,
    Visibility NotesVisibility,
    string Status,
    string Version,
    Visibility VersionVisibility,
    string Stem,
    string InstallLabel,
    string StatusGlyph,
    Brush StatusBrush,
    bool CanInstall,
    bool CanRemove,
    BuiltinModSyncState State);
