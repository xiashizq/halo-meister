using System.Buffers.Binary;
using HaloMeister.App.Models;

namespace HaloMeister.App.Services;

public sealed record UnitCameraPreset(
    string Name,
    string Category,
    RuntimeTagEntry UnitTag)
{
    public string Detail => $"{Category} · {UnitTag.Name}  [{UnitTag.Group}]";
}

public sealed record CustomPlayerCamera(
    float X,
    float Y,
    float Z,
    float FieldOfViewDegrees,
    int ControlPointCount);

public sealed record PlayerCameraSession(
    RuntimeTagEntry PlayerUnit,
    IReadOnlyList<UnitCameraPreset> Presets,
    CustomPlayerCamera CustomCamera);

public sealed record PlayerCameraPatchResult(
    string Description,
    int ChangedValueCount);

public sealed class PlayerCameraService
{
    private const int UnitCameraSize = 120;
    private const int MaximumCameraTracks = 8;
    private const int MaximumControlPoints = 16;

    private readonly RuntimeTagMemoryService _memory = RuntimeTagMemoryService.Current;
    private readonly RuntimeTagDefinitionService _definitions = new();
    private readonly Dictionary<long, MemoryPatch> _patches = [];
    private int _processId;

    public static PlayerCameraService Current { get; } = new();
    public bool IsActive => _patches.Count > 0;
    public bool IsConnected => _memory.IsConnected;

    public PlayerCameraSession Load(int playerTagIndex)
    {
        EnsureConnected();
        EnsureDefinitions();
        ResetForNewProcess();

        IReadOnlyList<RuntimeTagEntry> tags = _memory.ReadTags();
        RuntimeTagEntry player = tags.FirstOrDefault(tag =>
                tag.Index == playerTagIndex &&
                tag.Group.Equals("bipd", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0)
            ?? throw new InvalidDataException(
                "The controlled player's live [bipd] tag is unavailable.");

        UnitCameraPreset[] presets = tags
            .Where(IsUsableUnit)
            .Where(tag => TryFindCameraBase(tag, out _))
            .Select(tag => new UnitCameraPreset(
                DisplayName(tag),
                tag.Group.Equals("vehi", StringComparison.OrdinalIgnoreCase)
                    ? "Vehicle"
                    : "Biped",
                tag))
            .OrderBy(preset =>
                preset.UnitTag.Index == player.Index ? 0 :
                preset.Category == "Vehicle" ? 1 : 2)
            .ThenBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(preset => preset.UnitTag.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new PlayerCameraSession(
            player,
            presets,
            ReadCustomCamera(player, tags));
    }

    public PlayerCameraPatchResult ApplyPreset(
        int playerTagIndex,
        UnitCameraPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        EnsureConnected();
        EnsureDefinitions();
        ResetForNewProcess();
        RestoreInternal();

        IReadOnlyList<RuntimeTagEntry> tags = _memory.ReadTags();
        RuntimeTagEntry player = FindPlayer(tags, playerTagIndex);
        RuntimeTagEntry donor = tags.FirstOrDefault(tag =>
                tag.Index == preset.UnitTag.Index && IsUsableUnit(tag))
            ?? throw new InvalidOperationException(
                "That camera preset is no longer loaded. Refresh the preset list.");
        long playerCamera = FindCameraBase(player);
        long donorCamera = FindCameraBase(donor);
        byte[] replacement = _memory.ReadBytes(donorCamera, UnitCameraSize);

        ApplyWrites([(playerCamera, replacement)]);
        return new PlayerCameraPatchResult(
            $"{preset.Name} camera applied to {player.LeafName}.",
            1);
    }

    public PlayerCameraPatchResult ApplyCustom(
        int playerTagIndex,
        float x,
        float y,
        float z,
        float fieldOfViewDegrees)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            throw new ArgumentOutOfRangeException(
                nameof(x),
                "Custom camera coordinates must be finite numbers.");
        if (!float.IsFinite(fieldOfViewDegrees) ||
            fieldOfViewDegrees is < 30f or > 150f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fieldOfViewDegrees),
                "Custom camera FOV must be between 30 and 150 degrees.");
        }

        EnsureConnected();
        EnsureDefinitions();
        ResetForNewProcess();
        RestoreInternal();

        IReadOnlyList<RuntimeTagEntry> tags = _memory.ReadTags();
        RuntimeTagEntry player = FindPlayer(tags, playerTagIndex);
        IReadOnlyList<RuntimeTagFieldValue> playerFields = ReadRoot(player);
        RuntimeTagFieldValue fov = FindUnitCameraField(
            playerFields,
            field => Clean(field.Name).Equals(
                "override fov",
                StringComparison.OrdinalIgnoreCase));
        if (fov.Size != sizeof(float))
            throw new InvalidDataException("The player camera FOV has an unexpected size.");

        List<long> positions = FindControlPointPositions(player, playerFields, tags);
        if (positions.Count == 0)
            throw new InvalidDataException(
                "The controlled player's camera has no editable [trak] control points.");

        byte[] position = new byte[sizeof(float) * 3];
        BinaryPrimitives.WriteSingleLittleEndian(position, x);
        BinaryPrimitives.WriteSingleLittleEndian(position.AsSpan(4), y);
        BinaryPrimitives.WriteSingleLittleEndian(position.AsSpan(8), z);
        byte[] fovRadians = new byte[sizeof(float)];
        BinaryPrimitives.WriteSingleLittleEndian(
            fovRadians,
            fieldOfViewDegrees * (MathF.PI / 180f));

        var writes = positions
            .Select(address => (Address: address, Bytes: position))
            .Append((fov.Address, fovRadians))
            .ToArray();
        ApplyWrites(writes);
        return new PlayerCameraPatchResult(
            $"Custom camera applied to {positions.Count} track control point(s).",
            writes.Length);
    }

    public int Restore()
    {
        EnsureConnected();
        ResetForNewProcess();
        return RestoreInternal();
    }

    private CustomPlayerCamera ReadCustomCamera(
        RuntimeTagEntry player,
        IReadOnlyList<RuntimeTagEntry> tags)
    {
        IReadOnlyList<RuntimeTagFieldValue> fields = ReadRoot(player);
        RuntimeTagFieldValue fov = FindUnitCameraField(
            fields,
            field => Clean(field.Name).Equals(
                "override fov",
                StringComparison.OrdinalIgnoreCase));
        List<long> positions = FindControlPointPositions(player, fields, tags);
        if (positions.Count == 0)
            throw new InvalidDataException(
                "The controlled player's camera has no readable [trak] control points.");

        byte[] position = _memory.ReadBytes(positions[0], 12);
        float radians = BinaryPrimitives.ReadSingleLittleEndian(
            _memory.ReadBytes(fov.Address, sizeof(float)));
        float degrees = radians == 0f ? 70f : radians * (180f / MathF.PI);
        return new CustomPlayerCamera(
            BinaryPrimitives.ReadSingleLittleEndian(position),
            BinaryPrimitives.ReadSingleLittleEndian(position.AsSpan(4)),
            BinaryPrimitives.ReadSingleLittleEndian(position.AsSpan(8)),
            degrees,
            positions.Count);
    }

    private List<long> FindControlPointPositions(
        RuntimeTagEntry player,
        IReadOnlyList<RuntimeTagFieldValue> playerFields,
        IReadOnlyList<RuntimeTagEntry> tags)
    {
        RuntimeTagFieldValue tracks = FindUnitCameraField(
            playerFields,
            field => field.ChildBlockDefinition == "unit_camera_track_block");
        if (!tracks.CanOpenBlock)
            return [];

        Dictionary<int, RuntimeTagEntry> tagsByIndex =
            tags.ToDictionary(tag => tag.Index);
        var trackIndices = new HashSet<int>();
        int trackCount = Math.Min(tracks.ChildCount, MaximumCameraTracks);
        for (int index = 0; index < trackCount; index++)
        {
            IReadOnlyList<RuntimeTagFieldValue> fields =
                _definitions.ReadBlockFields(
                    player.Group,
                    tracks.ChildBlockDefinition!,
                    tracks.ChildAddress,
                    index,
                    _memory.ReadBytes,
                    ResolveOrNull);
            foreach (RuntimeTagFieldValue reference in fields.Where(field =>
                         field.IsTagReference &&
                         tagsByIndex.TryGetValue(
                             field.ReferencedTagIndex,
                             out RuntimeTagEntry? target) &&
                         target.Group.Equals(
                             "trak",
                             StringComparison.OrdinalIgnoreCase)))
            {
                trackIndices.Add(reference.ReferencedTagIndex);
            }
        }

        var result = new List<long>();
        foreach (int trackIndex in trackIndices)
        {
            RuntimeTagEntry track = tagsByIndex[trackIndex];
            RuntimeTagFieldValue? points = ReadRoot(track).FirstOrDefault(field =>
                field.ChildBlockDefinition == "camera_track_control_point_block" &&
                field.CanOpenBlock);
            if (points?.ChildBlockDefinition is null)
                continue;
            int pointCount = Math.Min(points.ChildCount, MaximumControlPoints);
            for (int index = 0; index < pointCount; index++)
            {
                RuntimeTagFieldValue? position = _definitions
                    .ReadBlockFields(
                        track.Group,
                        points.ChildBlockDefinition,
                        points.ChildAddress,
                        index,
                        _memory.ReadBytes,
                        ResolveOrNull)
                    .FirstOrDefault(field =>
                        field.Type == "real_vector_3d" &&
                        Clean(field.Name).Equals(
                            "position",
                            StringComparison.OrdinalIgnoreCase));
                if (position?.Size == 12)
                    result.Add(position.Address);
            }
        }
        return result.Distinct().ToList();
    }

    private void ApplyWrites(
        IReadOnlyList<(long Address, byte[] Bytes)> writes)
    {
        var completed = new List<long>();
        try
        {
            foreach ((long address, byte[] bytes) in writes)
            {
                byte[] original = _memory.ReadBytes(address, bytes.Length);
                _memory.WriteVerified(address, bytes);
                _patches[address] = new MemoryPatch(original, bytes.ToArray());
                completed.Add(address);
            }
        }
        catch
        {
            foreach (long address in completed.AsEnumerable().Reverse())
            {
                MemoryPatch patch = _patches[address];
                try { _memory.WriteVerified(address, patch.Original); }
                catch { }
                _patches.Remove(address);
            }
            throw;
        }
    }

    private int RestoreInternal()
    {
        int restored = 0;
        foreach ((long address, MemoryPatch patch) in _patches.ToArray())
        {
            byte[] current;
            try { current = _memory.ReadBytes(address, patch.Patched.Length); }
            catch
            {
                _patches.Remove(address);
                continue;
            }
            if (current.AsSpan().SequenceEqual(patch.Patched))
            {
                _memory.WriteVerified(address, patch.Original);
                restored++;
            }
            _patches.Remove(address);
        }
        return restored;
    }

    private long FindCameraBase(RuntimeTagEntry tag)
    {
        if (TryFindCameraBase(tag, out long address))
            return address;
        throw new InvalidDataException(
            $"{tag.Name} does not expose a valid unit camera definition.");
    }

    private bool TryFindCameraBase(RuntimeTagEntry tag, out long address)
    {
        address = 0;
        try
        {
            RuntimeTagFieldValue? flags = ReadRoot(tag).FirstOrDefault(field =>
                IsUnitCameraField(field) &&
                field.Type == "word_flags" &&
                Clean(field.Name).Equals(
                    "flags",
                    StringComparison.OrdinalIgnoreCase));
            if (flags is null)
                return false;
            address = flags.Address;
            _memory.ReadBytes(address, UnitCameraSize);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static RuntimeTagFieldValue FindUnitCameraField(
        IReadOnlyList<RuntimeTagFieldValue> fields,
        Func<RuntimeTagFieldValue, bool> predicate) =>
        fields.FirstOrDefault(field => IsUnitCameraField(field) && predicate(field))
        ?? throw new InvalidDataException(
            "The [unit] schema did not resolve the requested player camera field.");

    private static bool IsUnitCameraField(RuntimeTagFieldValue field) =>
        field.Name.Contains(
            "/ unit camera / ",
            StringComparison.OrdinalIgnoreCase) &&
        !field.Name.Contains(
            "/ sync action camera / ",
            StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<RuntimeTagFieldValue> ReadRoot(RuntimeTagEntry tag) =>
        _definitions.ReadRootFields(
            tag.Group,
            tag.DataAddress,
            _memory.ReadBytes,
            ResolveOrNull);

    private long? ResolveOrNull(uint encoded) =>
        _memory.TryResolveOffset(encoded, out long address) ? address : null;

    private static RuntimeTagEntry FindPlayer(
        IReadOnlyList<RuntimeTagEntry> tags,
        int playerTagIndex) =>
        tags.FirstOrDefault(tag =>
            tag.Index == playerTagIndex &&
            tag.Group.Equals("bipd", StringComparison.OrdinalIgnoreCase) &&
            tag.DataAddress > 0)
        ?? throw new InvalidDataException(
            "The controlled player's live [bipd] tag is unavailable.");

    private static bool IsUsableUnit(RuntimeTagEntry tag) =>
        tag.DataAddress > 0 &&
        tag.RootCount > 0 &&
        (tag.Group.Equals("bipd", StringComparison.OrdinalIgnoreCase) ||
         tag.Group.Equals("vehi", StringComparison.OrdinalIgnoreCase)) &&
        !tag.Name.Contains(@"\stimuli\", StringComparison.OrdinalIgnoreCase) &&
        !tag.Name.Contains("/stimuli/", StringComparison.OrdinalIgnoreCase);

    private static string DisplayName(RuntimeTagEntry tag)
    {
        string leaf = tag.LeafName.Replace('_', ' ').Trim();
        if (leaf.Length == 0)
            return tag.Name;
        return string.Join(
            ' ',
            leaf.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private static string Clean(string name)
    {
        int description = name.IndexOfAny(['#', '{', ':', '^', '*', '!', '~']);
        string value = description >= 0 ? name[..description] : name;
        int path = value.LastIndexOf('/');
        return (path >= 0 ? value[(path + 1)..] : value).Trim();
    }

    private void EnsureConnected()
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException(
                "Connect Halo Meister to the running game from the header first.");
    }

    private void EnsureDefinitions()
    {
        if (_definitions.SchemaCount == 0)
            _definitions.LoadDirectory(
                RuntimeTagDefinitionLocator.ResolveCampaignEvolved());
        if (!_definitions.HasSchema("bipd") ||
            !_definitions.HasSchema("vehi") ||
            !_definitions.HasSchema("trak"))
        {
            throw new InvalidDataException(
                "The loaded definitions do not provide the [bipd], [vehi], and [trak] camera schemas.");
        }
    }

    private void ResetForNewProcess()
    {
        if (_processId == _memory.ProcessId)
            return;
        _patches.Clear();
        _processId = _memory.ProcessId;
    }

    private sealed record MemoryPatch(byte[] Original, byte[] Patched);
}
