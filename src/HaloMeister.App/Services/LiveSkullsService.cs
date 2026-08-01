using HaloMeister.App.Localization;
namespace HaloMeister.App.Services;

public sealed record LiveSkullDefinition(
    string Name,
    string DisplayName,
    int RuntimeIndex);

public sealed class LiveSkullItem
{
    public required LiveSkullDefinition Definition { get; init; }
    public bool IsEnabled { get; set; }

    public string Name => Definition.Name;
    public string DisplayName => Definition.DisplayName;
    public string Detail => $"Runtime skull {Definition.RuntimeIndex} · {Definition.Name}";
    public string IconUri =>
        $"ms-appx:///Assets/SkullIcons/{LiveSkullsService.IconFile(Name)}";
}

public sealed class LiveSkullsService
{
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;

    public static IReadOnlyList<LiveSkullDefinition> Catalog { get; } =
    [
        new("skull_iron", "Iron", 0),
        new("skull_black_eye", "Black Eye", 1),
        new("skull_tough_luck", "Tough Luck", 2),
        new("skull_catch", "Catch", 3),
        new("skull_fog", "Fog", 4),
        new("skull_famine", "Famine", 5),
        new("skull_thunderstorm", "Thunderstorm", 6),
        new("skull_tilt", "Tilt", 7),
        new("skull_mythic", "Mythic", 8),
        new("skull_assassin", "Assassin", 9),
        new("skull_blind", "Blind", 10),
        new("skull_superman", "Superman", 11),
        new("skull_birthday_party", "Grunt Birthday Party", 12),
        new("skull_daddy", "Daddy", 13),
        new("skull_red", "Red", 14),
        new("skull_yellow", "Yellow", 15),
        new("skull_blue", "Blue", 16),
        new("skull_angry", "Anger", 17),
        new("skull_bandanna", "Bandanna", 18),
        new("skull_bonded_pair", "Bonded Pair", 19),
        new("skull_boom", "Boom", 20),
        new("skull_envy", "Envy", 21),
        new("skull_eye_patch", "Eye Patch", 22),
        new("skull_foreign", "Foreign", 23),
        new("skull_ghost", "Ghost", 24),
        new("skull_grunt_funeral", "Grunt Funeral", 25),
        new("skull_jacked", "Jacked", 26),
        new("skull_malfunction", "Malfunction", 27),
        new("skull_masterblaster", "Master Blaster", 28),
        new("skull_pinata", "Pinata", 29),
        new("skull_recession", "Recession", 30),
        new("skull_scarab", "Scarab", 31),
        new("skull_so_angry", "So Angry", 32),
        new("skull_swarm", "Swarm", 33),
        new("skull_thats_just_wrong", "That's Just Wrong", 34),
        new("skull_they_come_back", "They Come Back", 35),
        new("skull_boots_off_the_ground", "Boots Off the Ground", 36),
        new("skull_adaptation", "Adaptation", 37),
        new("skull_reload", "Reload", 38),
        new("skull_spore_visibility", "Spore Visibility", 39),
        new("skull_night_vision", "Night Vision", 40),
        new("skull_lights_out", "Lights Out", 41),
        new("skull_riskrun", "Riskrun", 42),
        new("skull_pop", "Pop", 43),
        new("skull_armistice", "Armistice", 44),
        new("skull_fragile", "Fragile", 45),
        new("skull_give_and_take", "Give and Take", 46),
        new("skull_stow_and_grow", "Stow and Grow", 47),
        new("skull_hip_fire", "Hip Fire", 48),
        new("skull_temperamental", "Temperamental", 49),
        new("skull_floor_is_lava", "Floor Is Lava", 50),
        new("skull_magnified", "Magnified", 51),
        new("skull_johnny_ammo_tree", "Johnny Ammo Tree", 52),
        new("skull_leadhead", "Leadhead", 53),
        new("skull_efficient", "Efficient", 54),
        new("skull_third_person", "Third Person", 55),
    ];

    public ScriptingBridgeStatus BridgeStatus => _bridge.GetStatus();

    public static string IconFile(string name) => name switch
    {
        "skull_iron" => "T_Icon_Skull_Iron.png",
        "skull_black_eye" => "T_Icon_Skull_BlackEye.png",
        "skull_tough_luck" => "T_Icon_Skulls_ToughLuck.png",
        "skull_catch" => "T_Icon_Skull_Catch.png",
        "skull_fog" => "T_Icon_Skull_Fog.png",
        "skull_famine" => "T_Icon_Skull_Famine.png",
        "skull_thunderstorm" => "T_Icon_Skull_Thunderstorm.png",
        "skull_tilt" => "T_Icon_Skull_Tilt.png",
        "skull_mythic" => "T_Icon_Skull_Mythic.png",
        "skull_blind" => "T_Icon_Skull_Blind.png",
        "skull_birthday_party" => "T_Icon_Skull_GruntBirthdayParty.png",
        "skull_angry" or "skull_so_angry" => "T_Icon_Skull_Angry.png",
        "skull_bandanna" => "T_Icon_Skull_Bandana.png",
        "skull_boom" => "T_Icon_Skull_Boom.png",
        "skull_cowbell" => "T_Icon_Skulls_Cowbell.png",
        "skull_eye_patch" => "T_Icon_Skull_EyePatch.png",
        "skull_foreign" => "T_Icon_Skull_Foreign.png",
        "skull_ghost" => "T_Icon_Skull_Ghost.png",
        "skull_grunt_funeral" => "T_Icon_Skull_GruntFuneral.png",
        "skull_malfunction" => "T_Icon_Skull_Malfunction.png",
        "skull_pinata" => "T_Icon_Skulls_Pinata.png",
        "skull_recession" => "T_Icon_Skull_Recession.png",
        "skull_thats_just_wrong" => "T_Icon_Skulls_ThatsJustWrong.png",
        "skull_they_come_back" => "T_Icon_Skull_TheyComeBack.png",
        "skull_boots_off_the_ground" => "T_Icon_Skull_Acrophobia.png",
        "skull_adaptation" => "T_Icon_Skull_Adaptation.png",
        "skull_reload" => "T_Icon_Skull_Reload.png",
        "skull_spore_visibility" => "T_Icon_Skull_SporeVisibility.png",
        "skull_night_vision" => "T_Icon_Skull_Nightvision.png",
        "skull_riskrun" => "T_Icon_Skull_RiskRun.png",
        "skull_pop" => "T_Icon_Skull_Pop.png",
        "skull_armistice" => "T_Icon_Skull_Armistice.png",
        "skull_give_and_take" => "T_Icon_Skull_GiveAndTake.png",
        "skull_stow_and_grow" => "T_Icon_Skull_StowAndGrow.png",
        "skull_hip_fire" => "T_Icon_Skull_HipFire.png",
        "skull_iwhbyd" => "T_Icon_Skull_IWHBYD.png",
        "skull_temperamental" => "T_Icon_Skull_Temperamental.png",
        "skull_floor_is_lava" => "T_Icon_Skull_FloorIsLava.png",
        "skull_magnified" => "T_Icon_Skulls_Magnified.png",
        "skull_johnny_ammo_tree" => "T_Icon_Skull_JohnnyAmmoTree.png",
        "skull_leadhead" => "T_Icon_Skull_Relentless.png",
        "skull_efficient" => "T_Icon_Skull_Efficent.png",
        "skull_third_person" => "T_Icon_Skull_Perspective.png",
        _ => "T_Icon_Skulls_VisualDefault.png",
    };

    public async Task<IReadOnlyList<LiveSkullItem>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureBridgeReady();
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.BlamSkullsRead,
            "read",
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);

        Dictionary<string, bool> values = ParseValues(result.Message);
        LiveSkullItem[] items = Catalog
            .Select(definition =>
            {
                if (!values.TryGetValue(definition.Name, out bool enabled))
                    throw new InvalidDataException(
                        $"The native bridge did not return {definition.Name}.");
                return new LiveSkullItem
                {
                    Definition = definition,
                    IsEnabled = enabled,
                };
            })
            .ToArray();
        if (values.Count != Catalog.Count)
            throw new InvalidDataException(
                "The native bridge returned an unexpected live-skull catalog.");
        return items;
    }

    public async Task SetAsync(
        string name,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (!Catalog.Any(skull =>
                string.Equals(skull.Name, name, StringComparison.Ordinal)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(name),
                "The requested skull is not in the verified runtime catalog.");
        }

        EnsureBridgeReady();
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.BlamSkullWrite,
            $"{name}={(enabled ? 1 : 0)}",
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);

        Dictionary<string, bool> values = ParseValues(result.Message);
        if (!values.TryGetValue(name, out bool actual) || actual != enabled)
            throw new InvalidDataException(
                "The game did not confirm the requested skull state.");
    }

    private static Dictionary<string, bool> ParseValues(string message)
    {
        var values = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (string line in message.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf('=');
            if (separator <= 0 ||
                separator + 2 != line.Length ||
                (line[^1] != '0' && line[^1] != '1'))
            {
                throw new InvalidDataException(
                    "The native bridge returned an invalid live-skull value.");
            }
            values[line[..separator]] = line[^1] == '1';
        }
        return values;
    }

    private void EnsureBridgeReady()
    {
        ScriptingBridgeStatus status = BridgeStatus;
        if (!status.IsRuntimeReady)
            throw new InvalidOperationException(
                L.Get("bridge.error_not_responding"));
        if (status.IsStale)
            throw new InvalidOperationException(status.Summary);
    }
}
