using System.ComponentModel;
using HaloMeister.App.Localization;
using HaloMeister.App.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Pages;

public sealed partial class ProfilePage : Page, IActivatablePage
{
    private readonly AppState _state = AppState.Current;
    private bool _subscribed;

    public ProfilePage()
    {
        InitializeComponent();

        FlagList.ItemsSource = _state.Flags;
        NumberList.ItemsSource = _state.Numbers;
        ApplyBuildPolicy();
    }

    private void ApplyBuildPolicy()
    {
        if (!BuildPolicy.IsRetail)
            return;

        PageDescription.Text = L.Get("profile.retail_page_description");
        EntitlementNotice.Message = L.Get("profile.retail_entitlement_notice");
        CustomEntitlementExpander.IsEnabled = false;
    }

    public void OnActivated()
    {
        if (!_subscribed)
        {
            _state.SaveLoaded += OnSaveLoaded;
            _subscribed = true;
        }

        SubscribeRows();
        RefreshEntitlements();
    }

    public void OnDeactivated()
    {
        if (!_subscribed)
            return;
        _state.SaveLoaded -= OnSaveLoaded;
        UnsubscribeRows();
        _subscribed = false;
    }

    private void OnSaveLoaded()
    {
        SubscribeRows();
        RefreshEntitlements();
    }

    private void SubscribeRows()
    {
        UnsubscribeRows();
        foreach (EntitlementRow row in _state.EntitlementRows)
            row.PropertyChanged += OnEntitlementChanged;
    }

    private void UnsubscribeRows()
    {
        foreach (EntitlementRow row in _state.EntitlementRows)
            row.PropertyChanged -= OnEntitlementChanged;
    }

    private void OnEntitlementChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EntitlementRow.IsUnlocked) or nameof(EntitlementRow.Status))
            RefreshEntitlements();
    }

    private void OnFilterChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        => RefreshEntitlements();

    private void OnStatusFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EntitlementList is not null) RefreshEntitlements();
    }

    private void RefreshEntitlements()
    {
        string query = EntitlementSearch?.Text?.Trim() ?? string.Empty;
        int status = StatusFilter?.SelectedIndex ?? 0;

        EntitlementRow[] visible = _state.EntitlementRows
            .Where(row => query.Length == 0 ||
                          row.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(row => status == 0 ||
                          status == 1 && row.IsUnlocked ||
                          status == 2 && !row.IsUnlocked)
            .ToArray();

        EntitlementList.ItemsSource = visible;
        bool canEdit = _state.IsLoaded && !BuildPolicy.IsRetail;
        UnlockAllButton.IsEnabled = canEdit;
        LockAllButton.IsEnabled = canEdit;
        NewEntitlement.IsEnabled = canEdit;
        AddEntitlementButton.IsEnabled = canEdit;

        int known = _state.EntitlementRows.Count(row => row.IsCatalogued);
        int unlocked = _state.EntitlementRows.Count(row => row.IsCatalogued && row.IsUnlocked);
        int custom = _state.EntitlementRows.Count(row => !row.IsCatalogued);
        EntitlementSummary.Text = !_state.IsLoaded
            ? L.Get("profile.load_save_data_to_check_ownership")
            : L.Format("profile.entitlements_unlocked_summary", unlocked, known) +
              (custom > 0
                  ? L.Format(
                      custom == 1
                          ? "profile.custom_value_preserved"
                          : "profile.custom_values_preserved",
                      custom)
                  : string.Empty);
    }

    private void OnUnlockAll(object sender, RoutedEventArgs e)
    {
        _state.SetAllCataloguedEntitlements(true);
        RefreshEntitlements();
    }

    private void OnLockAll(object sender, RoutedEventArgs e)
    {
        _state.SetAllCataloguedEntitlements(false);
        RefreshEntitlements();
    }

    private void OnAddEntitlement(object sender, RoutedEventArgs e)
    {
        string value = NewEntitlement.Text;
        _state.AddEntitlement(value);
        NewEntitlement.Text = string.Empty;
        SubscribeRows();
        RefreshEntitlements();
    }
}
