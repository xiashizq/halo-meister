using System.Text;

namespace HaloMeister.Core;

public sealed record Mission(string Code, string Title)
{
    public string Display => $"{Code} \u2014 {Title}";
}

public sealed record EntitlementDefinition(string Id, string Display, string Category);

/// <summary>
/// Known tag vocabulary, seeded from a fully-unlocked save. The editor always unions this
/// with whatever the loaded file actually contains, so unrecognised tags are never lost and
/// new ones show up automatically.
/// </summary>
public static class Catalog
{
    public const string SkullPrefix = "Blam.Skull.";
    public const string TerminalPrefix = "Blam.Terminal.";
    public const string InsertionPrefix = "Blam.Progress.Mission.InsertionPoints.";
    public const string CompletionPrefix = "Blam.Progress.Mission.Completion.";

    /// <summary>
    /// Mission codes as they appear in the save. Titles are a best-effort mapping onto the
    /// classic campaign; the code is what the game actually keys off.
    /// </summary>
    public static readonly IReadOnlyList<Mission> Missions = new List<Mission>
    {
        new("e10", "Prologue I"),
        new("e20", "Prologue II"),
        new("e30", "Prologue III"),
        new("a15", "The Pillar of Autumn"),
        new("a30", "Halo"),
        new("a50", "Truth and Reconciliation"),
        new("b30", "The Silent Cartographer"),
        new("b40", "Assault on the Control Room"),
        new("c10", "343 Guilty Spark"),
        new("c20", "The Library"),
        new("c40", "Two Betrayals"),
        new("c45", "Two Betrayals (alt. key)"),
        new("d20", "Keyes"),
        new("d40", "The Maw"),
    };

    /// <summary>Difficulty keys, in the order the game lists them.</summary>
    public static readonly IReadOnlyList<string> Difficulties = new[]
    {
        "Easy", "Normal", "Heroic", "Legendary", "LASO", "Remix", "Remix.Deathless",
    };

    public static readonly IReadOnlyList<string> Skulls = new[]
    {
        "Adaptation", "Anger", "Armistice", "Bandana", "BlackEye", "Blind", "Boom",
        "BootsOffTheGround", "Catch", "Cowbell", "Efficient", "EnduranceSpec", "EyePatch",
        "Famine", "FloorIsLava", "Fog", "Foreign", "Ghost", "GiveAndTake", "GruntBirthdayParty",
        "GruntFuneral", "HipFire", "IWHBYD", "Iron", "JohnnyAmmoTree", "Leadhead", "LightsOut",
        "Magnified", "Malfunction", "Mythic", "NightVision", "Pinata", "Pop", "Recession",
        "Reload", "Riskrun", "SporeVisibility", "StowAndGrow", "Temperamental", "ThatsJustWrong",
        "TheyComeBack", "ThirdPerson", "Thunderstorm", "Tilt", "ToughLuck",
    };

    public static readonly IReadOnlyList<string> Terminals = new[]
    {
        "terminal_e10", "terminal_e20", "terminal_e30", "terminal_a10", "terminal_a30",
        "terminal_a50", "terminal_b30", "terminal_b40", "terminal_c10", "terminal_c20",
        "terminal_c40", "terminal_d20", "terminal_d40",
    };

    public static readonly IReadOnlyList<string> InsertionPoints = new[]
    {
        "ins_e10_rest_respite", "ins_e10_geneforge_start", "ins_e10_main_lower_lab",
        "ins_e20_start", "ins_e20_settlement_end", "ins_e20_prison_start", "ins_e20_pre_bridge",
        "ins_e30_start", "ins_e30_grav_lift", "ins_e30_transit_station", "ins_e30_space_1",
        "ins_a10_start", "ins_a10_cafeteria", "ins_a10_stairs",
        "ins_a30_lz", "ins_a30_cave", "ins_a30_holdouts",
        "ins_a50_area1", "ins_a50_gravity_room", "ins_a50_control",
        "ins_b30_beach_lz", "ins_b30_shaft_a_inactive", "ins_b30_shaft_a_active",
        "ins_b40_b2_scorpion", "ins_b40_b2_postchasm", "ins_b40_b3_start", "ins_b40_b4_start",
        "ins_c10_level_a", "ins_c10_level_b",
        "ins_c20_floor1_start", "ins_c20_floor2_start", "ins_c20_floor3_start",
        "ins_c45_section1", "ins_c45_section2", "ins_c45_section3", "ins_c45_section4",
        "ins_d20_section1", "ins_d20_section4", "ins_d20_section5",
        "ins_d40_start", "ins_d40_bridge", "ins_d40_warthog",
    };

    /// <summary>Mission-unlock gate tags, e.g. Blam.Progress.Mission.Completion.unlock_a30.</summary>
    public static readonly IReadOnlyList<string> UnlockGates = new[]
    {
        "unlock_e20", "unlock_e30", "unlock_a30", "unlock_a50", "unlock_b30", "unlock_b40",
        "unlock_c10", "unlock_c20", "unlock_c45", "unlock_d20", "unlock_d40",
    };

    /// <summary>
    /// PlayFab-backed customization entitlements shipped in
    /// DT_CustomizationEntitlementsTable. Platform-store product licenses are
    /// intentionally excluded because they are not values in this save array.
    /// </summary>
    public static readonly IReadOnlyList<EntitlementDefinition> Entitlements =
    [
        new("WaypointUnlock_blue_001", "Blue weapon coating", "Weapon coating"),
        new("WaypointUnlock_orange_001", "Orange weapon coating", "Weapon coating"),
        new("WaypointUnlock_purple_001", "Purple weapon coating", "Weapon coating"),
        new("WaypointUnlock_red_001", "Red weapon coating", "Weapon coating"),
        .. Enumerable.Range(1, 16).Select(index =>
            new EntitlementDefinition(
                $"WaypointUnlock_ship_{index:000}",
                $"Ship armor {index:00}",
                "Armor coating")),
    ];

    public static string SkullTag(string skull) => SkullPrefix + skull;
    public static string TerminalTag(string terminal) => TerminalPrefix + terminal;
    public static string InsertionTag(string insertion) => InsertionPrefix + insertion;
    public static string CompletionTag(string difficulty, string mission) => $"{CompletionPrefix}{difficulty}.{mission}";
    public static string UnlockTag(string gate) => CompletionPrefix + gate;

    /// <summary>Turns "GruntBirthdayParty" into "Grunt Birthday Party", leaving acronyms alone.</summary>
    public static string Humanize(string identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return identifier;
        if (Acronyms.Contains(identifier)) return identifier;

        var sb = new StringBuilder(identifier.Length + 8);
        for (int i = 0; i < identifier.Length; i++)
        {
            char c = identifier[i];
            if (c == '_')
            {
                sb.Append(' ');
                continue;
            }

            bool boundary = i > 0
                && char.IsUpper(c)
                && (!char.IsUpper(identifier[i - 1]) || (i + 1 < identifier.Length && char.IsLower(identifier[i + 1])));

            if (boundary && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            sb.Append(c);
        }

        return sb.ToString();
    }

    private static readonly HashSet<string> Acronyms = new(StringComparer.Ordinal)
    {
        "IWHBYD", "LASO", "EOD", "ODST",
    };
}
