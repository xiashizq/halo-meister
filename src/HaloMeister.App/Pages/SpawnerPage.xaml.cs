using HaloMeister.App.Localization;
using HaloMeister.App.Models;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace HaloMeister.App.Pages;

public sealed partial class SpawnerPage : Page
{
    private readonly RuntimeTagMemoryService _game = RuntimeTagMemoryService.Current;
    private readonly EnemySpawnerService _spawner = new();
    private readonly VehicleWorkshopService _vehicleSpawner = new();
    private IReadOnlyList<EnemySpawnChoice> _characters = [];
    private IReadOnlyList<ArmorSpawnChoice> _armor = [];
    private IReadOnlyList<LoadableVehicle> _vehicles = [];
    private string _armorStatus = L.Get("spawner.scan_armor_default");
    private EnemySpawnChoice? _selectedCharacter;
    private ArmorSpawnChoice? _selectedArmor;
    private LoadableVehicle? _selectedVehicle;
    private SpawnVariantChoice? _selectedVariant;
    private readonly List<TeamCompositionItem> _teamComposition = [];
    private bool _updatingFilters;
    private bool _busy;

    public SpawnerPage()
    {
        InitializeComponent();
        SpawnTypePicker.SelectedIndex = 0;
        _game.ConnectionChanged += OnGameConnectionChanged;
        Unloaded += OnUnloaded;
        UpdateConnectionButton();
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        await RunBusy(async () =>
        {
            SpawnerCatalog catalog = await Task.Run(_spawner.Connect);
            _characters = catalog.Characters;
            _armor = catalog.Armor;
            _armorStatus = catalog.ArmorStatus;
            try
            {
                _vehicles = await Task.Run(_vehicleSpawner.Refresh);
            }
            catch (InvalidDataException)
            {
                _vehicles = [];
            }
            await _spawner.WarmUpAsync();

            SearchBox.IsEnabled = true;
            SpawnTypePicker.IsEnabled = true;
            CategoryPicker.IsEnabled = true;
            ResetSelection();
            UpdateCategories();
            ApplyFilter();
            UpdateBridgeStatus();
            ShowStatus(
                L.Format(
                    "spawner.loaded_catalog",
                    _characters.Count,
                    _armor.Sum(item => item.Variants.Count),
                    _vehicles.Count),
                InfoBarSeverity.Success);
        });
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnSpawnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryPicker is null) return;
        ResetSelection();
        UpdateCategories();
        ApplyFilter();
    }

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingFilters) ApplyFilter();
    }

    private void UpdateCategories()
    {
        if (CategoryPicker is null) return;
        IEnumerable<string> categories = CurrentMode switch
        {
            SpawnMode.Vehicle => _vehicles.Select(item => item.Category),
            SpawnMode.Armor => _armor.Select(item => item.Category),
            _ => _characters.Select(item => item.Category),
        };
        FilterOption[] options =
        [
            new(L.Get("spawner.filter_all"), L.Get("spawner.all_categories")),
            .. categories
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(value => new FilterOption(value, value)),
        ];

        _updatingFilters = true;
        CategoryPicker.ItemsSource = options;
        CategoryPicker.SelectedIndex = 0;
        _updatingFilters = false;
    }

    private void ApplyFilter()
    {
        if (SearchBox is null || SpawnList is null) return;
        string query = SearchBox.Text.Trim();
        string category = (CategoryPicker.SelectedItem as FilterOption)?.Value ?? L.Get("spawner.filter_all");
        bool MatchesCategory(string value) =>
            string.Equals(category, L.Get("spawner.filter_all"), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, category, StringComparison.OrdinalIgnoreCase);
        bool MatchesSearch(string value) =>
            query.Length == 0 ||
            value.Contains(query, StringComparison.OrdinalIgnoreCase);

        int total;
        Array filtered;
        switch (CurrentMode)
        {
            case SpawnMode.Vehicle:
                total = _vehicles.Count;
                filtered = _vehicles
                    .Where(item =>
                        MatchesCategory(item.Category) &&
                        MatchesSearch(item.SearchText))
                    .Take(1000)
                    .ToArray();
                CatalogTitle.Text = L.Get("spawner.vehicles_title");
                CatalogSubtitle.Text = L.Get("spawner.catalog_vehicles_subtitle");
                break;
            case SpawnMode.Armor:
                total = _armor.Count;
                filtered = _armor
                    .Where(item =>
                        MatchesCategory(item.Category) &&
                        MatchesSearch(item.SearchText))
                    .ToArray();
                CatalogTitle.Text = L.Get("spawner.catalog_armor_title");
                CatalogSubtitle.Text = _armor.Count > 0
                    ? L.Get("spawner.catalog_armor_subtitle")
                    : L.Get("spawner.catalog_no_armor");
                break;
            default:
                total = _characters.Count;
                filtered = _characters
                    .Where(item =>
                        MatchesCategory(item.Category) &&
                        MatchesSearch(item.SearchText))
                    .Take(1000)
                    .ToArray();
                CatalogTitle.Text = L.Get("spawner.characters");
                CatalogSubtitle.Text = L.Get("spawner.catalog_characters_subtitle");
                break;
        }

        SpawnList.ItemsSource = filtered;
        CountText.Text = $"{filtered.Length:N0} / {total:N0}";
        EmptyCatalogText.Text = total == 0
            ? CurrentMode == SpawnMode.Armor
                ? _armorStatus
                : L.Get("spawner.empty_no_loaded")
            : L.Get("spawner.empty_no_match");
        EmptyCatalogText.Visibility =
            filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSpawnItemClicked(object sender, ItemClickEventArgs e)
    {
        ResetSelection(clearListSelection: false);
        switch (e.ClickedItem)
        {
            case EnemySpawnChoice character:
                _selectedCharacter = character;
                ShowSelection(
                    character.DisplayName,
                    character.Category,
                    character.TagPath,
                    character.Variants,
                    showPreview: false);
                break;
            case ArmorSpawnChoice armor:
                _selectedArmor = armor;
                ShowSelection(
                    armor.DisplayName,
                    armor.Category,
                    armor.TagPath,
                    armor.Variants,
                    showPreview: true);
                break;
            case LoadableVehicle vehicle:
                _selectedVehicle = vehicle;
                ShowSelection(
                    vehicle.DisplayName,
                    vehicle.Category,
                    vehicle.TagPath,
                    _vehicleSpawner.ReadSpawnVariants(vehicle),
                    showPreview: false);
                break;
        }
        UpdateSpawnButton();
    }

    private void ShowSelection(
        string name,
        string category,
        string tagPath,
        IReadOnlyList<SpawnVariantChoice> variants,
        bool showPreview)
    {
        EmptySelectionPanel.Visibility = Visibility.Collapsed;
        SelectionPanel.Visibility = Visibility.Visible;
        SelectedNameText.Text = name;
        SelectedCategoryText.Text = category;
        SelectedTagText.Text = tagPath;
        VariantPanel.Visibility =
            variants.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        VariantPicker.ItemsSource = variants;
        VariantPicker.SelectedIndex = variants.Count > 0 ? 0 : -1;
        ArmorPreviewBorder.Visibility =
            showPreview ? Visibility.Visible : Visibility.Collapsed;
        UpdateVariantDetails();
    }

    private void OnVariantChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedVariant = VariantPicker.SelectedItem as SpawnVariantChoice;
        UpdateVariantDetails();
        UpdateSpawnButton();
    }

    private void UpdateVariantDetails()
    {
        _selectedVariant = VariantPicker.SelectedItem as SpawnVariantChoice;
        VariantDetailText.Text = _selectedVariant?.Detail ?? string.Empty;
        if (_selectedArmor is not null &&
            _selectedVariant?.ImageUri is string uri)
        {
            ArmorPreview.Source = new BitmapImage(new Uri(uri));
        }
        else
        {
            ArmorPreview.Source = null;
        }
    }

    private async void OnSpawn(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await RunBusy(async () =>
        {
            ScriptExecutionResult result;
            string successPrefix;
            switch (CurrentMode)
            {
                case SpawnMode.Vehicle:
                    LoadableVehicle vehicle = _selectedVehicle
                        ?? throw new InvalidOperationException(L.Get("spawner.select_vehicle"));
                    result = await _vehicleSpawner.SpawnAsync(vehicle, _selectedVariant);
                    successPrefix = string.Empty;
                    break;
                case SpawnMode.Armor:
                    ArmorSpawnChoice armor = _selectedArmor
                        ?? throw new InvalidOperationException(L.Get("spawner.select_armor"));
                    SpawnVariantChoice armorVariant = _selectedVariant
                        ?? throw new InvalidOperationException(L.Get("spawner.select_armor_variant"));
                    result = await _spawner.SpawnArmorAsync(armor, armorVariant);
                    successPrefix = L.Format("spawner.created_armor", armorVariant.Name, string.Empty);
                    break;
                default:
                    EnemySpawnChoice character = _selectedCharacter
                        ?? throw new InvalidOperationException(L.Get("spawner.select_character"));
                    SpawnVariantChoice characterVariant = _selectedVariant
                        ?? throw new InvalidOperationException(L.Get("spawner.select_character_variant"));
                    result = await _spawner.SpawnBodyAsync(character, characterVariant);
                    successPrefix = L.Format(
                        "spawner.character_spawn_success",
                        character.DisplayName,
                        characterVariant.Name,
                        string.Empty);
                    break;
            }
            ShowStatus(successPrefix + result.Message, InfoBarSeverity.Success);
        });
    }

    private async void OnSpawnTeam(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await RunBusy(async () =>
        {
            EnemySpawnChoice character = _selectedCharacter
                ?? throw new InvalidOperationException(L.Get("spawner.select_character"));
            SpawnVariantChoice variant = _selectedVariant
                ?? throw new InvalidOperationException(L.Get("spawner.select_character_variant"));
            ScriptExecutionResult result =
                await _spawner.SpawnTeamAsync(character, variant);
            ShowStatus(
                L.Format(
                    "spawner.spawn_team_success",
                    character.DisplayName,
                    variant.Name,
                    result.Message),
                InfoBarSeverity.Success);
        });
    }

    private async void OnSpawnArmorAi(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await RunBusy(async () =>
        {
            ArmorSpawnChoice armor = _selectedArmor
                ?? throw new InvalidOperationException(L.Get("spawner.select_armor"));
            SpawnVariantChoice variant = _selectedVariant
                ?? throw new InvalidOperationException(L.Get("spawner.select_armor_variant"));
            ScriptExecutionResult result =
                await _spawner.SpawnArmorWithJohnsonAiAsync(
                    armor,
                    variant,
                    1);
            ShowStatus(
                L.Format("spawner.armor_spawn_success", variant.Name, result.Message),
                InfoBarSeverity.Success);
        });
    }

    private void OnAddToTeam(object sender, RoutedEventArgs e)
    {
        if (_selectedVariant is null)
            return;
        int quantity = double.IsFinite(TeamQuantityBox.Value)
            ? Math.Clamp((int)Math.Round(TeamQuantityBox.Value), 1, 50)
            : 1;
        TeamCompositionItem item = CurrentMode switch
        {
            SpawnMode.Character when _selectedCharacter is not null =>
                new TeamCompositionItem(
                    _selectedCharacter,
                    null,
                    _selectedVariant,
                    quantity,
                    FriendlyCompanionBox.IsChecked == true,
                    RandomizeVariantsBox.IsChecked == true,
                    RandomizeWeaponsBox.IsChecked == true),
            SpawnMode.Armor when _selectedArmor is not null =>
                new TeamCompositionItem(
                    null,
                    _selectedArmor,
                    _selectedVariant,
                    quantity,
                    FriendlyCompanionBox.IsChecked == true,
                    RandomizeVariantsBox.IsChecked == true,
                    RandomizeWeaponsBox.IsChecked == true),
            _ => throw new InvalidOperationException(L.Get("spawner.mixed_team_support")),
        };
        int existingIndex = _teamComposition.FindIndex(existing =>
            existing.Identity == item.Identity);
        if (existingIndex >= 0)
        {
            TeamCompositionItem existing = _teamComposition[existingIndex];
            _teamComposition[existingIndex] = existing with
            {
                Quantity = Math.Min(50, existing.Quantity + quantity),
            };
        }
        else
        {
            _teamComposition.Add(item);
        }
        RefreshTeamComposition();
    }

    private void OnRemoveTeamItem(object sender, RoutedEventArgs e)
    {
        if (TeamCompositionList.SelectedItem is TeamCompositionItem selected)
            _teamComposition.RemoveAll(item => item.Identity == selected.Identity);
        RefreshTeamComposition();
    }

    private void OnClearTeam(object sender, RoutedEventArgs e)
    {
        _teamComposition.Clear();
        RefreshTeamComposition();
    }

    private void OnTeamCompositionSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
        => UpdateSpawnButton();

    private async void OnSpawnComposition(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await RunBusy(async () =>
        {
            if (_teamComposition.Count == 0)
                throw new InvalidOperationException(L.Get("spawner.no_team_selections"));
            if (_teamComposition.Any(item => item.FriendlyCompanion) &&
                (!_spawner.BridgeStatus.IsRuntimeReady ||
                 _spawner.BridgeStatus.RunningVersion is < 86))
                throw new InvalidOperationException(
                    L.Get("spawner.friendly_companion_requires_v86"));

            int created = 0;
            int batchIndex = 0;
            foreach (TeamCompositionItem item in _teamComposition)
            {
                IReadOnlyList<EnemySpawnChoice> familyCharacters =
                    item.Character is not null && item.RandomizeVariants
                        ? await Task.Run(() =>
                            _spawner.GetCharacterFamilyVariants(item.Character))
                        : item.Character is not null
                            ? [item.Character]
                            : [];
                IReadOnlyList<AiWeaponChoice> johnsonWeapons =
                    item.Armor is not null && item.RandomizeWeapons
                        ? await Task.Run(_spawner.GetJohnsonCompatibleWeapons)
                        : [];
                var weaponCache =
                    new Dictionary<int, IReadOnlyList<AiWeaponChoice>>();
                var generatedAssignments = new List<TeamAssignment>(
                    item.Quantity);
                int johnsonWeaponOffset = johnsonWeapons.Count > 0
                    ? Random.Shared.Next(johnsonWeapons.Count)
                    : 0;
                for (int index = 0; index < item.Quantity; index++)
                {
                    EnemySpawnChoice? character =
                        familyCharacters.Count > 0
                            ? familyCharacters[
                                item.RandomizeVariants
                                    ? Random.Shared.Next(familyCharacters.Count)
                                    : 0]
                            : null;
                    IReadOnlyList<SpawnVariantChoice> variants =
                        character?.Variants ??
                        item.Armor?.Variants ??
                        [item.Variant];
                    SpawnVariantChoice variant =
                        item.RandomizeVariants && variants.Count > 0
                            ? variants[Random.Shared.Next(variants.Count)]
                            : item.Variant;
                    IReadOnlyList<AiWeaponChoice> weapons = johnsonWeapons;
                    if (character is not null && item.RandomizeWeapons)
                    {
                        if (!weaponCache.TryGetValue(
                                character.CharacterTag.Index,
                                out weapons!))
                        {
                            EnemySpawnChoice selectedCharacter = character;
                            weapons = await Task.Run(
                                () => _spawner.GetCompatibleWeapons(selectedCharacter));
                            weaponCache[character.CharacterTag.Index] = weapons;
                        }
                    }
                    AiWeaponChoice? weapon =
                        item.RandomizeWeapons && weapons.Count > 0
                            ? item.Armor is not null
                                ? weapons[(johnsonWeaponOffset + index) % weapons.Count]
                                : weapons[Random.Shared.Next(weapons.Count)]
                            : null;
                    generatedAssignments.Add(
                        new TeamAssignment(character, variant, weapon));
                }
                var assignments = generatedAssignments
                    .GroupBy(assignment =>
                        $"{assignment.Character?.CharacterTag.Index ?? -1}:" +
                        $"{assignment.Variant.StringId:X8}:" +
                        $"{assignment.Weapon?.Datum.ToString("X8") ?? "default"}");
                foreach (IGrouping<string, TeamAssignment> assignmentGroup in assignments)
                {
                    TeamAssignment assignment = assignmentGroup.First();
                    int remaining = assignmentGroup.Count();
                    while (remaining > 0)
                    {
                        int batchCount = Math.Min(5, remaining);
                        (float offsetX, float offsetY) =
                            FormationOffset(batchIndex++);
                        ScriptExecutionResult result;
                        if (assignment.Character is not null)
                        {
                            result = await _spawner.SpawnGroupAsync(
                                assignment.Character,
                                assignment.Variant,
                                batchCount,
                                offsetX,
                                offsetY,
                                assignment.Weapon,
                                item.FriendlyCompanion);
                        }
                        else
                        {
                            result = await _spawner.SpawnArmorWithJohnsonAiAsync(
                                item.Armor
                                    ?? throw new InvalidOperationException(
                                        L.Get("spawner.queued_armor_unavailable")),
                                assignment.Variant,
                                batchCount,
                                offsetX,
                                offsetY,
                                assignment.Weapon,
                                item.FriendlyCompanion);
                        }
                        if (result.Outcome == ScriptOutcome.Failed)
                        {
                            throw new InvalidOperationException(
                                L.Format("spawner.batch_failed", created, result.Message));
                        }
                        created += batchCount;
                        remaining -= batchCount;
                    }
                }
            }
            ShowStatus(
                L.Format("spawner.created_mixed_team", created),
                InfoBarSeverity.Success);
        });
    }

    private static (float X, float Y) FormationOffset(int batchIndex) =>
        batchIndex switch
        {
            0 => (0, 0),
            _ when batchIndex % 2 == 1 =>
                (-3.2f * ((batchIndex + 1) / 2), -2.0f),
            _ => (3.2f * (batchIndex / 2), -2.0f),
        };

    private void RefreshTeamComposition()
    {
        TeamCompositionList.ItemsSource = null;
        TeamCompositionList.ItemsSource = _teamComposition.ToArray();
        EmptyTeamText.Visibility =
            _teamComposition.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        UpdateSpawnButton();
    }

    private async Task RunBusy(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        ScanButton.IsEnabled = false;
        SpawnButton.IsEnabled = false;
        SpawnTeamButton.IsEnabled = false;
        SpawnArmorAiButton.IsEnabled = false;
        AddToTeamButton.IsEnabled = false;
        SpawnCompositionButton.IsEnabled = false;
        RemoveTeamItemButton.IsEnabled = false;
        ClearTeamButton.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _busy = false;
            UpdateConnectionButton();
            UpdateSpawnButton();
        }
    }

    private void ResetSelection(bool clearListSelection = true)
    {
        _selectedCharacter = null;
        _selectedArmor = null;
        _selectedVehicle = null;
        _selectedVariant = null;
        if (clearListSelection && SpawnList is not null)
            SpawnList.SelectedItem = null;
        if (EmptySelectionPanel is not null)
            EmptySelectionPanel.Visibility = Visibility.Visible;
        if (SelectionPanel is not null)
            SelectionPanel.Visibility = Visibility.Collapsed;
        if (VariantPicker is not null)
            VariantPicker.ItemsSource = null;
        if (ArmorPreview is not null)
            ArmorPreview.Source = null;
        UpdateSpawnButton();
    }

    private void OnGameConnectionChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(UpdateConnectionButton);

    private void UpdateConnectionButton()
        => ScanButton.IsEnabled = !_busy && _game.IsConnected;

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _game.ConnectionChanged -= OnGameConnectionChanged;
        _spawner.Dispose();
        _vehicleSpawner.Dispose();
    }

    private void UpdateBridgeStatus()
    {
        BridgeStatusText.Text = _spawner.BridgeStatus.Summary;
        UpdateSpawnButton();
    }

    private void UpdateSpawnButton()
    {
        if (SpawnButton is null) return;
        ScriptingBridgeStatus status = _spawner.BridgeStatus;
        bool hasSelection = CurrentMode switch
        {
            SpawnMode.Vehicle => _selectedVehicle is not null,
            SpawnMode.Armor => _selectedArmor is not null && _selectedVariant is not null,
            _ => _selectedCharacter is not null && _selectedVariant is not null,
        };
        SpawnButton.IsEnabled =
            !_busy &&
            _game.IsConnected &&
            hasSelection &&
            status.IsRuntimeReady &&
            !status.IsStale;
        if (SpawnTeamButton is not null)
        {
            // Bridge v67 asks the engine to build an authored starting-location
            // record before creating the selected character through actor_new.
            // directly. Older bridges can report ai_place success while
            // creating the authored actor somewhere else.
            bool supportsSerializedAiTeam =
                status.IsRuntimeReady &&
                status.RunningVersion is >= 67;
            SpawnTeamButton.IsEnabled =
                !_busy &&
                _game.IsConnected &&
                CurrentMode == SpawnMode.Character &&
                _selectedCharacter is not null &&
                _selectedVariant is not null &&
                supportsSerializedAiTeam;
        }
        if (SpawnArmorAiButton is not null)
        {
            bool supportsAiComposition =
                status.IsRuntimeReady &&
                status.RunningVersion is >= 82;
            SpawnArmorAiButton.Visibility =
                CurrentMode == SpawnMode.Armor
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            SpawnArmorAiButton.IsEnabled =
                !_busy &&
                _game.IsConnected &&
                CurrentMode == SpawnMode.Armor &&
                _selectedArmor is not null &&
                _selectedVariant is not null &&
                supportsAiComposition;
            AddToTeamButton.IsEnabled =
                !_busy &&
                _game.IsConnected &&
                CurrentMode is SpawnMode.Character or SpawnMode.Armor &&
                _selectedVariant is not null &&
                supportsAiComposition;
            SpawnCompositionButton.IsEnabled =
                !_busy &&
                _game.IsConnected &&
                _teamComposition.Count > 0 &&
                supportsAiComposition;
            RemoveTeamItemButton.IsEnabled =
                !_busy &&
                TeamCompositionList.SelectedItem is TeamCompositionItem;
            ClearTeamButton.IsEnabled =
                !_busy &&
                _teamComposition.Count > 0;
        }
    }

    private SpawnMode CurrentMode =>
        ((SpawnTypePicker?.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "vehicle" => SpawnMode.Vehicle,
            "armor" => SpawnMode.Armor,
            _ => SpawnMode.Character,
        };

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private sealed record FilterOption(string Value, string Label);

    private sealed record TeamCompositionItem(
        EnemySpawnChoice? Character,
        ArmorSpawnChoice? Armor,
        SpawnVariantChoice Variant,
        int Quantity,
        bool FriendlyCompanion,
        bool RandomizeVariants,
        bool RandomizeWeapons)
    {
        public string Identity =>
            (Character is not null
                ? $"character:{Character.CharacterTag.Index}:{Variant.StringId}"
                : $"armor:{Armor!.BipedTag.Index}:{Variant.StringId}") +
            $":friendly={FriendlyCompanion}:rv={RandomizeVariants}:" +
            $"rw={RandomizeWeapons}";
        public string DisplayName =>
            Character?.DisplayName ?? L.Format("spawner.spartan_suffix", Variant.Name);
        public string Detail =>
            string.Join(
                " · ",
                Character is not null
                    ? L.Format("spawner.character_ai_detail", Variant.Name)
                    : L.Format("spawner.armor_ai_detail", Variant.Name),
                FriendlyCompanion
                    ? L.Get("spawner.friendly_companion_label")
                    : L.Get("spawner.hostile"),
                RandomizeVariants
                    ? L.Get("spawner.random_variants")
                    : L.Get("spawner.fixed_variant"),
                RandomizeWeapons
                    ? L.Get("spawner.random_mission_weapons")
                    : L.Get("spawner.default_weapon"));
        public string QuantityLabel => $"×{Quantity:N0}";
    }

    private sealed record TeamAssignment(
        EnemySpawnChoice? Character,
        SpawnVariantChoice Variant,
        AiWeaponChoice? Weapon);

    private enum SpawnMode
    {
        Character,
        Armor,
        Vehicle,
    }
}
