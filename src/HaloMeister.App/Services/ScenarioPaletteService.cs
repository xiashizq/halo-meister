using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using HaloMeister.App.Localization;
using HaloMeister.App.Models;

namespace HaloMeister.App.Services;

public enum ScenarioPaletteKind
{
    Scenery,
    Machine,
}

public sealed record ScenarioPaletteEntry(
    ScenarioPaletteKind Kind,
    int Index,
    string TagPath,
    string TagGroup,
    uint Datum,
    bool CanPlace)
{
    public string Title
    {
        get
        {
            if (string.IsNullOrWhiteSpace(TagPath))
                return L.Get("scenario_palette.unknown_tag");
            int separator = Math.Max(TagPath.LastIndexOf('\\'), TagPath.LastIndexOf('/'));
            string leaf = separator >= 0 ? TagPath[(separator + 1)..] : TagPath;
            int dot = leaf.LastIndexOf('.');
            return dot > 0 ? leaf[..dot] : leaf;
        }
    }

    public string DisplayPath => FormatTagPath(TagPath, TagGroup);

    public string Detail => $"{Index}.  {DisplayPath}";

    public string SearchText => $"{Index} {Title} {DisplayPath} {TagGroup}";

    public static string FormatTagPath(string path, string group)
    {
        string normalized = path.Replace('/', '\\').Trim();
        string extension = group.ToLowerInvariant() switch
        {
            "scen" => "scenery",
            "mach" => "device_machine",
            _ => group.ToLowerInvariant(),
        };
        if (extension.Length == 0 ||
            normalized.EndsWith("." + extension, StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        return $"{normalized}.{extension}";
    }
}

public sealed record ScenarioPaletteSession(
    string ScenarioName,
    IReadOnlyList<ScenarioPaletteEntry> Scenery,
    IReadOnlyList<ScenarioPaletteEntry> Machines);

/// <summary>
/// One object this session created from a scenario palette. The datum is the
/// handle returned by the native object allocator and is only valid until the
/// mission unloads.
/// </summary>
public sealed record PlacedScenarioObject(
    long Id,
    ScenarioPaletteKind Kind,
    string Title,
    string Path,
    uint ObjectDatum)
{
    public string KindLabel => Kind == ScenarioPaletteKind.Machine
        ? L.Get("scenario_palette.kind_machine")
        : L.Get("scenario_palette.kind_scenery");

    public string DatumText => $"0x{ObjectDatum:X8}";

    public string Detail => L.Format(
        "scenario_palette.placed_detail",
        KindLabel,
        DatumText);

    public string SearchText => $"{Title} {Path} {KindLabel} {DatumText}";
}

public readonly record struct ScenarioPlacementResult(
    ScriptExecutionResult Execution,
    PlacedScenarioObject? Placed);

/// <summary>
/// A point in simulation world units, plus the view angles derived from the
/// forward vector. Yaw 0 faces +X and increases toward +Y. Pitch 0 is level.
/// </summary>
public readonly record struct ScenarioPlayerPose(
    float X,
    float Y,
    float Z,
    float? YawDegrees,
    float? PitchDegrees)
{
    public bool HasView =>
        YawDegrees is float yaw &&
        PitchDegrees is float pitch &&
        float.IsFinite(yaw) &&
        float.IsFinite(pitch);
}

/// <summary>
/// Reads the loaded scenario's scenery and device-machine palettes. Place uses
/// the native object create path, which spawns the tag in front of the player
/// and records the returned object datum so that object can be deleted later.
/// </summary>
public sealed class ScenarioPaletteService(RuntimeTagMemoryService memory)
{
    private const int PaletteEntrySize = 16;
    private const int MaxPalette = 256;

    private readonly RuntimeTagMemoryService _memory = memory;
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private readonly RuntimeTagDefinitionService _definitions = new();
    private readonly Dictionary<int, IReadOnlyList<RuntimeTagFieldValue>> _roots = [];
    // The page is recreated when the language changes. Keep the session log
    // outside that instance so placed objects stay recorded.
    private static readonly object PlacedGate = new();
    private static readonly List<PlacedScenarioObject> PlacedObjects = [];
    private static string? PlacedScenario;
    private static long NextPlacedId = 1;

    public IReadOnlyList<PlacedScenarioObject> Placed
    {
        get
        {
            lock (PlacedGate)
                return PlacedObjects.ToArray();
        }
    }

    public void NoteScenario(string? scenarioName)
    {
        lock (PlacedGate)
        {
            if (string.IsNullOrWhiteSpace(scenarioName))
            {
                PlacedObjects.Clear();
                PlacedScenario = null;
                return;
            }

            if (string.Equals(PlacedScenario, scenarioName, StringComparison.OrdinalIgnoreCase))
                return;

            PlacedObjects.Clear();
            PlacedScenario = scenarioName;
        }
    }

    public ScenarioPaletteSession Scan()
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException(L.Get("scenario_palette.error_connect"));

        EnsureDefinitions();
        _roots.Clear();

        IReadOnlyList<RuntimeTagEntry> tags = _memory.ReadTags();
        Dictionary<int, RuntimeTagEntry> tagsByIndex = tags
            .GroupBy(tag => tag.Index)
            .ToDictionary(group => group.Key, group => group.First());

        RuntimeTagEntry[] scenarios = FindTags(tags, "scnr");
        if (scenarios.Length == 0)
            throw new InvalidDataException(L.Get("scenario_palette.error_no_scenario"));

        var scenario = scenarios
            .Select(tag => (
                Tag: tag,
                Scenery: ReadPalette(tag, ScenarioPaletteKind.Scenery, tagsByIndex, required: true),
                Machines: ReadPalette(tag, ScenarioPaletteKind.Machine, tagsByIndex, required: true)))
            .OrderByDescending(item => item.Scenery.Count + item.Machines.Count)
            .ThenBy(item => item.Tag.Index)
            .First();

        List<ScenarioPaletteEntry> scenery = scenario.Scenery;
        List<ScenarioPaletteEntry> machines = scenario.Machines;
        if (scenery.Count == 0)
        {
            scenery = FindTags(tags, "*cen")
                .SelectMany(tag => ReadPalette(tag, ScenarioPaletteKind.Scenery, tagsByIndex, required: false))
                .ToList();
        }

        if (machines.Count == 0)
        {
            machines = FindTags(tags, "dgr*")
                .SelectMany(tag => ReadPalette(tag, ScenarioPaletteKind.Machine, tagsByIndex, required: false))
                .ToList();
        }

        return new ScenarioPaletteSession(scenario.Tag.Name, scenery, machines);
    }

    public async Task<ScenarioPlacementResult> PlaceAsync(
        ScenarioPaletteEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.CanPlace || entry.Datum == 0 || entry.Datum == uint.MaxValue)
        {
            throw new InvalidOperationException(
                L.Format("scenario_palette.error_unloaded", entry.Title));
        }

        ScriptingBridgeStatus status = _bridge.GetStatus();
        status.EnsureRuntimeReady();

        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.BlamSpawn,
            entry.Datum.ToString("X8"),
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome == ScriptOutcome.Failed)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.Message)
                    ? L.Format("scenario_palette.error_unloaded", entry.Title)
                    : result.Message);
        if (result.Outcome != ScriptOutcome.Confirmed ||
            !TryReadCreatedDatum(result.Message, out uint objectDatum))
        {
            return new ScenarioPlacementResult(result, null);
        }

        var placed = new PlacedScenarioObject(
            NextId(),
            entry.Kind,
            entry.Title,
            entry.DisplayPath,
            objectDatum);
        lock (PlacedGate)
            PlacedObjects.Insert(0, placed);
        return new ScenarioPlacementResult(result, placed);
    }

    public async Task<ScenarioPlacementResult> PlaceAtAsync(
        ScenarioPaletteEntry entry,
        ScenarioPlayerPose pose,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.CanPlace || entry.Datum == 0 || entry.Datum == uint.MaxValue)
        {
            throw new InvalidOperationException(
                L.Format("scenario_palette.error_unloaded", entry.Title));
        }

        if (!pose.HasView ||
            pose.PitchDegrees is not float pitch ||
            pose.YawDegrees is not float yaw)
        {
            throw new InvalidOperationException(L.Get("scenario_palette.error_pose"));
        }

        if (Math.Abs(pitch) > 90f)
            throw new InvalidOperationException(L.Get("scenario_palette.error_pitch"));
        if (!float.IsFinite(pose.X) ||
            !float.IsFinite(pose.Y) ||
            !float.IsFinite(pose.Z) ||
            Math.Abs(pose.X) > 100_000f ||
            Math.Abs(pose.Y) > 100_000f ||
            Math.Abs(pose.Z) > 100_000f ||
            Math.Abs(yaw) > 1080f)
        {
            throw new InvalidOperationException(L.Get("scenario_palette.error_coordinates"));
        }

        ScriptingBridgeStatus status = _bridge.GetStatus();
        status.EnsureRuntimeReady();

        string payload = string.Join(
            ',',
            entry.Datum.ToString("X8"),
            pose.X.ToString("0.########", CultureInfo.InvariantCulture),
            pose.Y.ToString("0.########", CultureInfo.InvariantCulture),
            pose.Z.ToString("0.########", CultureInfo.InvariantCulture),
            yaw.ToString("0.########", CultureInfo.InvariantCulture),
            pitch.ToString("0.########", CultureInfo.InvariantCulture));
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.BlamObjectPlace,
            payload,
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome == ScriptOutcome.Failed)
        {
            throw new InvalidOperationException(
                BridgeFailureMessage(result.Message, entry.Title));
        }

        if (result.Outcome != ScriptOutcome.Confirmed ||
            !TryReadCreatedDatum(result.Message, out uint objectDatum))
        {
            return new ScenarioPlacementResult(result, null);
        }

        var placed = new PlacedScenarioObject(
            NextId(),
            entry.Kind,
            entry.Title,
            entry.DisplayPath,
            objectDatum);
        lock (PlacedGate)
            PlacedObjects.Insert(0, placed);
        return new ScenarioPlacementResult(result, placed);
    }

    public async Task<ScenarioPlayerPose> ReadPlayerPoseAsync(
        CancellationToken cancellationToken = default)
    {
        ScriptingBridgeStatus status = _bridge.GetStatus();
        status.EnsureRuntimeReady();
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.PlayerPosition,
            "read",
            cancellationToken: cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.Message)
                    ? L.Get("scenario_palette.pose_unavailable")
                    : BridgeFailureMessage(result.Message, ""));
        }

        if (!TryReadPlayerPose(result.Message, out ScenarioPlayerPose pose))
        {
            throw new InvalidDataException(L.Get("scenario_palette.pose_unavailable"));
        }

        return pose;
    }

    public async Task<ScriptExecutionResult> DestroyAsync(
        PlacedScenarioObject placed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placed);
        lock (PlacedGate)
        {
            if (PlacedObjects.All(item => item.Id != placed.Id))
            {
                throw new InvalidOperationException(
                    L.Format("scenario_palette.error_missing", placed.Title));
            }
        }

        ScriptingBridgeStatus status = _bridge.GetStatus();
        status.EnsureRuntimeReady();

        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.BlamObjectDelete,
            placed.ObjectDatum.ToString("X8"),
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
        {
            throw new InvalidOperationException(
                DestroyFailureMessage(placed.Title, result.Message));
        }

        lock (PlacedGate)
            PlacedObjects.RemoveAll(item => item.Id == placed.Id);
        return result;
    }

    public async Task<int> DestroyAllAsync(CancellationToken cancellationToken = default)
    {
        PlacedScenarioObject[] snapshot;
        lock (PlacedGate)
            snapshot = PlacedObjects.ToArray();

        int destroyed = 0;
        foreach (PlacedScenarioObject placed in snapshot)
        {
            await DestroyAsync(placed, cancellationToken);
            destroyed++;
        }

        return destroyed;
    }

    private static long NextId()
    {
        lock (PlacedGate)
            return NextPlacedId++;
    }

    internal static bool TryReadCreatedDatum(string message, out uint datum)
    {
        datum = 0;
        const string marker = "datum 0x";
        int start = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return false;
        start += marker.Length;
        if (start + 8 > message.Length)
            return false;
        return uint.TryParse(
                message.AsSpan(start, 8),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out datum) &&
            datum != 0 &&
            datum != uint.MaxValue;
    }

    private static string DestroyFailureMessage(string title, string message) =>
        BridgeFailureMessage(message, title);

    private static string BridgeFailureMessage(string message, string fallbackTitle)
    {
        if (message.Contains("Unsupported script kind", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("native spawn operation is unsupported", StringComparison.OrdinalIgnoreCase))
        {
            return L.Get("scenario_palette.error_bridge");
        }

        return string.IsNullOrWhiteSpace(message)
            ? L.Format("scenario_palette.error_destroy", fallbackTitle)
            : message;
    }

    internal static bool TryReadPlayerPose(string message, out ScenarioPlayerPose pose)
    {
        pose = default;
        const string marker = "Return value: ";
        int markerOffset = message.IndexOf(marker, StringComparison.Ordinal);
        if (markerOffset < 0)
            return false;
        string[] values = message[(markerOffset + marker.Length)..]
            .Trim()
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (values.Length < 3 ||
            !TryFloat(values[0], out float x) ||
            !TryFloat(values[1], out float y) ||
            !TryFloat(values[2], out float z))
        {
            return false;
        }

        float? yaw = null;
        float? pitch = null;
        if (values.Length >= 6 &&
            TryFloat(values[3], out float forwardX) &&
            TryFloat(values[4], out float forwardY) &&
            TryFloat(values[5], out float forwardZ))
        {
            yaw = MathF.Atan2(forwardY, forwardX) * (180f / MathF.PI);
            float horizontal = MathF.Sqrt(forwardX * forwardX + forwardY * forwardY);
            pitch = MathF.Atan2(forwardZ, horizontal) * (180f / MathF.PI);
        }

        pose = new ScenarioPlayerPose(x, y, z, yaw, pitch);
        return true;
    }

    private static bool TryFloat(string text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
        float.IsFinite(value);

    private List<ScenarioPaletteEntry> ReadPalette(
        RuntimeTagEntry owner,
        ScenarioPaletteKind kind,
        IReadOnlyDictionary<int, RuntimeTagEntry> tagsByIndex,
        bool required)
    {
        try
        {
            string definition = kind == ScenarioPaletteKind.Machine
                ? "scenario_machine_palette_block"
                : "scenario_scenery_palette_block";
            string[] names = kind == ScenarioPaletteKind.Machine
                ? ["machine palette"]
                : ["scenery palette"];
            string expectedGroup = kind == ScenarioPaletteKind.Machine ? "mach" : "scen";
            LoadedBlock? palette = FindLoadedBlock(
                owner,
                definition,
                names,
                MaxPalette,
                PaletteEntrySize);
            if (palette is null)
                return [];

            byte[] raw = _memory.ReadBytes(palette.Address, palette.Count * PaletteEntrySize);
            var entries = new List<ScenarioPaletteEntry>(palette.Count);
            for (int index = 0; index < palette.Count; index++)
            {
                byte[] reference = raw.AsSpan(index * PaletteEntrySize, PaletteEntrySize).ToArray();
                if (!TryReadReference(
                        reference,
                        tagsByIndex,
                        expectedGroup,
                        out string path,
                        out string group,
                        out uint datum,
                        out bool canPlace))
                {
                    continue;
                }

                entries.Add(new ScenarioPaletteEntry(
                    kind,
                    index,
                    path,
                    group,
                    datum,
                    canPlace));
            }

            return entries;
        }
        catch (InvalidDataException) when (!required)
        {
            return [];
        }
    }

    private bool TryReadReference(
        byte[] reference,
        IReadOnlyDictionary<int, RuntimeTagEntry> tagsByIndex,
        string fallbackGroup,
        out string path,
        out string group,
        out uint datum,
        out bool canPlace)
    {
        path = "";
        group = ReadGroup(reference);
        if (string.IsNullOrWhiteSpace(group))
            group = fallbackGroup;
        datum = 0;
        canPlace = false;

        ushort index = BinaryPrimitives.ReadUInt16LittleEndian(reference.AsSpan(12, 2));
        uint storedDatum = BinaryPrimitives.ReadUInt32LittleEndian(reference.AsSpan(12, 4));
        if (storedDatum != uint.MaxValue &&
            index != ushort.MaxValue &&
            tagsByIndex.TryGetValue(index, out RuntimeTagEntry? tag))
        {
            path = tag.Name;
            if (!string.IsNullOrWhiteSpace(tag.Group))
                group = tag.Group;
            if (tag.DataAddress > 0 && tag.Index >= 0)
            {
                datum = RuntimeTagMemoryService.BuildRuntimeDatum(tag);
                canPlace = datum != 0 && datum != uint.MaxValue;
            }

            return !string.IsNullOrWhiteSpace(path);
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(reference.AsSpan(8, 4));
        uint nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(reference.AsSpan(4, 4));
        if (length is > 0 and <= 1024 &&
            _memory.TryResolveOffset(nameOffset, out long address) &&
            address > 0)
        {
            path = ReadCString(address, length);
            return !string.IsNullOrWhiteSpace(path);
        }

        return false;
    }

    private void EnsureDefinitions()
    {
        if (_definitions.SchemaCount == 0)
            _definitions.LoadDirectory(RuntimeTagDefinitionLocator.ResolveCampaignEvolved());
        if (!_definitions.HasSchema("scnr"))
            throw new InvalidDataException(L.Get("scenario_palette.error_no_schema"));
    }

    private static RuntimeTagEntry[] FindTags(IReadOnlyList<RuntimeTagEntry> tags, string group) =>
        tags.Where(tag =>
                tag.DataAddress > 0 &&
                string.Equals(tag.Group, group, StringComparison.OrdinalIgnoreCase))
            .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(tag => tag.Index)
            .ToArray();

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
                L.Format("scenario_palette.error_invalid_block", definition, tag.Name));
        }

        return new LoadedBlock(field.ChildAddress, field.ChildCount);
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

    private static string CleanFieldName(string name)
    {
        int description = name.IndexOfAny(['#', '{', ':', '^', '*', '!', '~']);
        string value = description >= 0 ? name[..description] : name;
        int path = value.LastIndexOf('/');
        return (path >= 0 ? value[(path + 1)..] : value).Trim();
    }

    private sealed record LoadedBlock(long Address, int Count);
}
