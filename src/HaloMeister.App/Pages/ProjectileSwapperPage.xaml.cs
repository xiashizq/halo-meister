using HaloMeister.App.Localization;
using HaloMeister.App.Models;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class ProjectileSwapperPage : Page, IActivatablePage
{
    private readonly RuntimeTagMemoryService _game = RuntimeTagMemoryService.Current;
    private readonly ProjectileSwapperService _swapper = new();
    private ProjectileSwapperSession? _session;
    private ProjectileSwapWeapon? _selectedWeapon;
    private RuntimeTagEntry? _selectedProjectile;
    private bool _busy;

    public ProjectileSwapperPage()
    {
        InitializeComponent();
        _game.ConnectionChanged += OnGameConnectionChanged;
        UpdateConnectionButtons();
    }

    public void OnActivated() => UpdateConnectionButtons();

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            ProjectileSwapperSession session = await Task.Run(_swapper.Connect);
            ShowSession(session);
            ShowStatus(
                L.Format(
                    "projectile_swapper.found_weapons_projectiles",
                    session.Weapons.Count,
                    session.Projectiles.Count),
                InfoBarSeverity.Success);
        });
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        int selectedIndex = _selectedWeapon?.Tag.Index ?? -1;
        await RunBusy(async () =>
        {
            ProjectileSwapperSession session = await Task.Run(_swapper.Refresh);
            ShowSession(session, selectedIndex);
            ShowStatus(
                L.Get("projectile_swapper.refreshed_weapons_projectiles"),
                InfoBarSeverity.Success);
        });
    }

    private void ShowSession(ProjectileSwapperSession session, int selectedIndex = -1)
    {
        _session = session;
        _selectedWeapon = session.Weapons.FirstOrDefault(item => item.Tag.Index == selectedIndex);
        WeaponSearchBox.IsEnabled = true;
        ApplyWeaponFilter();
        ConnectionText.Text = L.Format(
            "projectile_swapper.scanned_summary",
            session.Weapons.Count,
            session.Projectiles.Count);
        RefreshButton.IsEnabled = true;

        _selectedProjectile = null;
        ProjectilePicker.Text = "";
        ShowSelection();
    }

    private void OnWeaponSearchChanged(object sender, TextChangedEventArgs e)
        => ApplyWeaponFilter();

    private void ApplyWeaponFilter()
    {
        if (_session is null)
        {
            WeaponList.ItemsSource = null;
            WeaponCountText.Text = "";
            return;
        }

        string query = WeaponSearchBox.Text.Trim();
        ProjectileSwapWeapon[] filtered = _session.Weapons
            .Where(weapon =>
                query.Length == 0 ||
                weapon.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                weapon.Tag.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        WeaponList.ItemsSource = filtered;
        WeaponCountText.Text = L.Format(
            "projectile_swapper.weapons_shown_count",
            filtered.Length,
            _session.Weapons.Count);
        WeaponList.SelectedItem = _selectedWeapon;
    }

    private void OnWeaponClicked(object sender, ItemClickEventArgs e)
    {
        _selectedWeapon = e.ClickedItem as ProjectileSwapWeapon;
        _selectedProjectile = null;
        ProjectilePicker.Text = "";
        ShowSelection();
    }

    private void ShowSelection()
    {
        SelectedWeaponText.Text = _selectedWeapon?.Name ?? L.Get("projectile_swapper.select_a_weapon");
        CurrentProjectileText.Text = _selectedWeapon?.CurrentProjectileText ?? "";
        ProjectilePicker.IsEnabled = _selectedWeapon is not null && !_busy;
        UpdateSwapButton();
    }

    private void OnProjectileTextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput ||
            _session is null)
            return;

        string query = sender.Text.Trim();
        RuntimeTagEntry[] results = _session.Projectiles
            .Where(projectile =>
                query.Length == 0 ||
                projectile.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                projectile.LeafName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                ProjectileSwapperService.FriendlyName(projectile)
                    .Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        sender.ItemsSource = results;
        _selectedProjectile = results.FirstOrDefault(projectile =>
            IsExactProjectileText(projectile, query));
        UpdateSwapButton();
    }

    private void OnProjectilePickerGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not AutoSuggestBox picker || _session is null) return;
        if (picker.Text.Trim().Length == 0)
            picker.ItemsSource = _session.Projectiles;
        picker.IsSuggestionListOpen = true;
    }

    private void OnProjectileSuggestionChosen(
        AutoSuggestBox sender,
        AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        _selectedProjectile = args.SelectedItem as RuntimeTagEntry;
        sender.Text = _selectedProjectile?.DisplayName ?? "";
        UpdateSwapButton();
    }

    private async void OnSwap(object sender, RoutedEventArgs e)
    {
        if (_selectedWeapon is not { } weapon ||
            _selectedProjectile is not { } projectile)
            return;

        await RunBusy(async () =>
        {
            await Task.Run(() => _swapper.Swap(weapon, projectile));
            ProjectileSwapperSession session = await Task.Run(_swapper.Refresh);
            ShowSession(session, weapon.Tag.Index);
            ShowStatus(
                L.Format(
                    "projectile_swapper.weapon_now_fires",
                    weapon.Name,
                    ProjectileSwapperService.FriendlyName(projectile)),
                InfoBarSeverity.Success);
        });
    }

    private async Task RunBusy(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        BusyRing.IsActive = true;
        ScanButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        ProjectilePicker.IsEnabled = false;
        SwapButton.IsEnabled = false;
        try { await action(); }
        catch (Exception ex) { ShowStatus(ex.Message, InfoBarSeverity.Error); }
        finally
        {
            _busy = false;
            BusyRing.IsActive = false;
            UpdateConnectionButtons();
            ProjectilePicker.IsEnabled = _selectedWeapon is not null;
            UpdateSwapButton();
        }
    }

    private void OnGameConnectionChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(UpdateConnectionButtons);

    private void UpdateConnectionButtons()
    {
        ScanButton.IsEnabled = !_busy && _game.IsConnected;
        RefreshButton.IsEnabled = !_busy && _game.IsConnected && _session is not null;
        UpdateSwapButton();
    }

    private void UpdateSwapButton()
    {
        SwapButton.IsEnabled =
            !_busy &&
            _selectedWeapon is not null &&
            _selectedProjectile is not null &&
            _swapper.ProcessId != 0;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private static bool IsExactProjectileText(RuntimeTagEntry projectile, string text) =>
        projectile.Name.Equals(text, StringComparison.OrdinalIgnoreCase) ||
        projectile.DisplayName.Equals(text, StringComparison.OrdinalIgnoreCase) ||
        ProjectileSwapperService.FriendlyName(projectile)
            .Equals(text, StringComparison.OrdinalIgnoreCase);
}
