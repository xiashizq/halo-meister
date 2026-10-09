namespace HaloMeister.App.Services;

/// <summary>
/// Shipping built-in overlay packages. The fuller character roster is its own
/// pack. Combat behavior is one optional pack per character.
/// </summary>
public static class BuiltinModCatalog
{
    public const string CampaignId = "campaign";
    public const string CharactersId = "characters";
    public const string TrooperId = "char_trooper";
    public const string EliteId = "char_elite";
    public const string GruntId = "char_grunt";
    public const string JackalId = "char_jackal";
    public const string BruteId = "char_brute";
    public const string HunterId = "char_hunter";

    public static BuiltinModDefinition Campaign { get; } = new(
        Id: CampaignId,
        Stem: FullPalettesOverlayService.OverlayStem,
        ExpectedFingerprint: FullPalettesOverlayService.ExpectedBundledFingerprint,
        LegacyStems:
        [
            "HM_FullPalettes_P",
            "ZZ_HM_DemoSquads_P",
            "HM_DemoSquads_P",
        ],
        TitleKey: "builtin_mod.campaign.title",
        DescriptionKey: "builtin_mod.campaign.description",
        NoteKeys:
        [
            "builtin_mod.campaign.optional",
            "builtin_mod.campaign.required",
        ]);

    public static BuiltinModDefinition Characters { get; } = new(
        Id: CharactersId,
        Stem: FullPalettesOverlayService.CharacterOverlayStem,
        ExpectedFingerprint: FullPalettesOverlayService.ExpectedCharacterFingerprint,
        LegacyStems: [],
        TitleKey: "builtin_mod.characters.title",
        DescriptionKey: "builtin_mod.characters.description",
        NoteKeys: ["builtin_mod.characters.note"]);

    public static BuiltinModDefinition Trooper { get; } = Enhancement(
        TrooperId,
        "MMYJ_CHAR_TROOPER_P",
        FullPalettesOverlayService.ExpectedTrooperFingerprint);

    public static BuiltinModDefinition Elite { get; } = Enhancement(
        EliteId,
        "MMYJ_CHAR_ELITE_P",
        FullPalettesOverlayService.ExpectedEliteFingerprint);

    public static BuiltinModDefinition Grunt { get; } = Enhancement(
        GruntId,
        "MMYJ_CHAR_GRUNT_P",
        FullPalettesOverlayService.ExpectedGruntFingerprint);

    public static BuiltinModDefinition Jackal { get; } = Enhancement(
        JackalId,
        "MMYJ_CHAR_JACKAL_P",
        FullPalettesOverlayService.ExpectedJackalFingerprint);

    public static BuiltinModDefinition Brute { get; } = Enhancement(
        BruteId,
        "MMYJ_CHAR_BRUTE_P",
        FullPalettesOverlayService.ExpectedBruteFingerprint);

    public static BuiltinModDefinition Hunter { get; } = Enhancement(
        HunterId,
        "MMYJ_CHAR_HUNTER_P",
        FullPalettesOverlayService.ExpectedHunterFingerprint);

    public static IReadOnlyList<BuiltinModDefinition> Core { get; } =
    [
        Campaign,
        Characters,
    ];

    public static IReadOnlyList<BuiltinModDefinition> Enhancements { get; } =
    [
        Trooper,
        Elite,
        Grunt,
        Jackal,
        Brute,
        Hunter,
    ];

    public static IReadOnlyList<BuiltinModDefinition> All { get; } =
        [.. Core, .. Enhancements];

    public static bool IsCore(string id) =>
        id is CampaignId or CharactersId;

    private static BuiltinModDefinition Enhancement(
        string id,
        string stem,
        string fingerprint) => new(
        Id: id,
        Stem: stem,
        ExpectedFingerprint: fingerprint,
        LegacyStems: [],
        TitleKey: $"builtin_mod.{id}.title",
        DescriptionKey: $"builtin_mod.{id}.description",
        NoteKeys: ["builtin_mod.char_enhance.note"]);
}

public sealed record BuiltinModDefinition(
    string Id,
    string Stem,
    string ExpectedFingerprint,
    IReadOnlyList<string> LegacyStems,
    string TitleKey,
    string DescriptionKey,
    IReadOnlyList<string> NoteKeys);
