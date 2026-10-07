using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using HaloMeister.App.Localization;
using HaloMeister.App.Models;

namespace HaloMeister.App.Services;

public enum ScenarioPropKind
{
    Scenery = 6,
    Machine = 7,
    Crate = 10,
}

public sealed record ScenarioPropPlacement(
    ScenarioPropKind Kind,
    string ScriptName,
    string TagPath,
    string TagGroup,
    bool HasPosition,
    float X,
    float Y,
    float Z)
{
    public string PositionDisplay =>
        HasPosition
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"X={X:0.###}  Y={Y:0.###}  Z={Z:0.###}")
            : L.Get("scenario_props.no_position");

    public string SearchText =>
        $"{ScriptName} {TagPath} {TagGroup} {PositionDisplay} {ScenarioPropGroup.KindLabel(Kind)}";
}

public sealed class ScenarioPropGroup
{
    public ScenarioPropGroup(
        ScenarioPropKind kind,
        string tagPath,
        string tagGroup,
        IReadOnlyList<ScenarioPropPlacement> placements)
    {
        Kind = kind;
        TagPath = tagPath;
        TagGroup = tagGroup;
        Placements = placements;
        string path = string.IsNullOrWhiteSpace(tagPath)
            ? L.Get("scenario_props.unknown_tag")
            : tagPath;
        string group = string.IsNullOrWhiteSpace(tagGroup) ? "" : $"[{tagGroup}]  ";
        Title = $"{KindLabel(kind)}  {group}{path}";
        Detail = L.Format("scenario_props.group_count", placements.Count);
        Key = $"{(int)kind}|{tagPath}";
        SearchText = Title + " " + string.Join(
            ' ',
            placements.Select(item => item.SearchText));
    }

    public ScenarioPropKind Kind { get; }
    public string TagPath { get; }
    public string TagGroup { get; }
    public IReadOnlyList<ScenarioPropPlacement> Placements { get; }
    public string Title { get; }
    public string Detail { get; }
    public string Key { get; }
    public string SearchText { get; }

    public static string KindLabel(ScenarioPropKind kind) => kind switch
    {
        ScenarioPropKind.Crate => L.Get("scenario_props.kind_crate"),
        ScenarioPropKind.Machine => L.Get("scenario_props.kind_machine"),
        _ => L.Get("scenario_props.kind_scenery"),
    };

    public static IReadOnlyList<ScenarioPropGroup> FromPlacements(
        IReadOnlyList<ScenarioPropPlacement> placements) =>
        placements
            .GroupBy(item => (item.Kind, item.TagPath))
            .OrderBy(group => group.Key.Kind)
            .ThenBy(group => group.Key.TagPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ScenarioPropGroup(
                group.Key.Kind,
                group.Key.TagPath,
                group.Select(item => item.TagGroup)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "",
                group.OrderBy(item => item.ScriptName, StringComparer.OrdinalIgnoreCase).ToArray()))
            .ToArray();
}

public sealed record ScenarioPropsSession(
    string ScenarioName,
    IReadOnlyList<ScenarioPropPlacement> Placements,
    int SkippedCount);

public sealed record ScenarioPropScriptSubmission(
    string Script,
    ScriptExecutionResult Result,
    string BridgeReport);

/// <summary>
/// Reads named crate, scenery, and device-machine placements from the loaded
/// scenario. Hiding hides the object and disables its collision; showing
/// restores both.
/// </summary>
public sealed class ScenarioPropsService(RuntimeTagMemoryService memory)
{
    private const int NameEntrySize = 8;
    private const int PaletteEntrySize = 16;
    private const int SceneryElementSize = 216;
    private const int CrateElementSize = 216;
    private const int MachineElementSize = 228;
    private const int PaletteIndexOffset = 0;
    private const int PositionOffset = 8;
    private const int InstanceHeaderBytes = 20;
    private const int MaxNames = 2048;
    private const int MaxScenery = 2000;
    private const int MaxCrates = 1536;
    private const int MaxMachines = 400;
    private const int MaxPalette = 256;
    private const int ScriptBatchBytes = 48 * 1024;

    private readonly RuntimeTagMemoryService _memory = memory;
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private readonly RuntimeTagDefinitionService _definitions = new();
    private readonly Dictionary<int, IReadOnlyList<RuntimeTagFieldValue>> _roots = [];
    private readonly Dictionary<(int TagIndex, string Definition, int Index), (string Path, string Group)> _palettes = [];

    public ScenarioPropsSession Scan()
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException(L.Get("scenario_props.error_connect"));

        EnsureDefinitions();
        _roots.Clear();
        _palettes.Clear();

        IReadOnlyList<RuntimeTagEntry> tags = _memory.ReadTags();
        Dictionary<int, RuntimeTagEntry> tagsByIndex = tags
            .GroupBy(tag => tag.Index)
            .ToDictionary(group => group.Key, group => group.First());

        RuntimeTagEntry[] scenarios = FindTags(tags, "scnr");
        if (scenarios.Length == 0)
            throw new InvalidDataException(L.Get("scenario_props.error_no_scenario"));

        RuntimeTagEntry[] sceneryResources = FindTags(tags, "*cen");
        RuntimeTagEntry[] deviceResources = FindTags(tags, "dgr*");

        LoadedBlock[] scenery = Prefer(
            Blocks(sceneryResources, "scenario_scenery_block", ["scenery", "scenerys"], MaxScenery, SceneryElementSize),
            Blocks(scenarios, "scenario_scenery_block", ["scenery", "scenerys"], MaxScenery, SceneryElementSize));
        LoadedBlock[] crates = Prefer(
            Blocks(sceneryResources, "scenario_crate_block", ["crates"], MaxCrates, CrateElementSize),
            Blocks(scenarios, "scenario_crate_block", ["crates"], MaxCrates, CrateElementSize));
        LoadedBlock[] machines = Prefer(
            Blocks(deviceResources, "scenario_machine_block", ["machines"], MaxMachines, MachineElementSize),
            Blocks(scenarios, "scenario_machine_block", ["machines"], MaxMachines, MachineElementSize));

        var candidates = new List<ScenarioPropPlacement>();
        int skipped = 0;
        foreach (RuntimeTagEntry source in sceneryResources.Concat(deviceResources).Concat(scenarios))
        {
            LoadedBlock? names = FindLoadedBlock(
                source,
                "scenario_object_names_block",
                ["names", "object names"],
                MaxNames,
                NameEntrySize);
            if (names is null) continue;

            byte[] raw = _memory.ReadBytes(names.Address, names.Count * NameEntrySize);
            for (int index = 0; index < names.Count; index++)
            {
                int offset = index * NameEntrySize;
                uint stringId = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset, 4));
                short objectType = BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(offset + 4, 2));
                short datumIndex = BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(offset + 6, 2));
                if (!TryKind(objectType, out ScenarioPropKind kind))
                    continue;
                if (!TryScriptName(stringId, out string scriptName))
                {
                    skipped++;
                    continue;
                }

                LoadedBlock[] instances = kind switch
                {
                    ScenarioPropKind.Crate => crates,
                    ScenarioPropKind.Machine => machines,
                    _ => scenery,
                };
                ReadInstance(
                    source,
                    instances,
                    kind,
                    datumIndex,
                    tagsByIndex,
                    out string tagPath,
                    out string tagGroup,
                    out bool hasPosition,
                    out float x,
                    out float y,
                    out float z);
                candidates.Add(new ScenarioPropPlacement(
                    kind,
                    scriptName,
                    tagPath,
                    tagGroup,
                    hasPosition,
                    x,
                    y,
                    z));
            }
        }

        ScenarioPropPlacement[] placements = candidates
            .GroupBy(item => item.ScriptName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => !string.IsNullOrWhiteSpace(item.TagPath))
                .ThenByDescending(item => item.HasPosition)
                .First())
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.TagPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ScriptName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ScenarioPropsSession(scenarios[0].Name, placements, skipped);
    }

    public async Task<ScenarioPropScriptSubmission> SetHiddenAsync(
        IReadOnlyList<string> scriptNames,
        bool hidden,
        CancellationToken cancellationToken = default)
    {
        if (scriptNames is null || scriptNames.Count == 0)
            throw new InvalidOperationException(L.Get("scenario_props.error_no_selection"));

        foreach (string name in scriptNames)
        {
            if (!ScenarioSquadInfo.IsValidHsAiName(name))
                throw new InvalidOperationException(
                    L.Format("scenario_props.error_invalid_name", name));
        }

        string script = BuildHideScript(scriptNames, hidden);
        ScriptExecutionResult? result = null;
        var reports = new List<string>();
        foreach (string batch in SplitScript(script))
        {
            result = await _bridge.ExecuteAsync(
                ScriptLanguage.HaloScript,
                batch,
                TimeSpan.FromSeconds(30),
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.Message))
                reports.Add(result.Message);
            if (result.Outcome == ScriptOutcome.Failed)
                break;
        }

        if (result is null)
            throw new InvalidOperationException(L.Get("scenario_props.error_no_selection"));

        return new ScenarioPropScriptSubmission(
            script,
            result,
            string.Join("\n\n", reports));
    }

    public static string BuildHideScript(IReadOnlyList<string> scriptNames, bool hidden)
    {
        string flag = hidden ? "true" : "false";
        string collision = hidden ? "false" : "true";
        return string.Join(
            '\n',
            scriptNames.SelectMany(name => new[]
            {
                $"(object_hide {name} {flag})",
                $"(object_cinematic_collision {name} {collision})",
            }));
    }

    private void EnsureDefinitions()
    {
        if (_definitions.SchemaCount == 0)
            _definitions.LoadDirectory(RuntimeTagDefinitionLocator.ResolveCampaignEvolved());
        if (!_definitions.HasSchema("scnr"))
            throw new InvalidDataException(L.Get("scenario_props.error_no_schema"));
    }

    private static RuntimeTagEntry[] FindTags(IReadOnlyList<RuntimeTagEntry> tags, string group) =>
        tags.Where(tag =>
                tag.DataAddress > 0 &&
                string.Equals(tag.Group, group, StringComparison.OrdinalIgnoreCase))
            .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(tag => tag.Index)
            .ToArray();

    private LoadedBlock[] Blocks(
        IReadOnlyList<RuntimeTagEntry> tags,
        string definition,
        string[] names,
        int maxCount,
        int elementSize)
    {
        var blocks = new List<LoadedBlock>();
        foreach (RuntimeTagEntry tag in tags)
        {
            LoadedBlock? block = FindLoadedBlock(tag, definition, names, maxCount, elementSize);
            if (block is not null)
                blocks.Add(block);
        }
        return blocks.ToArray();
    }

    private static LoadedBlock[] Prefer(LoadedBlock[] resources, LoadedBlock[] scenario)
    {
        LoadedBlock[] populated = resources.Where(block => block.Count > 0).ToArray();
        if (populated.Length > 0) return populated;
        return scenario.Where(block => block.Count > 0).ToArray();
    }

    private LoadedBlock? FindLoadedBlock(
        RuntimeTagEntry tag,
        string definition,
        string[] names,
        int maxCount,
        int elementSize)
    {
        if (tag.DataAddress <= 0 || !_definitions.HasSchema(tag.Group))
            return null;

        IReadOnlyList<RuntimeTagFieldValue> root = Root(tag);
        RuntimeTagFieldValue? field = root
            .Where(item =>
                item.Type == "block" &&
                string.Equals(item.ChildBlockDefinition, definition, StringComparison.OrdinalIgnoreCase) &&
                names.Contains(CleanFieldName(item.Name), StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(item => item.ChildCount)
            .FirstOrDefault();
        field ??= root
            .Where(item =>
                item.Type == "block" &&
                string.Equals(item.ChildBlockDefinition, definition, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.ChildCount)
            .FirstOrDefault();
        if (field is null || field.ChildCount == 0)
            return null;
        if (field.ChildCount < 0 ||
            field.ChildCount > maxCount ||
            field.ChildAddress <= 0 ||
            field.ChildElementSize != elementSize)
        {
            throw new InvalidDataException(
                L.Format("scenario_props.error_invalid_block", definition, tag.Name));
        }

        return new LoadedBlock(tag, field.ChildAddress, field.ChildCount, field.ChildElementSize);
    }

    private void ReadInstance(
        RuntimeTagEntry nameSource,
        IReadOnlyList<LoadedBlock> instances,
        ScenarioPropKind kind,
        short datumIndex,
        IReadOnlyDictionary<int, RuntimeTagEntry> tagsByIndex,
        out string tagPath,
        out string tagGroup,
        out bool hasPosition,
        out float x,
        out float y,
        out float z)
    {
        tagPath = "";
        tagGroup = ExpectedGroup(kind);
        hasPosition = false;
        x = y = z = 0;
        LoadedBlock? instance = ResolveInstance(nameSource, instances, datumIndex);
        if (instance is null) return;

        byte[] header = _memory.ReadBytes(
            instance.Address + (long)datumIndex * instance.ElementSize,
            InstanceHeaderBytes);
        short paletteIndex = BinaryPrimitives.ReadInt16LittleEndian(
            header.AsSpan(PaletteIndexOffset, 2));
        hasPosition = TryReadPosition(header, out x, out y, out z);
        (tagPath, string resolvedGroup) = ResolvePalette(instance.Tag, kind, paletteIndex, tagsByIndex);
        if (!string.IsNullOrWhiteSpace(resolvedGroup))
            tagGroup = resolvedGroup;
    }

    private static LoadedBlock? ResolveInstance(
        RuntimeTagEntry nameSource,
        IReadOnlyList<LoadedBlock> instances,
        short datumIndex)
    {
        if (datumIndex < 0) return null;
        LoadedBlock? local = instances.FirstOrDefault(block =>
            block.Tag.Index == nameSource.Index && datumIndex < block.Count);
        if (local is not null) return local;
        return instances.FirstOrDefault(block => datumIndex < block.Count);
    }

    private (string Path, string Group) ResolvePalette(
        RuntimeTagEntry owner,
        ScenarioPropKind kind,
        short paletteIndex,
        IReadOnlyDictionary<int, RuntimeTagEntry> tagsByIndex)
    {
        if (paletteIndex < 0) return ("", ExpectedGroup(kind));
        string definition = kind switch
        {
            ScenarioPropKind.Crate => "scenario_crate_palette_block",
            ScenarioPropKind.Machine => "scenario_machine_palette_block",
            _ => "scenario_scenery_palette_block",
        };
        var cacheKey = (owner.Index, definition, (int)paletteIndex);
        if (_palettes.TryGetValue(cacheKey, out (string Path, string Group) cached))
            return cached;

        (string Path, string Group) resolved = ("", ExpectedGroup(kind));
        LoadedBlock? palette = FindLoadedBlock(
            owner,
            definition,
            kind switch
            {
                ScenarioPropKind.Crate => ["crate palette"],
                ScenarioPropKind.Machine => ["machine palette"],
                _ => ["scenery palette"],
            },
            MaxPalette,
            PaletteEntrySize);
        if (palette is not null && paletteIndex < palette.Count)
        {
            byte[] reference = _memory.ReadBytes(
                palette.Address + (long)paletteIndex * PaletteEntrySize,
                PaletteEntrySize);
            resolved = ReadTagReference(reference, tagsByIndex, ExpectedGroup(kind));
        }

        _palettes[cacheKey] = resolved;
        return resolved;
    }

    private (string Path, string Group) ReadTagReference(
        byte[] reference,
        IReadOnlyDictionary<int, RuntimeTagEntry> tagsByIndex,
        string fallbackGroup)
    {
        string group = ReadGroup(reference);
        if (string.IsNullOrWhiteSpace(group))
            group = fallbackGroup;

        ushort index = BinaryPrimitives.ReadUInt16LittleEndian(reference.AsSpan(12, 2));
        uint datum = BinaryPrimitives.ReadUInt32LittleEndian(reference.AsSpan(12, 4));
        if (datum != uint.MaxValue &&
            index != ushort.MaxValue &&
            tagsByIndex.TryGetValue(index, out RuntimeTagEntry? tag))
        {
            return (tag.Name, string.IsNullOrWhiteSpace(tag.Group) ? group : tag.Group);
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(reference.AsSpan(8, 4));
        uint nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(reference.AsSpan(4, 4));
        if (length is > 0 and <= 1024 &&
            _memory.TryResolveOffset(nameOffset, out long address) &&
            address > 0)
        {
            return (ReadCString(address, length), group);
        }

        return ("", group);
    }

    private bool TryScriptName(uint stringId, out string name)
    {
        name = "";
        if (stringId == 0 || stringId == uint.MaxValue)
            return false;
        if (!_memory.TryGetStringIdName(stringId, out string? resolved) ||
            string.IsNullOrWhiteSpace(resolved))
        {
            return false;
        }

        name = resolved.Trim();
        return ScenarioSquadInfo.IsValidHsAiName(name);
    }

    private static bool TryKind(short objectType, out ScenarioPropKind kind)
    {
        kind = (ScenarioPropKind)objectType;
        return objectType is (short)ScenarioPropKind.Scenery
            or (short)ScenarioPropKind.Machine
            or (short)ScenarioPropKind.Crate;
    }

    private static string ExpectedGroup(ScenarioPropKind kind) => kind switch
    {
        ScenarioPropKind.Crate => "bloc",
        ScenarioPropKind.Machine => "mach",
        _ => "scen",
    };

    private static bool TryReadPosition(byte[] header, out float x, out float y, out float z)
    {
        x = BinaryPrimitives.ReadSingleLittleEndian(header.AsSpan(PositionOffset, 4));
        y = BinaryPrimitives.ReadSingleLittleEndian(header.AsSpan(PositionOffset + 4, 4));
        z = BinaryPrimitives.ReadSingleLittleEndian(header.AsSpan(PositionOffset + 8, 4));
        return float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z);
    }

    private static string ReadGroup(ReadOnlySpan<byte> reference)
    {
        Span<char> chars = stackalloc char[4];
        for (int index = 0; index < 4; index++)
        {
            byte value = reference[3 - index];
            chars[index] = value is >= 32 and <= 126 ? (char)value : ' ';
        }
        return new string(chars).Trim();
    }

    private string ReadCString(long address, int size)
    {
        byte[] bytes = _memory.ReadBytes(address, size);
        int zero = Array.IndexOf(bytes, (byte)0);
        int length = zero >= 0 ? zero : bytes.Length;
        return Encoding.UTF8.GetString(bytes, 0, length).Trim();
    }

    private IReadOnlyList<RuntimeTagFieldValue> Root(RuntimeTagEntry tag)
    {
        if (_roots.TryGetValue(tag.Index, out IReadOnlyList<RuntimeTagFieldValue>? cached))
            return cached;
        IReadOnlyList<RuntimeTagFieldValue> root = _definitions.ReadRootFields(
            tag.Group,
            tag.DataAddress,
            _memory.ReadBytes,
            encoded => _memory.TryResolveOffset(encoded, out long address) ? address : null);
        _roots[tag.Index] = root;
        return root;
    }

    private static IEnumerable<string> SplitScript(string script)
    {
        var batch = new StringBuilder();
        int batchBytes = 0;
        foreach (string line in script.Split('\n'))
        {
            int lineBytes = Encoding.UTF8.GetByteCount(line) + 1;
            if (batch.Length > 0 && batchBytes + lineBytes > ScriptBatchBytes)
            {
                yield return batch.ToString();
                batch.Clear();
                batchBytes = 0;
            }

            if (batch.Length > 0)
                batch.Append('\n');
            batch.Append(line);
            batchBytes += lineBytes;
        }

        if (batch.Length > 0)
            yield return batch.ToString();
    }

    private static string CleanFieldName(string name)
    {
        int description = name.IndexOfAny(['#', '{', ':', '^', '*', '!', '~']);
        string value = description >= 0 ? name[..description] : name;
        int path = value.LastIndexOf('/');
        return (path >= 0 ? value[(path + 1)..] : value).Trim();
    }

    private sealed record LoadedBlock(RuntimeTagEntry Tag, long Address, int Count, int ElementSize);
}
