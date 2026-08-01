using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using HaloMeister.App.Localization;
using HaloMeister.App.Models;

namespace HaloMeister.App.Services;

public sealed record ScenarioSquadInfo(
    int Index,
    string Name,
    string ScriptName,
    uint Flags,
    IReadOnlyList<string> FlagNames,
    string FlagsHex,
    short TeamIndex,
    string TeamDisplay,
    short ParentIndex,
    string ParentDisplay,
    short InitialZoneIndex,
    string InitialZoneDisplay,
    short InitialObjectiveIndex,
    string InitialObjectiveDisplay,
    short InitialTask,
    string InitialTaskDisplay,
    short EditorFolderIndex,
    string EditorFolderDisplay,
    int SpawnPointCount,
    int SpawnFormationCount)
{
    public string ListTitle => $"{Index}. {Name}";
    public string SearchText =>
        $"{Index} {Name} {ScriptName} {TeamDisplay} {ParentDisplay} {InitialObjectiveDisplay}";
    public bool CanScript => IsValidHsAiName(ScriptName);

    internal static bool IsValidHsAiName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        char first = name[0];
        if (!(char.IsAsciiLetter(first) || first == '_'))
            return false;
        for (int i = 1; i < name.Length; i++)
        {
            char c = name[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
                return false;
        }
        return true;
    }
}

public sealed record ScenarioSquadsSession(
    string ScenarioPath,
    IReadOnlyList<ScenarioSquadInfo> Squads);

public sealed class ScenarioSquadsService : IDisposable
{
    private static readonly string[] SquadFlagNames =
    [
        "blind",
        "deaf",
        "braindead",
        "initially placed",
        "units not enterable by player",
        "fireteam absorber",
        "squad is runtime(DO NOT USE)",
        "no wave spawn",
    ];

    private static readonly string[] CampaignTeamNames =
    [
        "default",
        "player",
        "human",
        "covenant",
        "brute",
        "mule",
        "spare",
        "covenant_player",
        "flood",
        "sentinel",
        "heretic",
        "prophet",
        "guilty",
        "berserk_hostile_to_all",
        "unused14",
        "unused15",
    ];

    private readonly RuntimeTagMemoryService _memory = RuntimeTagMemoryService.Current;
    private readonly RuntimeTagDefinitionService _definitions = new();
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private readonly Dictionary<uint, string> _stringIdNames = new();

    public async Task<ScriptExecutionResult> PlaceAsync(
        ScenarioSquadInfo squad,
        CancellationToken cancellationToken = default) =>
        await RunHaloScriptAsync(
            squad,
            "ai_place",
            cancellationToken);

    public async Task<ScriptExecutionResult> EraseAsync(
        ScenarioSquadInfo squad,
        CancellationToken cancellationToken = default) =>
        await RunHaloScriptAsync(
            squad,
            "ai_erase",
            cancellationToken);

    private async Task<ScriptExecutionResult> RunHaloScriptAsync(
        ScenarioSquadInfo squad,
        string command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(squad);
        if (!_memory.IsConnected)
            throw new InvalidOperationException(
                L.Get("squads.error_connect_game_first"));
        if (!squad.CanScript)
            throw new InvalidOperationException(
                L.Get("squads.error_invalid_script_name"));

        ScriptingBridgeStatus status = _bridge.GetStatus();
        if (!status.IsRuntimeReady)
            throw new InvalidOperationException(
                L.Get("bridge.error_scripting_not_responding"));
        if (status.IsStale)
            throw new InvalidOperationException(status.Summary);

        string expression = $"{command} {squad.ScriptName}";
        return await _bridge.ExecuteAsync(
            ScriptLanguage.HaloScript,
            expression,
            TimeSpan.FromSeconds(15),
            cancellationToken);
    }

    public ScenarioSquadsSession Scan()
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException(
                L.Get("squads.error_connect_game_first"));

        EnsureDefinitions();
        IReadOnlyList<RuntimeTagEntry> tags = _memory.ReadTags();
        RuntimeTagEntry scenario = tags.FirstOrDefault(tag =>
                string.Equals(tag.Group, "scnr", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0)
            ?? throw new InvalidDataException(
                L.Get("squads.error_no_scenario"));

        IReadOnlyList<RuntimeTagFieldValue> root = ReadRoot(scenario);
        RuntimeTagFieldValue squads = root.FirstOrDefault(field =>
                field.Type == "block" &&
                string.Equals(
                    field.ChildBlockDefinition,
                    "squads_block",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                L.Get("squads.error_no_squads_block"));
        if (squads.ChildCount < 0 ||
            squads.ChildCount > 512 ||
            (squads.ChildCount > 0 &&
             (squads.ChildAddress <= 0 || squads.ChildBlockDefinition is null)))
        {
            throw new InvalidDataException(
                L.Get("squads.error_invalid_squads_block"));
        }

        RuntimeTagFieldValue? groups = FindBlock(root, "squad_groups_block");
        RuntimeTagFieldValue? zones = FindBlock(root, "zone_block");
        RuntimeTagFieldValue? objectives = FindBlock(root, "objectives_block");
        RuntimeTagFieldValue? folders = FindBlock(root, "g_scenario_editor_folder_block");

        var result = new List<ScenarioSquadInfo>(squads.ChildCount);
        for (int index = 0; index < squads.ChildCount; index++)
        {
            IReadOnlyList<RuntimeTagFieldValue> fields = ReadBlock(
                scenario, squads, index);

            string scriptName = ReadStringField(fields, "name");
            string name = string.IsNullOrWhiteSpace(scriptName)
                ? L.Format("squads.unnamed", index)
                : scriptName;

            uint flags = ReadUInt32Field(fields, "flags", "long_flags");
            short team = ReadInt16Field(fields, "team");
            short parent = ReadInt16Field(fields, "parent");
            short zone = ReadInt16Field(fields, "initial zone");
            short objective = ReadInt16Field(fields, "initial objective");
            short task = ReadInt16Field(fields, "initial task");
            short folder = ReadInt16Field(fields, "editor folder");

            RuntimeTagFieldValue? spawnPoints = fields.FirstOrDefault(field =>
                field.Type == "block" &&
                string.Equals(
                    field.ChildBlockDefinition,
                    "spawn_points_block",
                    StringComparison.OrdinalIgnoreCase));
            RuntimeTagFieldValue? formations = fields.FirstOrDefault(field =>
                field.Type == "block" &&
                string.Equals(
                    field.ChildBlockDefinition,
                    "spawn_formation_block",
                    StringComparison.OrdinalIgnoreCase));

            result.Add(new ScenarioSquadInfo(
                index,
                name,
                scriptName,
                flags,
                DecodeFlags(flags),
                $"0x{flags:X4}",
                team,
                FormatTeam(team),
                parent,
                FormatNamedIndex(scenario, groups, parent),
                zone,
                FormatNamedIndex(scenario, zones, zone),
                objective,
                FormatNamedIndex(scenario, objectives, objective),
                task,
                task < 0 ? L.Get("squads.none") : task.ToString(CultureInfo.InvariantCulture),
                folder,
                FormatNamedIndex(scenario, folders, folder),
                spawnPoints?.ChildCount ?? 0,
                formations?.ChildCount ?? 0));
        }

        return new ScenarioSquadsSession(scenario.Name, result);
    }

    public void Dispose() { }

    private void EnsureDefinitions()
    {
        if (_definitions.SchemaCount == 0)
            _definitions.LoadDirectory(
                RuntimeTagDefinitionLocator.ResolveCampaignEvolved());
        if (!_definitions.HasSchema("scnr"))
            throw new InvalidDataException(
                L.Get("squads.error_no_scnr_schema"));
    }

    private static RuntimeTagFieldValue? FindBlock(
        IReadOnlyList<RuntimeTagFieldValue> root,
        string definition) =>
        root.FirstOrDefault(field =>
            field.Type == "block" &&
            string.Equals(
                field.ChildBlockDefinition,
                definition,
                StringComparison.OrdinalIgnoreCase));

    private string FormatNamedIndex(
        RuntimeTagEntry scenario,
        RuntimeTagFieldValue? block,
        short index)
    {
        if (index < 0)
            return L.Get("squads.none");
        if (block is null ||
            block.ChildBlockDefinition is null ||
            block.ChildCount <= 0 ||
            block.ChildAddress <= 0 ||
            index >= block.ChildCount)
        {
            return $"{index}";
        }

        IReadOnlyList<RuntimeTagFieldValue> fields = ReadBlock(scenario, block, index);
        RuntimeTagFieldValue? nameField = fields.FirstOrDefault(field =>
            string.Equals(
                CleanFieldName(field.Name),
                "name",
                StringComparison.OrdinalIgnoreCase));
        if (nameField is null)
            return $"{index}";

        string label = nameField.Type switch
        {
            "string" or "long_string" => ReadCString(nameField.Address, nameField.Size),
            "string_id" => ResolveStringIdLabel(nameField),
            _ => nameField.Value,
        };
        if (string.IsNullOrWhiteSpace(label))
            return $"{index}";
        return $"{index}. {label}";
    }

    private string ResolveStringIdLabel(RuntimeTagFieldValue field)
    {
        if (field.Size != sizeof(uint))
            return field.Value;
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(
            _memory.ReadBytes(field.Address, sizeof(uint)));
        if (id == 0 || id == uint.MaxValue)
            return L.Get("squads.none");
        if (_stringIdNames.TryGetValue(id, out string? cached))
            return cached;
        if (_memory.TryGetStringIdName(id, out string? name) &&
            !string.IsNullOrWhiteSpace(name))
        {
            _stringIdNames[id] = name;
            return name;
        }
        string fallback = $"0x{id:X8}";
        _stringIdNames[id] = fallback;
        return fallback;
    }

    private static IReadOnlyList<string> DecodeFlags(uint flags)
    {
        var names = new List<string>();
        for (int bit = 0; bit < SquadFlagNames.Length; bit++)
        {
            if ((flags & (1u << bit)) != 0)
                names.Add(SquadFlagNames[bit]);
        }
        return names;
    }

    private static string FormatTeam(short teamIndex)
    {
        if (teamIndex < 0)
            return L.Get("squads.none");
        if (teamIndex < CampaignTeamNames.Length)
            return $"{teamIndex}. {CampaignTeamNames[teamIndex]}";
        return teamIndex.ToString(CultureInfo.InvariantCulture);
    }

    private static string ReadStringField(
        IReadOnlyList<RuntimeTagFieldValue> fields,
        string name)
    {
        RuntimeTagFieldValue? field = fields.FirstOrDefault(item =>
            (item.Type is "string" or "long_string") &&
            string.Equals(
                CleanFieldName(item.Name),
                name,
                StringComparison.OrdinalIgnoreCase));
        if (field is null) return "";
        return field.Value.Trim('\0', ' ');
    }

    private uint ReadUInt32Field(
        IReadOnlyList<RuntimeTagFieldValue> fields,
        string name,
        string type)
    {
        RuntimeTagFieldValue? field = fields.FirstOrDefault(item =>
            string.Equals(item.Type, type, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                CleanFieldName(item.Name),
                name,
                StringComparison.OrdinalIgnoreCase));
        if (field is null || field.Size < sizeof(uint))
            return 0;
        return BinaryPrimitives.ReadUInt32LittleEndian(
            _memory.ReadBytes(field.Address, sizeof(uint)));
    }

    private short ReadInt16Field(
        IReadOnlyList<RuntimeTagFieldValue> fields,
        string name)
    {
        RuntimeTagFieldValue? field = fields.FirstOrDefault(item =>
            (item.Type is "short_enum" or "short_block_index" or
                "custom_short_block_index" or "short_integer") &&
            string.Equals(
                CleanFieldName(item.Name),
                name,
                StringComparison.OrdinalIgnoreCase));
        if (field is null || field.Size < sizeof(short))
            return -1;
        return BinaryPrimitives.ReadInt16LittleEndian(
            _memory.ReadBytes(field.Address, sizeof(short)));
    }

    private string ReadCString(long address, int size)
    {
        if (address <= 0 || size <= 0) return "";
        byte[] bytes = _memory.ReadBytes(address, size);
        int zero = Array.IndexOf(bytes, (byte)0);
        int length = zero >= 0 ? zero : bytes.Length;
        return Encoding.UTF8.GetString(bytes, 0, length).Trim();
    }

    private IReadOnlyList<RuntimeTagFieldValue> ReadRoot(RuntimeTagEntry tag) =>
        _definitions.ReadRootFields(
            tag.Group,
            tag.DataAddress,
            _memory.ReadBytes,
            ResolveOrNull);

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
            ResolveOrNull);

    private long? ResolveOrNull(uint encoded) =>
        _memory.TryResolveOffset(encoded, out long address) ? address : null;

    private static string CleanFieldName(string name)
    {
        int description = name.IndexOfAny(['#', '{', ':', '^', '*', '!', '~']);
        string value = description >= 0 ? name[..description] : name;
        int path = value.LastIndexOf('/');
        return (path >= 0 ? value[(path + 1)..] : value).Trim();
    }
}
