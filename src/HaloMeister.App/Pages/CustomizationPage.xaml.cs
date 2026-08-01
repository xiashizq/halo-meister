using HaloMeister.App.Localization;
using HaloMeister.App.Models;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace HaloMeister.App.Pages;

public sealed partial class CustomizationPage : Page
{
    private readonly AppState _state = AppState.Current;
    private readonly CustomizationStore _store = new();
    private readonly RuntimeTagMemoryService _game = RuntimeTagMemoryService.Current;
    private readonly RuntimeCustomizationVariantService _runtimeArmor = new();
    private readonly RuntimeCustomizationPreferenceStore _runtimePreferences = new();
    private readonly List<CustomizationSlot> _slots = [];
    private readonly List<string> _unmatchedTags = [];
    private readonly HashSet<string> _configuredRuntimeSlots =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _autoApplyTimer = new()
    {
        Interval = TimeSpan.FromSeconds(10),
    };
    private bool _updatingSelection;
    private bool _loadingProfiles;
    private bool _autoApplyBusy;
    private bool _initialized;
    private int _retailUnavailableChoiceCount;
    private int _retailBlockedConfiguredCount;
    private string _profileId = "invalid_id";

    public CustomizationPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _autoApplyTimer.Tick += OnAutoApplyTick;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            _initialized = true;
            LoadProfiles();
        }
        _autoApplyTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        _autoApplyTimer.Stop();

    private void LoadProfiles()
    {
        _loadingProfiles = true;
        IReadOnlyList<CustomizationProfile> profiles = _store.GetProfiles();
        ProfilePicker.ItemsSource = profiles;
        ProfilePicker.SelectedIndex = profiles.Count > 0 ? 0 : -1;
        _loadingProfiles = false;
        Reload();
    }

    private void Reload()
    {
        try
        {
            LoadingRing.IsActive = true;
            CustomizationProfile? profile =
                ProfilePicker.SelectedItem as CustomizationProfile;
            string? selectedPath = profile?.ConfigPath;
            _profileId = profile?.Id ?? "invalid_id";
            IReadOnlyList<string> selectedTags = _store.Load(selectedPath);
            IReadOnlyDictionary<string, string?> runtimeSelections =
                _runtimePreferences.Load(_profileId);
            _slots.Clear();
            _unmatchedTags.Clear();
            _configuredRuntimeSlots.Clear();
            _retailUnavailableChoiceCount = 0;
            _retailBlockedConfiguredCount = 0;
            _configuredRuntimeSlots.UnionWith(runtimeSelections.Keys);

            foreach (CustomizationCategory category in CustomizationCatalog.Categories)
            {
                string? selectedTag = selectedTags.FirstOrDefault(tag =>
                    CustomizationCatalog.TryGetSlotSegment(tag, out string segment) &&
                    segment.Equals(category.TagSegment, StringComparison.OrdinalIgnoreCase));
                List<CosmeticChoice> choices = GetAvailableChoices(category);
                CosmeticChoice? selected = null;
                if (runtimeSelections.TryGetValue(
                        category.TagSegment,
                        out string? runtimePreference))
                {
                    selected = choices.FirstOrDefault(choice =>
                        string.Equals(
                            choice.PreferenceValue,
                            runtimePreference,
                            StringComparison.OrdinalIgnoreCase));
                }
                selected ??= choices.FirstOrDefault(choice =>
                    string.Equals(
                        choice.Tag,
                        selectedTag,
                        StringComparison.OrdinalIgnoreCase));

                if (selectedTag is not null && selected is null &&
                    !BuildPolicy.EnforceCustomizationOwnership)
                {
                    selected = new CosmeticChoice(
                        L.Format("customization.custom_future_item", selectedTag.Split('.').Last()),
                        selectedTag,
                        L.Get("customization.preserved_from_config"));
                    choices.Add(selected);
                }
                else if (selectedTag is not null && selected is null)
                {
                    _retailBlockedConfiguredCount++;
                }

                _slots.Add(new CustomizationSlot(
                    category.Group,
                    category.Name,
                    category.TagSegment,
                    choices,
                    selected ?? choices[0],
                    MarkDirty));
            }

            foreach (string tag in selectedTags)
            {
                bool assigned =
                    CustomizationCatalog.TryGetSlotSegment(
                        tag,
                        out string segment) &&
                    _slots.Any(slot =>
                        slot.TagSegment.Equals(
                            segment,
                            StringComparison.OrdinalIgnoreCase));
                if (!assigned) _unmatchedTags.Add(tag);
            }

            SlotList.ItemsSource = null;
            SlotList.ItemsSource = _slots;
            SlotList.SelectedIndex = _slots.Count > 0 ? 0 : -1;
            ConfigPathText.Text = _store.ConfigPath ?? string.Empty;
            SaveButton.IsEnabled = false;
            DirtyText.Text = L.Get("customization.no_unsaved_changes");
            UpdateSummary();
            UpdateSafetyNotice();
            Report(
                _unmatchedTags.Count > 0
                    ? L.Format(
                        "customization.loaded_overrides_unrecognized",
                        _slots.Count(slot => slot.HasOverride),
                        _unmatchedTags.Count)
                    : L.Format(
                        "customization.loaded_overrides",
                        _slots.Count(slot => slot.HasOverride)) + ".",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            SlotList.ItemsSource = null;
            SaveButton.IsEnabled = false;
            Report(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            LoadingRing.IsActive = false;
            _ = ApplyConfiguredLiveAsync(silent: true);
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            CosmeticChoice? blocked = _slots
                .Select(slot => slot.Selected)
                .OfType<CosmeticChoice>()
                .FirstOrDefault(choice => !CanEquip(choice));
            if (blocked is not null)
                throw new InvalidOperationException(GetOwnershipFailure(blocked));

            string[] selected = _slots
                .Select(slot => slot.Selected?.Tag)
                .OfType<string>()
                .Concat(_unmatchedTags)
                .ToArray();

            ConfigBackup backup = await _store.SaveAsync(selected);
            SaveButton.IsEnabled = false;
            DirtyText.Text = L.Get("customization.saved");
            UpdateSummary();
            Report(
                L.Format("customization.saved_selections", backup.Path),
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Report(ex.Message, ex is InvalidOperationException
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Error);
        }
    }

    private void OnReload(object sender, RoutedEventArgs e) => Reload();

    private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingProfiles) Reload();
    }

    private void OnSlotSelected(object sender, SelectionChangedEventArgs e)
    {
        ShowSelectedSlot();
    }

    private async void OnChoiceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelection ||
            SlotList.SelectedItem is not CustomizationSlot slot ||
            DetailsChoiceCombo.SelectedItem is not CosmeticChoice choice)
            return;

        if (!CanEquip(choice))
        {
            _updatingSelection = true;
            DetailsChoiceCombo.SelectedItem = slot.Selected;
            _updatingSelection = false;
            Report(GetOwnershipFailure(choice), InfoBarSeverity.Warning);
            return;
        }

        slot.Selected = choice;
        _runtimePreferences.Set(
            _profileId,
            slot.TagSegment,
            choice.PreferenceValue);
        _configuredRuntimeSlots.Add(slot.TagSegment);
        ShowSelectedSlot();
        UpdateSummary();
        await ApplySelectionLiveAsync(slot, choice, silent: false);
    }

    private async void OnResetSlot(object sender, RoutedEventArgs e)
    {
        if (SlotList.SelectedItem is not CustomizationSlot slot) return;
        slot.Selected = slot.Choices[0];
        _runtimePreferences.Set(_profileId, slot.TagSegment, null);
        _configuredRuntimeSlots.Add(slot.TagSegment);
        ShowSelectedSlot();
        UpdateSummary();
        await ApplySelectionLiveAsync(slot, slot.Choices[0], silent: false);
    }

    private async Task ApplySelectionLiveAsync(
        CustomizationSlot slot,
        CosmeticChoice selected,
        bool silent)
    {
        if (!CanEquip(selected))
        {
            if (!silent)
                Report(GetOwnershipFailure(selected), InfoBarSeverity.Warning);
            return;
        }
        if (!_store.IsGameRunning) return;

        try
        {
            if (!_game.IsConnected)
                await Task.Run(_game.Connect);

            if (slot.TagSegment.Equals(
                    "MasterChief",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!CustomizationCatalog.TryGetMasterChiefModelVariantIndex(
                        selected,
                        out int armorVariant))
                    throw new InvalidDataException(
                        L.Get("customization.armor_no_model_variant"));
                RuntimeArmorVariantResult result =
                    await _runtimeArmor.ApplyAsync(armorVariant);
                if (!silent)
                    Report(
                        L.Format("customization.applied_armor_live", selected.Name),
                        InfoBarSeverity.Success);
                return;
            }

            if (!CustomizationCatalog.TryGetWeaponModelVariantIndex(
                    slot.TagSegment,
                    selected,
                    out int weaponVariant))
                throw new InvalidDataException(
                    L.Format("customization.weapon_no_model_variant", slot.Name));
            RuntimeWeaponVariantResult weaponResult =
                await _runtimeArmor.ApplyWeaponAsync(
                    slot.TagSegment,
                    weaponVariant);
            if (!silent)
                Report(
                    L.Format("customization.applied_weapon_live", selected.Name, slot.Name),
                    InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                bool unavailable = ex.Message.Contains(
                    "not currently",
                    StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains(
                        "not loaded",
                        StringComparison.OrdinalIgnoreCase);
                Report(
                    unavailable
                        ? L.Format(
                            "customization.saved_for_profile",
                            selected.Name,
                            _profileId,
                            slot.Name)
                        : ex.Message,
                    unavailable
                        ? InfoBarSeverity.Informational
                        : InfoBarSeverity.Error);
            }
        }
    }

    private async void OnAutoApplyTick(object? sender, object e) =>
        await ApplyConfiguredLiveAsync(silent: true);

    private async Task ApplyConfiguredLiveAsync(bool silent)
    {
        if (_autoApplyBusy || !_store.IsGameRunning) return;
        _autoApplyBusy = true;
        try
        {
            foreach (CustomizationSlot slot in _slots.Where(slot =>
                         _configuredRuntimeSlots.Contains(slot.TagSegment)))
            {
                if (slot.Selected is { } selected)
                    await ApplySelectionLiveAsync(slot, selected, silent);
            }
        }
        finally
        {
            _autoApplyBusy = false;
        }
    }

    private async void OnOpenConfigFolder(object sender, RoutedEventArgs e)
    {
        if (_store.ConfigPath is not { } path) return;
        await Launcher.LaunchFolderPathAsync(Path.GetDirectoryName(path)!);
    }

    private void MarkDirty()
    {
        bool running = _store.IsGameRunning;
        SaveButton.IsEnabled = !running;
        DirtyText.Text = running
            ? L.Get("customization.unsaved_close_game")
            : L.Get("customization.unsaved_changes");
    }

    private void ShowSelectedSlot()
    {
        if (SlotList.SelectedItem is not CustomizationSlot slot)
        {
            DetailsChoiceCombo.ItemsSource = null;
            return;
        }

        _updatingSelection = true;
        SlotTitleText.Text = slot.Name;
        SlotGroupText.Text = slot.Group;
        SelectedTagText.Text = slot.SelectedTag;
        AvailabilityText.Text = slot.SelectedAvailability;
        HeroImage.Source = slot.SelectedImageUri is { } uri
            ? new BitmapImage(new Uri(uri))
            : null;
        DetailsChoiceCombo.ItemsSource = slot.Choices;
        DetailsChoiceCombo.SelectedItem = slot.Selected;
        _updatingSelection = false;
    }

    private void UpdateSummary()
    {
        int count = _slots.Count(slot => slot.HasOverride);
        OverrideCountText.Text = L.Format("customization.local_overrides_count", count);
    }

    private void UpdateSafetyNotice()
    {
        bool running = _store.IsGameRunning;
        if (BuildPolicy.EnforceCustomizationOwnership)
        {
            SafetyNotice.Severity = InfoBarSeverity.Warning;
            SafetyNotice.Title = L.Get("customization.retail_ownership_protection");
            SafetyNotice.Message =
                L.Format("customization.retail_safety_message", _retailUnavailableChoiceCount) +
                (_retailBlockedConfiguredCount > 0
                    ? L.Format("customization.retail_safety_unverified_suffix", _retailBlockedConfiguredCount)
                    : ".") +
                (running
                    ? L.Get("customization.retail_safety_running_suffix")
                    : L.Get("customization.retail_safety_verify_suffix"));
            return;
        }

        SafetyNotice.Severity = running ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        SafetyNotice.Title = running
            ? L.Get("customization.running_notice_title")
            : L.Get("customization.ready_to_edit");
        SafetyNotice.Message = running
            ? L.Get("customization.running_notice_message")
            : L.Get("customization.ready_message");
    }

    private List<CosmeticChoice> GetAvailableChoices(CustomizationCategory category)
    {
        if (!BuildPolicy.EnforceCustomizationOwnership)
            return category.Choices.ToList();

        var available = new List<CosmeticChoice>();
        foreach (CosmeticChoice choice in category.Choices)
        {
            if (!CanEquip(choice))
            {
                _retailUnavailableChoiceCount++;
                continue;
            }

            string? entitlement = CustomizationCatalog.GetRequiredPlayFabEntitlement(choice);
            available.Add(entitlement is null
                ? choice
                : choice with
                {
                    Availability = L.Format("customization.ownership_verified_suffix", choice.Availability, entitlement),
                });
        }

        return available;
    }

    private bool CanEquip(CosmeticChoice choice) =>
        !BuildPolicy.EnforceCustomizationOwnership ||
        CustomizationCatalog.CanEquipInRetail(choice, _state.Entitlements);

    private string GetOwnershipFailure(CosmeticChoice choice)
    {
        string? required = CustomizationCatalog.GetRequiredPlayFabEntitlement(choice);
        if (required is null)
            return L.Format("customization.cannot_equip_no_entitlement", choice.Name);
        if (!_state.IsLoaded)
            return L.Format("customization.download_save_before_equip", choice.Name, required);
        return L.Format("customization.cannot_equip_not_owned", choice.Name, required);
    }

    private void Report(string message, InfoBarSeverity severity)
    {
        PageStatus.Title = severity switch
        {
            InfoBarSeverity.Error => L.Get("common.something_went_wrong"),
            InfoBarSeverity.Warning => L.Get("common.careful"),
            InfoBarSeverity.Success => L.Get("common.done"),
            _ => L.Get("common.info"),
        };
        PageStatus.Message = message;
        PageStatus.Severity = severity;
        PageStatus.IsOpen = true;
    }
}
