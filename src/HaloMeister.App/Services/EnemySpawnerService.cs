using System.Globalization;
using System.Buffers.Binary;
using HaloMeister.App.Models;

namespace HaloMeister.App.Services;

public sealed record SpawnVariantChoice(
    string Name,
    byte[] StringIdBytes,
    short VariantIndex,
    int VariantBlockIndex,
    string? ImageUri = null)
{
    public uint StringId => StringIdBytes.Length == sizeof(uint)
        ? BinaryPrimitives.ReadUInt32LittleEndian(StringIdBytes)
        : 0;
    public string Detail => VariantIndex >= 0
        ? $"Model variant {VariantIndex}"
        : "Authored default";
}

public sealed record EnemySpawnChoice(
    RuntimeTagEntry CharacterTag,
    IReadOnlyList<SpawnVariantChoice> Variants)
{
    public string LeafName => CharacterTag.LeafName;
    public string DisplayName => FriendlyName(LeafName);
    public string TagPath => CharacterTag.Name;
    public string Category => CategorizeCharacter(CharacterTag.Name);
    public string VariantSummary => Variants.Count == 1
        ? "1 available variant"
        : $"{Variants.Count:N0} available variants";
    public string SearchText =>
        $"{DisplayName} {TagPath} {Category} {string.Join(' ', Variants.Select(item => item.Name))}";

    private static string CategorizeCharacter(string path)
    {
        string value = path.Replace('\\', '/').ToLowerInvariant();
        if (ContainsAny(value, "elite", "grunt", "jackal", "hunter", "engineer",
                "prophet", "brute", "drone"))
            return "Covenant";
        if (ContainsAny(value, "flood", "infection", "carrier", "combat_form",
                "pureform"))
            return "Flood";
        if (ContainsAny(value, "marine", "crewman", "keyes", "johnson", "pilot",
                "spartan", "masterchief", "master_chief", "odst"))
            return "UNSC";
        if (ContainsAny(value, "sentinel", "monitor", "enforcer", "forerunner"))
            return "Forerunner";
        if (ContainsAny(value, "critter", "ambient", "wildlife"))
            return "Wildlife";
        return "Other";
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.Ordinal));

    internal static string FriendlyName(string value)
    {
        string text = value.Replace('_', ' ').Replace('-', ' ').Trim();
        return text.Length == 0
            ? "Unnamed character"
            : string.Join(
                ' ',
                text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(word =>
                        char.ToUpperInvariant(word[0]) + word[1..]));
    }
}

public sealed record ArmorSpawnChoice(
    RuntimeTagEntry BipedTag,
    IReadOnlyList<SpawnVariantChoice> Variants)
{
    public string DisplayName => "Johnson Spartan";
    public string TagPath => BipedTag.Name;
    public string Category => "UNSC companion";
    public string VariantSummary => $"{Variants.Count:N0} available armor sets";
    public string SearchText =>
        $"{DisplayName} {TagPath} {Category} {string.Join(' ', Variants.Select(item => item.Name))}";
}

public sealed record AiWeaponChoice(RuntimeTagEntry WeaponTag)
{
    public string DisplayName =>
        EnemySpawnChoice.FriendlyName(WeaponTag.LeafName);
    public string TagPath => WeaponTag.Name;
    public uint Datum =>
        RuntimeTagMemoryService.BuildRuntimeDatum(WeaponTag);
}

public sealed record SpawnerCatalog(
    IReadOnlyList<EnemySpawnChoice> Characters,
    IReadOnlyList<ArmorSpawnChoice> Armor,
    string ArmorStatus);

public sealed class EnemySpawnerService : IDisposable
{
    private readonly RuntimeTagMemoryService _memory = RuntimeTagMemoryService.Current;
    private readonly RuntimeTagDefinitionService _definitions = new();
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private IReadOnlyList<RuntimeTagEntry> _tags = [];
    private int _warmedProcessId;

    public int ProcessId => _memory.ProcessId;
    public ScriptingBridgeStatus BridgeStatus => _bridge.GetStatus();

    public SpawnerCatalog Connect()
    {
        WarmUpDefinitions();

        if (!_memory.IsConnected)
            throw new InvalidOperationException(
                "Connect to the game from the header first.");
        _tags = _memory.ReadTags();
        EnemySpawnChoice[] characters = _tags
            .Where(tag =>
                string.Equals(tag.Group, "char", StringComparison.OrdinalIgnoreCase) &&
                tag.Name.Contains(@"\ai\", StringComparison.OrdinalIgnoreCase) &&
                !tag.Name.Contains(@"\stimuli\", StringComparison.OrdinalIgnoreCase))
            .GroupBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(character => new EnemySpawnChoice(
                character,
                ReadVariants(character)))
            .Where(choice => choice.Variants.Count > 0)
            .OrderBy(choice => choice.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.TagPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IReadOnlyList<ArmorSpawnChoice> armor = ReadArmorChoices(out string armorStatus);
        return new SpawnerCatalog(characters, armor, armorStatus);
    }

    public async Task<ScriptExecutionResult> SpawnAsync(
        EnemySpawnChoice choice,
        SpawnVariantChoice variant,
        CancellationToken cancellationToken = default)
        => await SpawnGroupAsync(
            choice,
            variant,
            1,
            cancellationToken: cancellationToken);

    public async Task<ScriptExecutionResult> SpawnGroupAsync(
        EnemySpawnChoice choice,
        SpawnVariantChoice variant,
        int count,
        float formationOffsetX = 0,
        float formationOffsetY = 0,
        AiWeaponChoice? weapon = null,
        bool followPlayer = false,
        CancellationToken cancellationToken = default)
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException("Connect to the running mission first.");
        if (count is < 1 or > 5)
            throw new ArgumentOutOfRangeException(
                nameof(count),
                "One native AI batch can contain between one and five actors.");
        WorldPoint playerPosition =
            await ReadPlayerPositionAsync(cancellationToken);
        string payload = await Task.Run(() => BuildPayload(
            choice,
            variant,
            playerPosition,
            count,
            formationOffsetX,
            formationOffsetY,
            weapon,
            followPlayer), cancellationToken);
        return await _bridge.ExecuteAsync(
            count == 1
                ? ScriptLanguage.BlamAiSpawn
                : ScriptLanguage.BlamAiTeamSpawn,
            payload,
            TimeSpan.FromSeconds(20),
            cancellationToken: cancellationToken);
    }

    public void WarmUpDefinitions()
    {
        if (_definitions.SchemaCount == 0)
            _definitions.LoadDirectory(
                RuntimeTagDefinitionLocator.ResolveCampaignEvolved());
        if (!_definitions.HasSchema("char") || !_definitions.HasSchema("scnr"))
            throw new InvalidDataException(
                "The loaded definitions do not provide both [char] and [scnr] schemas.");
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (_warmedProcessId == _memory.ProcessId && _warmedProcessId != 0)
            return;

        ScriptingBridgeStatus status = _bridge.GetStatus();
        if (!status.IsRuntimeReady || status.IsStale)
            return;

        try
        {
            ScriptExecutionResult result = await _bridge.ExecuteAsync(
                ScriptLanguage.PlayerPosition,
                "read",
                TimeSpan.FromSeconds(5),
                cancellationToken);
            if (result.Outcome == ScriptOutcome.Confirmed)
                _warmedProcessId = _memory.ProcessId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Optional prewarm; spawning remains available if this build does
            // not expose the player-position capability.
        }
    }

    public async Task<ScriptExecutionResult> SpawnTeamAsync(
        EnemySpawnChoice choice,
        SpawnVariantChoice variant,
        CancellationToken cancellationToken = default)
    {
        return await SpawnGroupAsync(
            choice,
            variant,
            5,
            cancellationToken: cancellationToken);
    }

    public IReadOnlyList<AiWeaponChoice> GetCompatibleWeapons(
        EnemySpawnChoice choice)
    {
        if (!_memory.IsConnected)
            return [];
        _tags = _memory.ReadTags();
        RuntimeTagEntry? character = _tags.FirstOrDefault(tag =>
            tag.Index == choice.CharacterTag.Index &&
            string.Equals(tag.Group, "char", StringComparison.OrdinalIgnoreCase));
        return character is null
            ? []
            : ReadCompatibleWeapons(character);
    }

    public IReadOnlyList<EnemySpawnChoice> GetCharacterFamilyVariants(
        EnemySpawnChoice choice)
    {
        if (!_memory.IsConnected)
            return [choice];
        _tags = _memory.ReadTags();
        string family = CharacterFamily(choice.CharacterTag.Name);
        EnemySpawnChoice[] choices = _tags
            .Where(tag =>
                string.Equals(tag.Group, "char", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0 &&
                tag.Name.Contains(@"\ai\", StringComparison.OrdinalIgnoreCase) &&
                !tag.Name.Contains(@"\null\", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    CharacterFamily(tag.Name),
                    family,
                    StringComparison.OrdinalIgnoreCase))
            .GroupBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(character => new EnemySpawnChoice(
                character,
                ReadVariants(character)))
            .Where(candidate => candidate.Variants.Count > 0)
            .OrderBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return choices.Length == 0
            ? [choice]
            : choices;
    }

    public IReadOnlyList<AiWeaponChoice> GetJohnsonCompatibleWeapons()
    {
        if (!_memory.IsConnected)
            return [];
        _tags = _memory.ReadTags();
        RuntimeTagEntry? scenario = _tags.FirstOrDefault(tag =>
            string.Equals(tag.Group, "scnr", StringComparison.OrdinalIgnoreCase) &&
            tag.DataAddress > 0);
        if (scenario is null)
            return [];
        RuntimeTagFieldValue? palette = ReadRoot(scenario).FirstOrDefault(field =>
            field.CanOpenBlock &&
            string.Equals(
                field.ChildBlockDefinition,
                "scenario_weapon_palette_block",
                StringComparison.OrdinalIgnoreCase));
        if (palette is null)
            return [];

        var weapons = new List<AiWeaponChoice>();
        for (int index = 0; index < Math.Min(palette.ChildCount, 1024); index++)
        {
            RuntimeTagFieldValue? reference = ReadBlock(scenario, palette, index)
                .FirstOrDefault(field => field.IsTagReference);
            RuntimeTagEntry? weapon = reference is null
                ? null
                : _tags.FirstOrDefault(tag =>
                    tag.Index == reference.ReferencedTagIndex &&
                    string.Equals(tag.Group, "weap", StringComparison.OrdinalIgnoreCase) &&
                    tag.DataAddress > 0);
            if (weapon is not null && IsSpartanCompatibleWeapon(weapon.Name))
                weapons.Add(new AiWeaponChoice(weapon));
        }
        return weapons
            .GroupBy(item => item.WeaponTag.Index)
            .Select(group => group.First())
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<ScriptExecutionResult> SpawnArmorWithJohnsonAiAsync(
        ArmorSpawnChoice armor,
        SpawnVariantChoice armorVariant,
        int count,
        float formationOffsetX = 0,
        float formationOffsetY = 0,
        AiWeaponChoice? weapon = null,
        bool followPlayer = true,
        CancellationToken cancellationToken = default)
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException("Connect to the running mission first.");
        _tags = _memory.ReadTags();

        RuntimeTagEntry johnson = FindJohnsonCharacter()
            ?? throw new InvalidOperationException(
                "No loaded Johnson [char] AI tag was found in this mission.");
        RuntimeTagEntry spartan = _tags.FirstOrDefault(tag =>
                tag.Index == armor.BipedTag.Index &&
                string.Equals(tag.Group, "bipd", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0)
            ?? throw new InvalidOperationException(
                "The selected Spartan biped is no longer loaded. Rescan the mission.");
        RuntimeTagFieldValue johnsonUnit = ReadRoot(johnson).FirstOrDefault(field =>
                field.IsTagReference &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "unit",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"The Johnson character {johnson.Name} has no unit reference.");
        RuntimeTagFieldValue defaultVariant = ReadRoot(spartan).FirstOrDefault(field =>
                field.Type == "string_id" &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "default model variant",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"The Spartan biped {spartan.Name} has no default model variant.");

        byte[] originalUnit = _memory.ReadBytes(johnsonUnit.Address, 16);
        byte[] spartanReference = _memory.BuildTagReference(spartan);
        byte[] originalVariant = _memory.ReadBytes(
            defaultVariant.Address,
            sizeof(uint));
        IReadOnlyList<MemoryPatch> shieldPatches = [];
        bool patchedUnit =
            !originalUnit.AsSpan().SequenceEqual(spartanReference);
        bool patchedVariant =
            !originalVariant.AsSpan().SequenceEqual(armorVariant.StringIdBytes);
        if (patchedUnit)
            _memory.WriteVerified(johnsonUnit.Address, spartanReference);
        try
        {
            shieldPatches = ApplyAuthoredSpartanShields(johnson);
            if (patchedVariant)
                _memory.WriteVerified(
                    defaultVariant.Address,
                    armorVariant.StringIdBytes);
            var johnsonChoice = new EnemySpawnChoice(
                johnson,
                ReadVariants(johnson));
            SpawnVariantChoice johnsonVariant =
                johnsonChoice.Variants.FirstOrDefault()
                ?? throw new InvalidDataException(
                    "The loaded Johnson character exposes no actor variant.");
            return await SpawnGroupAsync(
                johnsonChoice,
                johnsonVariant,
                count,
                formationOffsetX,
                formationOffsetY,
                weapon,
                followPlayer,
                cancellationToken);
        }
        finally
        {
            if (_memory.IsConnected)
            {
                RestorePatches(shieldPatches);
                if (patchedVariant)
                    _memory.WriteVerified(
                        defaultVariant.Address,
                        originalVariant);
                if (patchedUnit)
                    _memory.WriteVerified(
                        johnsonUnit.Address,
                        originalUnit);
            }
        }
    }

    public async Task<ScriptExecutionResult> SpawnBodyAsync(
        EnemySpawnChoice choice,
        SpawnVariantChoice variant,
        CancellationToken cancellationToken = default)
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException("Connect to the running mission first.");
        _tags = _memory.ReadTags();
        RuntimeTagEntry character = _tags.FirstOrDefault(tag =>
                tag.Index == choice.CharacterTag.Index &&
                string.Equals(tag.Group, "char", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "That character tag is no longer loaded. Rescan the mission.");
        RuntimeTagFieldValue unit = ReadRoot(character).FirstOrDefault(field =>
                field.IsTagReference &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "unit",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"The selected character {character.Name} has no authored unit reference.");
        RuntimeTagEntry biped = _tags.FirstOrDefault(tag =>
                tag.Index == unit.ReferencedTagIndex &&
                string.Equals(tag.Group, "bipd", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0)
            ?? throw new InvalidDataException(
                "The selected character's [bipd] unit is not published in the live tag table.");

        return await SpawnVariantBodyCoreAsync(biped, variant, cancellationToken);
    }

    public async Task<ScriptExecutionResult> SpawnArmorAsync(
        ArmorSpawnChoice choice,
        SpawnVariantChoice variant,
        CancellationToken cancellationToken = default)
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException("Connect to the running mission first.");
        _tags = _memory.ReadTags();
        RuntimeTagEntry biped = _tags.FirstOrDefault(tag =>
                tag.Index == choice.BipedTag.Index &&
                string.Equals(tag.Group, "bipd", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(tag.Name, choice.BipedTag.Name, StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0)
            ?? throw new InvalidOperationException(
                "The Spartan biped is no longer loaded. Rescan the mission.");

        return await SpawnVariantBodyCoreAsync(biped, variant, cancellationToken);
    }

    private async Task<ScriptExecutionResult> SpawnVariantBodyCoreAsync(
        RuntimeTagEntry biped,
        SpawnVariantChoice variant,
        CancellationToken cancellationToken)
    {
        RuntimeTagFieldValue defaultVariant = ReadRoot(biped).FirstOrDefault(field =>
                field.Type == "string_id" &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "default model variant",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"The biped {biped.Name} does not expose its default model variant.");
        if (defaultVariant.Size != sizeof(uint) ||
            variant.StringIdBytes.Length != sizeof(uint))
            throw new InvalidDataException(
                "The selected model variant does not use a four-byte string ID.");

        byte[] original = _memory.ReadBytes(defaultVariant.Address, sizeof(uint));
        bool patched = !original.AsSpan().SequenceEqual(variant.StringIdBytes);
        if (patched)
            _memory.WriteVerified(defaultVariant.Address, variant.StringIdBytes);

        try
        {
            ScriptExecutionResult result = await _bridge.ExecuteAsync(
                ScriptLanguage.BlamBipedVariantSpawn,
                $"{RuntimeTagMemoryService.BuildRuntimeDatum(biped):X8},{variant.StringId:X8}",
                TimeSpan.FromSeconds(15),
                cancellationToken);
            if (result.Outcome != ScriptOutcome.Confirmed)
                throw new InvalidOperationException(result.Message);
            return result;
        }
        finally
        {
            // Keep the authored default patched through the native bridge's
            // deferred model-initialization window, but never leave the loaded
            // biped tag modified after this one spawn transaction.
            if (patched && _memory.IsConnected)
                _memory.WriteVerified(defaultVariant.Address, original);
        }
    }

    private async Task<WorldPoint> ReadPlayerPositionAsync(
        CancellationToken cancellationToken)
    {
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.PlayerPosition,
            "current",
            cancellationToken: cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);

        const string marker = "Return value: ";
        int markerOffset = result.Message.IndexOf(marker, StringComparison.Ordinal);
        string[] values = markerOffset < 0
            ? []
            : result.Message[(markerOffset + marker.Length)..]
                .Trim()
                .Split(',', StringSplitOptions.TrimEntries);
        if (values.Length != 3 ||
            !float.TryParse(
                values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
            !float.TryParse(
                values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
            !float.TryParse(
                values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
            throw new InvalidDataException(
                "The game returned an invalid player position. Resume a campaign checkpoint and try again.");
        return new WorldPoint(x, y, z);
    }

    private string BuildPayload(
        EnemySpawnChoice choice,
        SpawnVariantChoice variant,
        WorldPoint playerPosition,
        int placementCount = 1,
        float formationOffsetX = 0,
        float formationOffsetY = 0,
        AiWeaponChoice? weapon = null,
        bool followPlayer = false)
    {
        if (placementCount is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(placementCount));
        RuntimeTagEntry scenario = _tags.FirstOrDefault(tag =>
            string.Equals(tag.Group, "scnr", StringComparison.OrdinalIgnoreCase) &&
            tag.DataAddress > 0)
            ?? throw new InvalidOperationException(
                "No loaded [scnr] tag with readable data was found. Load a campaign mission first.");
        IReadOnlyList<RuntimeTagFieldValue> root = ReadRoot(scenario);
        RuntimeTagFieldValue palette = root.FirstOrDefault(field =>
            field.ChildBlockDefinition == "character_palette_block")
            ?? throw new InvalidDataException(
                "The loaded scenario has no readable character palette.");
        RuntimeTagFieldValue squads = root.FirstOrDefault(field =>
            field.ChildBlockDefinition == "squads_block")
            ?? throw new InvalidDataException("The loaded scenario has no readable squads.");
        RuntimeTagFieldValue? objectives = root.FirstOrDefault(field =>
            field.ChildBlockDefinition == "objectives_block" &&
            field.CanOpenBlock);

        int hostileSquads = 0;
        int squadsWithSpawnPoints = 0;
        int inspectedSpawnPoints = 0;
        int indexedSpawnPoints = 0;
        int cellBasedSpawnPoints = 0;
        SpawnTemplate? nearest = null;
        int nearestPriority = int.MaxValue;
        bool nearestFollowsPlayer = false;
        for (int squadIndex = 0; squadIndex < Math.Min(squads.ChildCount, 2048); squadIndex++)
        {
            IReadOnlyList<RuntimeTagFieldValue> squad = ReadBlock(
                scenario, squads, squadIndex);
            RuntimeTagFieldValue? team = squad.FirstOrDefault(field =>
                field.Type == "short_enum" &&
                field.Name.StartsWith("team", StringComparison.OrdinalIgnoreCase));
            if (team is null ||
                !short.TryParse(
                    team.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out short teamIndex))
                continue;
            bool isHostile = teamIndex is not (0 or 1 or 2 or 7);
            if (isHostile)
                hostileSquads++;
            bool followsPlayer =
                followPlayer &&
                !isHostile &&
                objectives is not null &&
                SquadFollowsPlayer(scenario, squad, objectives);

            RuntimeTagFieldValue? spawnPoints = squad.FirstOrDefault(field =>
                field.ChildBlockDefinition == "spawn_points_block" &&
                field.CanOpenBlock);
            if (spawnPoints is null) continue;
            squadsWithSpawnPoints++;
            for (int pointIndex = 0;
                 pointIndex < Math.Min(spawnPoints.ChildCount, 256);
                 pointIndex++)
            {
                inspectedSpawnPoints++;
                IReadOnlyList<RuntimeTagFieldValue> point = ReadBlock(
                    scenario, spawnPoints, pointIndex);
                RuntimeTagFieldValue? characterType = point.FirstOrDefault(field =>
                    field.Type == "short_block_index" &&
                    field.Name.StartsWith("character type", StringComparison.OrdinalIgnoreCase));
                RuntimeTagFieldValue? position = point.FirstOrDefault(field =>
                    field.Type == "real_point_3d" &&
                    field.Name.StartsWith("position", StringComparison.OrdinalIgnoreCase));
                RuntimeTagFieldValue? actorVariant = point.FirstOrDefault(field =>
                    field.Type == "string_id" &&
                    string.Equals(
                        CleanFieldName(field.Name),
                        "actor variant name",
                        StringComparison.OrdinalIgnoreCase));
                if (characterType is null || position is null ||
                    actorVariant is null || actorVariant.Size != 4)
                    continue;
                short paletteIndex = ReadInt16(characterType.Address);
                if (paletteIndex < 0)
                {
                    RuntimeTagFieldValue? cellIndexField = point.FirstOrDefault(field =>
                        field.Type == "custom_short_block_index" &&
                        field.Name.StartsWith("cell", StringComparison.OrdinalIgnoreCase));
                    if (cellIndexField is null) continue;
                    short cellIndex = ReadInt16(cellIndexField.Address);
                    if (cellIndex < 0) continue;
                    paletteIndex = FindCellPaletteIndex(scenario, squad, cellIndex);
                    if (paletteIndex < 0) continue;
                    cellBasedSpawnPoints++;
                }
                else
                {
                    indexedSpawnPoints++;
                }
                if (paletteIndex >= palette.ChildCount) continue;

                RuntimeTagFieldValue? reference = ReadBlock(
                    scenario, palette, paletteIndex).FirstOrDefault(field =>
                        field.IsTagReference);
                if (reference is null) continue;
                RuntimeTagEntry? sourceCharacter = _tags.FirstOrDefault(tag =>
                    tag.Index == reference.ReferencedTagIndex &&
                    string.Equals(
                        tag.Group,
                        "char",
                        StringComparison.OrdinalIgnoreCase));
                if (sourceCharacter is null ||
                    sourceCharacter.Name.Contains(
                        @"\null\",
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                WorldPoint templatePosition = ReadPoint(position.Address);
                double distanceSquared =
                    Math.Pow(templatePosition.X - playerPosition.X, 2) +
                    Math.Pow(templatePosition.Y - playerPosition.Y, 2) +
                    Math.Pow(templatePosition.Z - playerPosition.Z, 2);
                bool exactCharacter =
                    sourceCharacter.Index == choice.CharacterTag.Index;
                bool sameCharacterFamily = string.Equals(
                    CharacterFamily(sourceCharacter.Name),
                    CharacterFamily(choice.CharacterTag.Name),
                    StringComparison.OrdinalIgnoreCase);
                int priority = exactCharacter
                    ? 0
                    : sameCharacterFamily
                        ? 1
                        : isHostile
                            ? 2
                            : 3;
                if (followsPlayer)
                    priority -= 10;
                if (nearest is null ||
                    priority < nearestPriority ||
                    (priority == nearestPriority &&
                     distanceSquared < nearest.DistanceSquared))
                {
                    nearestPriority = priority;
                    nearest = new SpawnTemplate(
                        squadIndex,
                        team.Address,
                        reference.Address,
                        position.Address,
                        actorVariant.Address,
                        distanceSquared);
                    nearestFollowsPlayer = followsPlayer;
                }
            }
        }

        if (placementCount > 1 && nearest is not null)
        {
            var parts = new List<string>(4 + placementCount * 3)
            {
                nearest.SquadIndex.ToString("X4", CultureInfo.InvariantCulture),
                nearest.TeamAddress.ToString("X16", CultureInfo.InvariantCulture),
                3.ToString("X4", CultureInfo.InvariantCulture),
            };
            for (int index = 0; index < placementCount; index++)
            {
                parts.Add(nearest.ReferenceAddress.ToString("X16", CultureInfo.InvariantCulture));
                parts.Add(nearest.PositionAddress.ToString("X16", CultureInfo.InvariantCulture));
                parts.Add(nearest.VariantAddress.ToString("X16", CultureInfo.InvariantCulture));
            }
            parts.Add(Convert.ToHexString(_memory.BuildTagReference(choice.CharacterTag)));
            parts.Add(Convert.ToHexString(variant.StringIdBytes));
            if (weapon is not null)
                parts.Add(weapon.Datum.ToString("X8", CultureInfo.InvariantCulture));
            return AppendFormationOffset(
                string.Join(',', parts),
                formationOffsetX,
                formationOffsetY,
                followPlayer);
        }

        if (nearest is not null)
        {
            if (placementCount > 1)
                throw new InvalidOperationException(
                    $"No scenario squad has {placementCount} usable spawn points. " +
                    "Try this action in a larger encounter area or use Spawn ahead of player.");
            string payload = string.Create(
                CultureInfo.InvariantCulture,
                $"{nearest.SquadIndex:X4},{nearest.ReferenceAddress:X16}," +
                $"{nearest.PositionAddress:X16}," +
                $"{nearest.VariantAddress:X16}," +
                $"{Convert.ToHexString(_memory.BuildTagReference(choice.CharacterTag))}," +
                $"{Convert.ToHexString(variant.StringIdBytes)}");
            if (weapon is not null)
                payload += "," +
                    weapon.Datum.ToString("X8", CultureInfo.InvariantCulture);
            return AppendFormationOffset(
                payload,
                formationOffsetX,
                formationOffsetY,
                followPlayer);
        }

        throw new InvalidOperationException(
            (placementCount > 1
                ? $"No scenario squad has {placementCount} usable spawn points in the loaded mission. "
                : "No hostile scenario squad has a usable spawn point in the loaded mission area. ") +
            $"Inspected {squads.ChildCount:N0} squads: {hostileSquads:N0} hostile, " +
            $"{squadsWithSpawnPoints:N0} with spawn-point blocks, " +
            $"{inspectedSpawnPoints:N0} spawn points, and {indexedSpawnPoints:N0} with " +
            $"a direct character-palette index ({cellBasedSpawnPoints:N0} resolved through cells).");
    }

    private static string AppendFormationOffset(
        string payload,
        float x,
        float y,
        bool followPlayer)
    {
        if (x == 0 && y == 0 && !followPlayer)
            return payload;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{payload};{x:R};{y:R};{(followPlayer ? 1 : 0)}");
    }

    private short FindCellPaletteIndex(
        RuntimeTagEntry scenario,
        IReadOnlyList<RuntimeTagFieldValue> squad,
        short cellIndex)
    {
        foreach (RuntimeTagFieldValue cells in squad.Where(field =>
                     field.ChildBlockDefinition == "cell_block" &&
                     field.CanOpenBlock &&
                     cellIndex < field.ChildCount))
        {
            IReadOnlyList<RuntimeTagFieldValue> cell = ReadBlock(
                scenario, cells, cellIndex);
            RuntimeTagFieldValue? choices = cell.FirstOrDefault(field =>
                field.ChildBlockDefinition == "character_palette_choice_block" &&
                field.CanOpenBlock);
            if (choices is null) continue;

            for (int choiceIndex = 0;
                 choiceIndex < Math.Min(choices.ChildCount, 128);
                 choiceIndex++)
            {
                RuntimeTagFieldValue? characterType = ReadBlock(
                    scenario, choices, choiceIndex).FirstOrDefault(field =>
                        field.Type == "short_block_index" &&
                        field.Name.StartsWith(
                            "character type",
                            StringComparison.OrdinalIgnoreCase));
                if (characterType is null) continue;
                short paletteIndex = ReadInt16(characterType.Address);
                if (paletteIndex >= 0) return paletteIndex;
            }
        }
        return -1;
    }

    private static string CharacterFamily(string path)
    {
        string normalized = path.Replace('\\', '/');
        const string marker = "objects/characters/";
        int markerIndex = normalized.IndexOf(
            marker,
            StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return string.Empty;
        int familyStart = markerIndex + marker.Length;
        int familyEnd = normalized.IndexOf('/', familyStart);
        return familyEnd < 0
            ? normalized[familyStart..]
            : normalized[familyStart..familyEnd];
    }

    private RuntimeTagEntry? FindJohnsonCharacter() =>
        _tags
            .Where(tag =>
                string.Equals(tag.Group, "char", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0 &&
                tag.Name.Contains("johnson", StringComparison.OrdinalIgnoreCase) &&
                tag.Name.Contains(@"\ai\", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(tag =>
                string.Equals(
                    tag.LeafName,
                    "johnson",
                    StringComparison.OrdinalIgnoreCase))
            .ThenBy(tag => tag.Name.Length)
            .FirstOrDefault();

    private bool SquadFollowsPlayer(
        RuntimeTagEntry scenario,
        IReadOnlyList<RuntimeTagFieldValue> squad,
        RuntimeTagFieldValue objectives)
    {
        RuntimeTagFieldValue? objectiveField = squad.FirstOrDefault(field =>
            field.Type == "short_block_index" &&
            string.Equals(
                CleanFieldName(field.Name),
                "initial objective",
                StringComparison.OrdinalIgnoreCase));
        RuntimeTagFieldValue? taskField = squad.FirstOrDefault(field =>
            string.Equals(
                CleanFieldName(field.Name),
                "initial task",
                StringComparison.OrdinalIgnoreCase));
        if (objectiveField is null || taskField is null)
            return false;
        short objectiveIndex = ReadInt16(objectiveField.Address);
        short taskIndex = ReadInt16(taskField.Address);
        if (objectiveIndex < 0 || objectiveIndex >= objectives.ChildCount ||
            taskIndex < 0)
            return false;

        RuntimeTagFieldValue? tasks = ReadBlock(
            scenario,
            objectives,
            objectiveIndex).FirstOrDefault(field =>
                field.ChildBlockDefinition == "tasks_block" &&
                field.CanOpenBlock);
        if (tasks is null || taskIndex >= tasks.ChildCount)
            return false;
        IReadOnlyList<RuntimeTagFieldValue> task =
            ReadBlock(scenario, tasks, taskIndex);
        RuntimeTagFieldValue? follow = task.FirstOrDefault(field =>
            field.Type == "short_enum" &&
            string.Equals(
                CleanFieldName(field.Name),
                "follow",
                StringComparison.OrdinalIgnoreCase));
        if (follow is null)
            return false;
        short followMode = ReadInt16(follow.Address);
        return followMode is 1 or 3 or 4;
    }

    private IReadOnlyList<MemoryPatch> ApplyAuthoredSpartanShields(
        RuntimeTagEntry johnson)
    {
        RuntimeTagFieldValue? johnsonVitality = FindVitalityBlock(johnson);
        if (johnsonVitality is null)
            return [];
        IReadOnlyList<RuntimeTagFieldValue> johnsonFields =
            ReadBlock(johnson, johnsonVitality, 0);
        RuntimeTagFieldValue? currentShield = FindFieldByCleanName(
            johnsonFields,
            "normal shield vitality");
        if (currentShield is not null &&
            ReadSingle(currentShield.Address) > 0)
            return [];

        RuntimeTagEntry? donor = _tags
            .Where(tag =>
                tag.Index != johnson.Index &&
                string.Equals(tag.Group, "char", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0)
            .Select(tag => (Tag: tag, Vitality: FindVitalityBlock(tag)))
            .Where(candidate => candidate.Vitality is not null)
            .Select(candidate => (
                candidate.Tag,
                Fields: ReadBlock(candidate.Tag, candidate.Vitality!, 0)))
            .Where(candidate =>
            {
                RuntimeTagFieldValue? field = FindFieldByCleanName(
                    candidate.Fields,
                    "normal shield vitality");
                return field is not null && ReadSingle(field.Address) > 0;
            })
            .OrderByDescending(candidate => ShieldDonorScore(candidate.Tag.Name))
            .ThenBy(candidate => candidate.Tag.Name.Length)
            .Select(candidate => candidate.Tag)
            .FirstOrDefault();
        if (donor is null)
            return [];

        RuntimeTagFieldValue donorVitality =
            FindVitalityBlock(donor)
            ?? throw new InvalidDataException(
                "The selected shield donor no longer exposes vitality data.");
        IReadOnlyList<RuntimeTagFieldValue> donorFields =
            ReadBlock(donor, donorVitality, 0);
        string[] copiedFields =
        [
            "normal shield vitality",
            "legendary shield vitality",
            "shield recharge delay time",
            "shield recharge time",
        ];
        var patches = new List<MemoryPatch>();
        try
        {
            foreach (string fieldName in copiedFields)
            {
                RuntimeTagFieldValue? target =
                    FindFieldByCleanName(johnsonFields, fieldName);
                RuntimeTagFieldValue? source =
                    FindFieldByCleanName(donorFields, fieldName);
                if (target is null || source is null ||
                    target.Size <= 0 || target.Size != source.Size)
                    continue;
                byte[] original = _memory.ReadBytes(target.Address, target.Size);
                byte[] replacement = _memory.ReadBytes(source.Address, source.Size);
                if (original.AsSpan().SequenceEqual(replacement))
                    continue;
                _memory.WriteVerified(target.Address, replacement);
                patches.Add(new MemoryPatch(target.Address, original));
            }
            return patches;
        }
        catch
        {
            RestorePatches(patches);
            throw;
        }
    }

    private IReadOnlyList<MemoryPatch> ApplyAuthoredWeapon(
        RuntimeTagEntry character,
        RuntimeTagEntry weapon)
    {
        RuntimeTagFieldValue? weapons = ReadRoot(character).FirstOrDefault(field =>
            field.CanOpenBlock &&
            string.Equals(
                field.ChildBlockDefinition,
                "character_weapons_block",
                StringComparison.OrdinalIgnoreCase));
        if (weapons is null || weapons.ChildCount <= 0)
            throw new InvalidDataException(
                $"The character {character.Name} has no authored weapon slots.");

        byte[] replacement = _memory.BuildTagReference(weapon);
        var patches = new List<MemoryPatch>();
        try
        {
            for (int index = 0;
                 index < Math.Min(weapons.ChildCount, 100);
                 index++)
            {
                RuntimeTagFieldValue? reference = ReadBlock(
                    character,
                    weapons,
                    index).FirstOrDefault(field =>
                        field.IsTagReference &&
                        string.Equals(
                            CleanFieldName(field.Name),
                            "weapon",
                            StringComparison.OrdinalIgnoreCase));
                if (reference is null || reference.Size != replacement.Length)
                    continue;
                byte[] original = _memory.ReadBytes(
                    reference.Address,
                    reference.Size);
                if (original.AsSpan().SequenceEqual(replacement))
                    continue;
                _memory.WriteVerified(reference.Address, replacement);
                patches.Add(new MemoryPatch(reference.Address, original));
            }
            if (patches.Count == 0)
            {
                bool alreadySelected = Enumerable.Range(
                        0,
                        Math.Min(weapons.ChildCount, 100))
                    .Select(index => ReadBlock(character, weapons, index))
                    .SelectMany(fields => fields)
                    .Where(field =>
                        field.IsTagReference &&
                        string.Equals(
                            CleanFieldName(field.Name),
                            "weapon",
                            StringComparison.OrdinalIgnoreCase))
                    .Any(field => _memory.ReadBytes(field.Address, field.Size)
                        .AsSpan().SequenceEqual(replacement));
                if (!alreadySelected)
                    throw new InvalidDataException(
                        $"The character {character.Name} exposes no writable authored weapon reference.");
            }
            return patches;
        }
        catch
        {
            RestorePatches(patches);
            throw;
        }
    }

    private RuntimeTagFieldValue? FindVitalityBlock(RuntimeTagEntry character) =>
        ReadRoot(character).FirstOrDefault(field =>
            field.CanOpenBlock &&
            field.ChildCount > 0 &&
            string.Equals(
                field.ChildBlockDefinition,
                "character_vitality_block",
                StringComparison.OrdinalIgnoreCase));

    private static RuntimeTagFieldValue? FindFieldByCleanName(
        IEnumerable<RuntimeTagFieldValue> fields,
        string name) =>
        fields.FirstOrDefault(field =>
            string.Equals(
                CleanFieldName(field.Name),
                name,
                StringComparison.OrdinalIgnoreCase));

    private float ReadSingle(long address) =>
        BinaryPrimitives.ReadSingleLittleEndian(
            _memory.ReadBytes(address, sizeof(float)));

    private void RestorePatches(IEnumerable<MemoryPatch> patches)
    {
        foreach (MemoryPatch patch in patches.Reverse())
            _memory.WriteVerified(patch.Address, patch.Original);
    }

    private static int ShieldDonorScore(string path)
    {
        string value = path.Replace('\\', '/').ToLowerInvariant();
        if (value.Contains("masterchief", StringComparison.Ordinal) ||
            value.Contains("master_chief", StringComparison.Ordinal) ||
            value.Contains("spartan", StringComparison.Ordinal))
            return 3;
        if (value.Contains("elite", StringComparison.Ordinal))
            return 2;
        return 1;
    }

    private static bool IsSpartanCompatibleWeapon(string path)
    {
        string value = path.Replace('\\', '/').ToLowerInvariant();
        if (value.Contains("turret", StringComparison.Ordinal) ||
            value.Contains("mounted", StringComparison.Ordinal) ||
            value.Contains("grenade", StringComparison.Ordinal) ||
            value.Contains("equipment", StringComparison.Ordinal) ||
            value.Contains("bomb", StringComparison.Ordinal))
            return false;
        return new[]
        {
            "assault_rifle", "battle_rifle", "shotgun", "sniper",
            "smg", "rocket", "pistol", "magnum", "plasma_rifle",
            "plasma_pistol", "needler", "carbine", "beam_rifle",
            "brute_shot",
        }.Any(name => value.Contains(name, StringComparison.Ordinal));
    }

    private IReadOnlyList<AiWeaponChoice> ReadCompatibleWeapons(
        RuntimeTagEntry character)
    {
        RuntimeTagFieldValue? weapons = ReadRoot(character).FirstOrDefault(field =>
            field.CanOpenBlock &&
            string.Equals(
                field.ChildBlockDefinition,
                "character_weapons_block",
                StringComparison.OrdinalIgnoreCase));
        if (weapons is null)
            return [];

        var results = new List<AiWeaponChoice>();
        for (int index = 0;
             index < Math.Min(weapons.ChildCount, 100);
             index++)
        {
            RuntimeTagFieldValue? reference = ReadBlock(
                character,
                weapons,
                index).FirstOrDefault(field =>
                    field.IsTagReference &&
                    string.Equals(
                        CleanFieldName(field.Name),
                        "weapon",
                        StringComparison.OrdinalIgnoreCase));
            if (reference is null)
                continue;
            RuntimeTagEntry? weapon = _tags.FirstOrDefault(tag =>
                tag.Index == reference.ReferencedTagIndex &&
                string.Equals(tag.Group, "weap", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0);
            if (weapon is not null)
                results.Add(new AiWeaponChoice(weapon));
        }
        return results
            .GroupBy(item => item.WeaponTag.Index)
            .Select(group => group.First())
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<SpawnVariantChoice> ReadVariants(RuntimeTagEntry character)
    {
        var results = new List<SpawnVariantChoice>();
        IReadOnlyList<RuntimeTagFieldValue> root;
        try
        {
            root = ReadRoot(character);
        }
        catch
        {
            return results;
        }

        RuntimeTagFieldValue? variants = root.FirstOrDefault(field =>
            field.CanOpenBlock &&
            string.Equals(
                field.ChildBlockDefinition,
                "character_variants_block",
                StringComparison.OrdinalIgnoreCase));
        if (variants is null)
        {
            return [new SpawnVariantChoice("Authored default", new byte[4], -1, -1)];
        }

        for (int index = 0; index < Math.Min(variants.ChildCount, 128); index++)
        {
            IReadOnlyList<RuntimeTagFieldValue> fields;
            try
            {
                fields = ReadBlock(character, variants, index);
            }
            catch
            {
                continue;
            }
            RuntimeTagFieldValue? name = fields.FirstOrDefault(field =>
                field.Type == "string_id" &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "variant name",
                    StringComparison.OrdinalIgnoreCase));
            RuntimeTagFieldValue? variantIndex = fields.FirstOrDefault(field =>
                field.Type == "short_integer" &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "variant index",
                    StringComparison.OrdinalIgnoreCase));
            if (name is null || name.Size != 4 || variantIndex is null)
                continue;

            byte[] stringId = _memory.ReadBytes(name.Address, 4);
            short skinIndex = ReadInt16(variantIndex.Address);
            results.Add(new SpawnVariantChoice(
                skinIndex >= 0 ? $"Skin variant {skinIndex}" : "Authored default",
                stringId,
                skinIndex,
                index));
        }
        if (results.Count == 0)
            results.Add(new SpawnVariantChoice(
                "Authored default", new byte[4], -1, -1));
        return results
            .GroupBy(
                item => $"{item.StringId:X8}:{item.VariantIndex}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.VariantIndex)
            .ToArray();
    }

    private IReadOnlyList<ArmorSpawnChoice> ReadArmorChoices(out string status)
    {
        if (!_definitions.HasSchema("bipd") || !_definitions.HasSchema("hlmt"))
        {
            status = "The loaded tag definitions do not include [bipd] and [hlmt].";
            return [];
        }

        CustomizationCategory? armorCatalog = CustomizationCatalog.Categories
            .FirstOrDefault(category =>
                string.Equals(category.Group, "Armor", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    category.TagSegment,
                    "MasterChief",
                    StringComparison.OrdinalIgnoreCase));
        if (armorCatalog is null)
        {
            status = "Halo Meister's Master Chief armor catalog is unavailable.";
            return [];
        }

        var candidates = new List<(ArmorSpawnChoice Choice, int Score, string ModelPath)>();
        int usableBipeds = 0;
        int resolvedModels = 0;
        foreach (RuntimeTagEntry biped in _tags.Where(IsUsableBiped))
        {
            usableBipeds++;
            try
            {
                RuntimeTagFieldValue? modelReference = ReadRoot(biped)
                    .FirstOrDefault(field =>
                        field.IsTagReference &&
                        string.Equals(
                            CleanFieldName(field.Name),
                            "model",
                            StringComparison.OrdinalIgnoreCase));
                RuntimeTagEntry? model = modelReference is null
                    ? null
                    : _tags.FirstOrDefault(tag =>
                        tag.Index == modelReference.ReferencedTagIndex &&
                        string.Equals(tag.Group, "hlmt", StringComparison.OrdinalIgnoreCase) &&
                        tag.DataAddress > 0 &&
                        tag.RootCount > 0);
                if (model is null) continue;
                resolvedModels++;

                RuntimeTagFieldValue? variants = ReadRoot(model).FirstOrDefault(field =>
                    field.CanOpenBlock &&
                    string.Equals(
                        field.ChildBlockDefinition,
                        "model_variant_block",
                        StringComparison.OrdinalIgnoreCase));
                if (variants is null) continue;

                var choices = new List<SpawnVariantChoice>();
                foreach (CosmeticChoice cosmetic in armorCatalog.Choices)
                {
                    if (!CustomizationCatalog.TryGetMasterChiefModelVariantIndex(
                            cosmetic,
                            out int index) ||
                        index < 0 ||
                        index >= variants.ChildCount)
                        continue;
                    RuntimeTagFieldValue? name = ReadBlock(model, variants, index)
                        .FirstOrDefault(field =>
                            field.Type == "string_id" &&
                            string.Equals(
                                CleanFieldName(field.Name),
                                "name",
                                StringComparison.OrdinalIgnoreCase));
                    if (name?.Size != sizeof(uint)) continue;
                    choices.Add(new SpawnVariantChoice(
                        cosmetic.Name,
                        _memory.ReadBytes(name.Address, name.Size),
                        checked((short)index),
                        index,
                        cosmetic.ImageUri));
                }
                if (choices.Count == 0) continue;

                int score =
                    SpartanNameScore(biped.Name) +
                    SpartanNameScore(model.Name) +
                    Math.Min(choices.Count, 40);
                if (string.Equals(
                        biped.Name.Replace('/', '\\'),
                        @"objects\characters\spartans\spartans",
                        StringComparison.OrdinalIgnoreCase))
                    score += 1000;
                if (score <= choices.Count) continue;
                candidates.Add((
                    new ArmorSpawnChoice(biped, choices),
                    score,
                    model.Name));
            }
            catch
            {
                // One malformed or partially published biped must not hide a
                // usable Spartan model elsewhere in the live tag table.
            }
        }

        (ArmorSpawnChoice Choice, int Score, string ModelPath)[] ranked = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Choice.Variants.Count)
            .ThenBy(candidate => candidate.Choice.TagPath.Length)
            .ToArray();
        if (ranked.Length == 0)
        {
            status =
                $"Scanned {usableBipeds:N0} usable [bipd] tags and resolved " +
                $"{resolvedModels:N0} [hlmt] models, but none exposed a recognized " +
                "Master Chief/Spartan armor model.";
            return [];
        }

        (ArmorSpawnChoice Choice, int Score, string ModelPath) selected = ranked[0];
        status =
            $"Resolved {selected.Choice.Variants.Count:N0} armor variants from " +
            $"{selected.Choice.TagPath} -> {selected.ModelPath}.";
        return [selected.Choice];
    }

    private static bool IsUsableBiped(RuntimeTagEntry tag) =>
        string.Equals(tag.Group, "bipd", StringComparison.OrdinalIgnoreCase) &&
        tag.DataAddress > 0 &&
        tag.RootCount > 0 &&
        !tag.Name.Contains(@"\stimuli\", StringComparison.OrdinalIgnoreCase) &&
        !tag.Name.Contains("/stimuli/", StringComparison.OrdinalIgnoreCase);

    private static int SpartanNameScore(string name)
    {
        string value = name.Replace('\\', '/').ToLowerInvariant();
        int score = 0;
        if (value.Contains("masterchief", StringComparison.Ordinal) ||
            value.Contains("master_chief", StringComparison.Ordinal)) score += 100;
        if (value.Contains("chief", StringComparison.Ordinal)) score += 60;
        if (value.Contains("player", StringComparison.Ordinal)) score += 40;
        if (value.Contains("spartan", StringComparison.Ordinal)) score += 30;
        return score;
    }

    private short ReadInt16(long address) =>
        BinaryPrimitives.ReadInt16LittleEndian(_memory.ReadBytes(address, 2));

    private WorldPoint ReadPoint(long address)
    {
        byte[] bytes = _memory.ReadBytes(address, 12);
        return new WorldPoint(
            BinaryPrimitives.ReadSingleLittleEndian(bytes),
            BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(4)),
            BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(8)));
    }

    private IReadOnlyList<RuntimeTagFieldValue> ReadRoot(RuntimeTagEntry tag) =>
        _definitions.ReadRootFields(
            tag.Group,
            tag.DataAddress,
            _memory.ReadBytes,
            Resolve);

    private IReadOnlyList<RuntimeTagFieldValue> ReadBlock(
        RuntimeTagEntry tag,
        RuntimeTagFieldValue block,
        int index) =>
        _definitions.ReadBlockFields(
            tag.Group,
            block.ChildBlockDefinition!,
            block.ChildAddress,
            index,
            _memory.ReadBytes,
            Resolve);

    private long? Resolve(uint encoded) =>
        _memory.TryResolveOffset(encoded, out long address) ? address : null;

    private static string CleanFieldName(string name)
    {
        int description = name.IndexOfAny(['#', '{', ':', '^', '*', '!', '~']);
        string value = description >= 0 ? name[..description] : name;
        int path = value.LastIndexOf('/');
        return (path >= 0 ? value[(path + 1)..] : value).Trim();
    }

    public void Dispose() { }

    private sealed record SpawnTemplate(
        int SquadIndex,
        long TeamAddress,
        long ReferenceAddress,
        long PositionAddress,
        long VariantAddress,
        double DistanceSquared);

    private sealed record MemoryPatch(long Address, byte[] Original);

    private readonly record struct WorldPoint(float X, float Y, float Z);
}
