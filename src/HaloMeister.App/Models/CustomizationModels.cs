namespace HaloMeister.App.Models;

public sealed record CosmeticChoice(
    string Name,
    string? Tag,
    string Availability,
    string? ImageName = null,
    int? RuntimeModelVariantIndex = null,
    string? RuntimePreferenceValue = null)
{
    public bool IsDefault => Tag is null && RuntimeModelVariantIndex is null;
    public bool IsRuntimeOnly => Tag is null && RuntimeModelVariantIndex is not null;
    public string? PreferenceValue => RuntimePreferenceValue ?? Tag;
    public string? ImageUri => ImageName is null
        ? null
        : $"ms-appx:///Assets/Customization/Game/{ImageName}";
}

public sealed class CustomizationSlot : ObservableObject
{
    private CosmeticChoice? _selected;
    private readonly Action _changed;

    public CustomizationSlot(
        string group,
        string name,
        string tagSegment,
        IReadOnlyList<CosmeticChoice> choices,
        CosmeticChoice selected,
        Action changed)
    {
        Group = group;
        Name = name;
        TagSegment = tagSegment;
        Choices = choices;
        _selected = selected;
        _changed = changed;
    }

    public string Group { get; }
    public string Name { get; }
    public string TagSegment { get; }
    public IReadOnlyList<CosmeticChoice> Choices { get; }

    public CosmeticChoice? Selected
    {
        get => _selected;
        set
        {
            if (value is null || !Set(ref _selected, value)) return;
            Raise(nameof(SelectedTag));
            Raise(nameof(SelectedImageUri));
            Raise(nameof(SelectedName));
            Raise(nameof(SelectedAvailability));
            Raise(nameof(HasOverride));
            _changed();
        }
    }

    public string SelectedTag => Selected switch
    {
        { Tag: not null } choice => choice.Tag,
        { RuntimeModelVariantIndex: int index } =>
            $"Runtime model variant {index + 1:00} (no gameplay tag)",
        _ => "Game default (no override)",
    };
    public string? SelectedImageUri => Selected?.ImageUri;
    public string SelectedName => Selected?.Name ?? "Game default";
    public string SelectedAvailability => Selected?.Availability ?? string.Empty;
    public bool HasOverride => Selected is { IsDefault: false };
}

/// <summary>
/// Cosmetic gameplay tags and thumbnails extracted from the installed game.
/// Unknown tags introduced by later updates are retained by CustomizationPage.
/// </summary>
public static class CustomizationCatalog
{
    private const string Prefix = "Blam.Customization.";
    private static readonly HashSet<string> PromotionalEntitlementValues =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "blue_001",
            "orange_001",
            "purple_001",
            "red_001",
        };

    public static IReadOnlyList<CustomizationCategory> Categories { get; } =
    [
        ArmorCategory("Armor", "Master Chief", "MasterChief", "T_UI_MasterChief_Default.png",
            ("Mark IV (Operation: METEORITE)", "MkIV_Chief", "Experimental; stock customization resolver may reject it", "T_UI_MasterChief_MkIV_Chief.jpg"),
            ("Mark V Flawless Cowboy", "OriginalCE", "Foundry Armory Pack", "T_UI_MasterChief_OriginalCE.png"),
            ("Gilded Onyx", "BlackAndGold", "Foundry Armory Pack", "T_UI_MasterChief_BlackAndGold.png"),
            ("Splintered Warden", "Blammite", "Alpha Halo Armory Pack", "T_UI_MasterChief_Blammite.png"),
            ("Lochagos", "Spartan", "Alpha Halo Armory Pack", "T_UI_MasterChief_Spartan.png"),
            ("Mobile Armor Type 117", "Mech", "Alpha Halo Armory Pack", "T_UI_MasterChief_Mech.png"),
            ("Timberwolf", "LoneWolf", "Alpha Halo Armory Pack", "T_UI_MasterChief_LoneWolf.png"),
            ("Gestalt", "Hunter", "Alpha Halo Armory Pack", "T_UI_MasterChief_Hunter.png"),
            ("Silver Anniversary", "Ship_001", "Promotion", "T_UI_MasterChief_Ship001.png"),
            ("Fantastic Spartan", "Orange_001", "Promotion", "T_UI_MasterChief_Orange.png"),
            ("Stream of the Crop", "Purple_001", "Promotion", "T_UI_MasterChief_Purple.png"),
            ("Ignition", "Blue_001", "Promotion", "T_UI_MasterChief_Blue.png"),
            ("Promotional armor 02", "Ship_002", "Hidden / promotion", "T_UI_MasterChief_Ship002.png"),
            ("Promotional armor 03", "Ship_003", "Hidden / promotion", "T_UI_MasterChief_Ship003.png"),
            ("Promotional armor 04", "Ship_004", "Hidden / promotion", "T_UI_MasterChief_Ship004.png"),
            ("Promotional armor 05", "Ship_005", "Hidden / promotion", "T_UI_MasterChief_Ship005.png"),
            ("Promotional armor 06", "Ship_006", "Hidden / promotion", "T_UI_MasterChief_Ship006.png"),
            ("Promotional armor 07", "Ship_007", "Hidden / promotion", "T_UI_MasterChief_Ship007.png"),
            ("Promotional armor 08", "Ship_008", "Hidden / promotion", "T_UI_MasterChief_Ship008.png"),
            ("Promotional armor 09", "Ship_009", "Hidden / promotion", "T_UI_MasterChief_Ship009.png"),
            ("Promotional armor 10", "Ship_010", "Hidden / promotion", "T_UI_MasterChief_Ship010.png"),
            ("Promotional armor 11", "Ship_011", "Hidden / promotion", "T_UI_MasterChief_Ship011.png"),
            ("Promotional armor 12", "Ship_012", "Hidden / promotion", "T_UI_MasterChief_Ship012.png"),
            ("Promotional armor 13", "Ship_013", "Hidden / promotion", "T_UI_MasterChief_Ship013.png"),
            ("Promotional armor 14", "Ship_014", "Hidden / promotion", "T_UI_MasterChief_Ship014.png"),
            ("Promotional armor 15", "Ship_015", "Hidden / promotion", "T_UI_MasterChief_Ship015.png"),
            ("Promotional armor 16", "Ship_016", "Hidden / promotion", "T_UI_MasterChief_Ship016.png")),

        Category("Weapons", "Assault Rifle", "AssaultRifle", "T_UI_AssaultRifle_Default.png",
            ("Flawless AR", "OriginalCE", "Foundry Armory Pack", "T_UI_AssaultRifle_OriginalCE.png"),
            ("Gilded Onyx / Milestone", "BlackAndGold", "Foundry Armory Pack", "T_UI_AssaultRifle_BlackAndGold.png"),
            ("Reflex Mix", "Purple_001", "Promotion", "T_UI_AssaultRifle_Purple001.png"),
            ("Promotional AR (red)", "Red_001", "Hidden / promotion", "T_UI_AssaultRifle_Red001.png"),
            ("Silver Anniversary", "Ship_001", "Promotion", "T_UI_AssaultRifle_Ship001.png")),

        Category("Weapons", "Battle Rifle", "BattleRifle", "T_UI_BattleRifle_Default.png",
            ("Laconian Lance", "Spartan", "Alpha Halo Armory Pack", "T_UI_BattleRifle_Spartan.png")),

        Category("Weapons", "Energy Sword", "EnergySword", "T_UI_EnergySword_Default.png",
            ("Subanese Fang", "Blammite", "Alpha Halo Armory Pack", "T_UI_EnergySword_Blamite.png")),

        Category("Weapons", "Fuel Rod Cannon", "FuelRod", "T_UI_FuelRod_Default.png",
            ("Colossus Fuel Rod Cannon", "Hunter", "Alpha Halo Armory Pack", "T_UI_FuelRod_Hunter.png")),

        Category("Weapons", "Magnum", "Magnum", "T_UI_Magnum_Default.png",
            ("Cold Iron: Keyes", "Keyes", "Alpha Halo Armory Pack", "T_UI_Magnum_Keyes.png")),

        Category("Weapons", "Needler", "Needler", "T_UI_Needler_Default.png",
            ("Stone Needler", "Stone", "Hidden / promotion", "T_UI_Needler_Stone.png")),

        Category("Weapons", "Sniper Rifle", "SniperRifle", "T_UI_SniperRifle_Default.png",
            ("Savage Tooth", "LoneWolf", "Alpha Halo Armory Pack", "T_UI_SniperRifle_LoneWolf.png")),

        Category("Weapons", "SPNKr Rocket Launcher", "Spnkr", "T_UI_Spnkr_Default.png",
            ("Proto Type SPNKr", "Mech", "Alpha Halo Armory Pack", "T_UI_Spnkr_Mech.png")),
    ];

    public static string BuildTag(string segment, string value) => $"{Prefix}{segment}.{value}";

    public static string? GetRequiredPlayFabEntitlement(CosmeticChoice choice)
    {
        if (choice.Tag is null) return null;

        string value = choice.Tag.Split('.').Last();
        if (PromotionalEntitlementValues.Contains(value) ||
            value.StartsWith("ship_", StringComparison.OrdinalIgnoreCase))
            return $"WaypointUnlock_{value.ToLowerInvariant()}";

        return null;
    }

    public static bool CanEquipInRetail(
        CosmeticChoice choice,
        IEnumerable<string> ownedEntitlements)
    {
        if (choice.IsDefault) return true;

        string? required = GetRequiredPlayFabEntitlement(choice);
        return required is not null && ownedEntitlements.Contains(
            required,
            StringComparer.OrdinalIgnoreCase);
    }

    public static CosmeticChoice? FindMasterChiefChoiceForVariantIndex(int variantIndex)
    {
        CustomizationCategory? armor = Categories.FirstOrDefault(category =>
            category.TagSegment.Equals("MasterChief", StringComparison.OrdinalIgnoreCase));
        return armor?.Choices.FirstOrDefault(choice =>
            TryGetMasterChiefModelVariantIndex(choice, out int candidate) &&
            candidate == variantIndex);
    }

    /// <summary>
    /// Maps the installed Master Chief cosmetic tags to the corresponding
    /// spartans [hlmt] model-variant block. The cooked customization-table
    /// row index is a different ordering and must not be used here.
    /// </summary>
    public static bool TryGetMasterChiefModelVariantIndex(
        CosmeticChoice choice,
        out int variantIndex)
    {
        if (choice.RuntimeModelVariantIndex is int runtimeIndex)
        {
            variantIndex = runtimeIndex;
            return true;
        }

        variantIndex = -1;
        if (choice.Tag is null)
        {
            variantIndex = 0;
            return true;
        }

        const string armorPrefix = Prefix + "MasterChief.";
        if (!choice.Tag.StartsWith(armorPrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        string value = choice.Tag[armorPrefix.Length..];
        variantIndex = value.ToLowerInvariant() switch
        {
            "default" => 0,
            "mkiv_chief" => 1,
            "originalce" => 2,
            "blammite" => 3,
            "hunter" => 4,
            "lonewolf" => 5,
            "mech" => 6,
            "spartan" => 7,
            "blackandgold" => 9,
            "purple_001" => 10,
            "orange_001" => 11,
            "blue_001" => 12,
            "ship_001" => 17,
            "ship_002" => 18,
            "ship_003" => 19,
            "ship_004" => 20,
            "ship_005" => 21,
            "ship_006" => 22,
            "ship_007" => 23,
            "ship_008" => 24,
            "ship_009" => 25,
            "ship_010" => 26,
            "ship_011" => 27,
            "ship_012" => 28,
            "ship_013" => 29,
            "ship_014" => 30,
            "ship_015" => 31,
            "ship_016" => 32,
            _ => -1,
        };
        return variantIndex >= 0;
    }

    public static bool TryGetWeaponModelVariantIndex(
        string segment,
        CosmeticChoice choice,
        out int variantIndex)
    {
        variantIndex = -1;
        if (choice.Tag is null)
        {
            variantIndex = 0;
            return true;
        }

        string prefix = Prefix + segment + ".";
        if (!choice.Tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        string value = choice.Tag[prefix.Length..].ToLowerInvariant();
        variantIndex = segment.ToLowerInvariant() switch
        {
            "assaultrifle" => value switch
            {
                "originalce" => 1,
                "blackandgold" => 3,
                "purple_001" => 4,
                "red_001" => 7,
                "ship_001" => 10,
                _ => -1,
            },
            "battlerifle" when value == "spartan" => 1,
            "energysword" when value == "blammite" => 2,
            "fuelrod" when value == "hunter" => 1,
            "magnum" when value == "keyes" => 1,
            "needler" when value == "stone" => 1,
            "sniperrifle" when value == "lonewolf" => 2,
            "spnkr" when value == "mech" => 1,
            _ => -1,
        };
        return variantIndex >= 0;
    }

    public static bool TryGetSlotSegment(string tag, out string segment)
    {
        segment = string.Empty;
        if (!tag.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

        string remainder = tag[Prefix.Length..];
        int separator = remainder.IndexOf('.');
        if (separator <= 0) return false;
        segment = remainder[..separator];
        return true;
    }

    private static CustomizationCategory Category(
        string group,
        string name,
        string segment,
        string defaultImage,
        params (string Name, string Value, string Availability, string ImageName)[] items)
    {
        List<CosmeticChoice> choices =
        [
            new CosmeticChoice("Standard issue / game default", null, "Unlocked by default", defaultImage),
        ];
        choices.AddRange(items.Select(item =>
            new CosmeticChoice(
                item.Name,
                BuildTag(segment, item.Value),
                item.Availability,
                item.ImageName)));
        return new CustomizationCategory(group, name, segment, choices);
    }

    private static CustomizationCategory ArmorCategory(
        string group,
        string name,
        string segment,
        string defaultImage,
        params (string Name, string Value, string Availability, string ImageName)[] items)
    {
        CustomizationCategory category =
            Category(group, name, segment, defaultImage, items);
        List<CosmeticChoice> choices = category.Choices.ToList();
        choices[0] = choices[0] with
        {
            Name = "Mark VI (standard issue)",
            Availability = "Included with the base game; no entitlement required",
        };
        foreach (int index in new[] { 8, 13, 14, 15, 16 })
        {
            choices.Add(new CosmeticChoice(
                $"Hidden armor — Variant {index + 1:00}",
                null,
                "Runtime-only model variant; auto-applies in game and is not written as a stock gameplay tag",
                RuntimeModelVariantIndex: index,
                RuntimePreferenceValue: $"runtime:model-variant:{index}"));
        }
        return category with { Choices = choices };
    }
}

public sealed record CustomizationCategory(
    string Group,
    string Name,
    string TagSegment,
    IReadOnlyList<CosmeticChoice> Choices);
