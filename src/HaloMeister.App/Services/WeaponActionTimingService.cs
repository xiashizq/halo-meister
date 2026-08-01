using System.Buffers.Binary;
using HaloMeister.App.Models;

namespace HaloMeister.App.Services;

public sealed record WeaponActionTimingResult(int GraphCount, int EventCount);

public sealed class WeaponActionTimingService
{
    private const short AllowInterruptionEvent = 6;
    private const int MaxAnimationsPerGraph = 512;
    private const int MaxEventsPerAnimation = 64;

    private readonly RuntimeTagMemoryService _memory = RuntimeTagMemoryService.Current;
    private readonly RuntimeTagDefinitionService _definitions = new();
    private readonly Dictionary<long, EventSnapshot> _snapshots = [];
    private int _processId;

    public static WeaponActionTimingService Current { get; } = new();
    public bool IsActive => _snapshots.Count > 0;

    public WeaponActionTimingResult Enable()
    {
        EnsureConnected();
        EnsureDefinitions();
        ResetForNewProcess();

        IReadOnlyList<RuntimeTagEntry> tags = _memory.ReadTags();
        Dictionary<int, RuntimeTagEntry> tagsByIndex =
            tags.ToDictionary(tag => tag.Index);
        HashSet<int> graphIndices = FindFirstPersonAnimationGraphs(tags, tagsByIndex);
        if (graphIndices.Count == 0)
        {
            throw new InvalidOperationException(
                "No loaded first-person weapon animation graphs were found. Load or resume a campaign checkpoint, then try again.");
        }

        var pending = new Dictionary<long, byte[]>();
        foreach (int graphIndex in graphIndices)
        {
            if (!tagsByIndex.TryGetValue(graphIndex, out RuntimeTagEntry? graph) ||
                graph.DataAddress <= 0)
                continue;
            CollectInterruptionFrames(graph, pending);
        }

        if (pending.Count == 0)
        {
            throw new InvalidOperationException(
                "The loaded first-person weapon animations have no delayed interruption markers to move.");
        }

        var written = new List<long>();
        try
        {
            foreach ((long address, byte[] original) in pending)
            {
                byte[] immediate = [0, 0];
                _memory.WriteVerified(address, immediate);
                _snapshots[address] = new EventSnapshot(original, immediate);
                written.Add(address);
            }
        }
        catch
        {
            foreach (long address in written)
            {
                EventSnapshot snapshot = _snapshots[address];
                try { _memory.WriteVerified(address, snapshot.Original); }
                catch { }
                _snapshots.Remove(address);
            }
            throw;
        }

        return new WeaponActionTimingResult(graphIndices.Count, written.Count);
    }

    public int Restore()
    {
        if (_snapshots.Count == 0)
            return 0;
        EnsureConnected();
        ResetForNewProcess();

        int restored = 0;
        foreach ((long address, EventSnapshot snapshot) in _snapshots.ToArray())
        {
            byte[] current;
            try { current = _memory.ReadBytes(address, snapshot.Patched.Length); }
            catch
            {
                _snapshots.Remove(address);
                continue;
            }

            // Do not overwrite a value another tool or a checkpoint reload changed.
            if (!current.AsSpan().SequenceEqual(snapshot.Patched))
            {
                _snapshots.Remove(address);
                continue;
            }

            _memory.WriteVerified(address, snapshot.Original);
            _snapshots.Remove(address);
            restored++;
        }
        return restored;
    }

    private HashSet<int> FindFirstPersonAnimationGraphs(
        IReadOnlyList<RuntimeTagEntry> tags,
        IReadOnlyDictionary<int, RuntimeTagEntry> tagsByIndex)
    {
        var graphIndices = new HashSet<int>();
        foreach (RuntimeTagEntry weapon in tags.Where(tag =>
                     tag.Group.Equals("weap", StringComparison.OrdinalIgnoreCase) &&
                     tag.DataAddress > 0))
        {
            IReadOnlyList<RuntimeTagFieldValue> root;
            try
            {
                root = _definitions.ReadRootFields(
                    weapon.Group,
                    weapon.DataAddress,
                    _memory.ReadBytes,
                    ResolveOffset);
            }
            catch
            {
                continue;
            }

            foreach (RuntimeTagFieldValue firstPerson in root.Where(field =>
                         field.CanOpenBlock &&
                         field.ChildBlockDefinition is not null &&
                         field.Name.Contains(
                             "first person",
                             StringComparison.OrdinalIgnoreCase)))
            {
                for (int element = 0; element < firstPerson.ChildCount; element++)
                {
                    IReadOnlyList<RuntimeTagFieldValue> fields =
                        _definitions.ReadBlockFields(
                            weapon.Group,
                            firstPerson.ChildBlockDefinition!,
                            firstPerson.ChildAddress,
                            element,
                            _memory.ReadBytes,
                            ResolveOffset);
                    foreach (RuntimeTagFieldValue reference in fields.Where(field =>
                                 field.IsTagReference &&
                                 tagsByIndex.TryGetValue(
                                     field.ReferencedTagIndex,
                                     out RuntimeTagEntry? target) &&
                                 target.Group.Equals(
                                     "jmad",
                                     StringComparison.OrdinalIgnoreCase)))
                    {
                        graphIndices.Add(reference.ReferencedTagIndex);
                    }
                }
            }
        }
        return graphIndices;
    }

    private void CollectInterruptionFrames(
        RuntimeTagEntry graph,
        IDictionary<long, byte[]> pending)
    {
        RuntimeTagFieldValue? animations = _definitions
            .ReadRootFields(
                graph.Group,
                graph.DataAddress,
                _memory.ReadBytes,
                ResolveOffset)
            .FirstOrDefault(field =>
                field.CanOpenBlock &&
                field.ChildBlockDefinition == "animation_pool_block");
        if (animations?.ChildBlockDefinition is null)
            return;

        int animationCount = Math.Min(
            animations.ChildCount,
            MaxAnimationsPerGraph);
        for (int animationIndex = 0; animationIndex < animationCount; animationIndex++)
        {
            IReadOnlyList<RuntimeTagFieldValue> animation =
                _definitions.ReadBlockFields(
                    graph.Group,
                    animations.ChildBlockDefinition,
                    animations.ChildAddress,
                    animationIndex,
                    _memory.ReadBytes,
                    ResolveOffset);
            foreach (RuntimeTagFieldValue shared in animation.Where(field =>
                         field.CanOpenBlock &&
                         field.ChildBlockDefinition is not null &&
                         field.Name.Contains(
                             "shared animation data",
                             StringComparison.OrdinalIgnoreCase)))
            {
                for (int sharedIndex = 0; sharedIndex < shared.ChildCount; sharedIndex++)
                {
                    IReadOnlyList<RuntimeTagFieldValue> sharedFields =
                        _definitions.ReadBlockFields(
                            graph.Group,
                            shared.ChildBlockDefinition!,
                            shared.ChildAddress,
                            sharedIndex,
                            _memory.ReadBytes,
                            ResolveOffset);
                    RuntimeTagFieldValue? events = sharedFields.FirstOrDefault(field =>
                        field.CanOpenBlock &&
                        field.ChildBlockDefinition is not null &&
                        field.Name.Contains(
                            "frame events",
                            StringComparison.OrdinalIgnoreCase));
                    if (events is not null)
                        CollectEventFrames(graph.Group, events, pending);
                }
            }
        }
    }

    private void CollectEventFrames(
        string group,
        RuntimeTagFieldValue events,
        IDictionary<long, byte[]> pending)
    {
        int eventCount = Math.Min(events.ChildCount, MaxEventsPerAnimation);
        for (int eventIndex = 0; eventIndex < eventCount; eventIndex++)
        {
            IReadOnlyList<RuntimeTagFieldValue> fields =
                _definitions.ReadBlockFields(
                    group,
                    events.ChildBlockDefinition!,
                    events.ChildAddress,
                    eventIndex,
                    _memory.ReadBytes,
                    ResolveOffset);
            RuntimeTagFieldValue? type = fields.FirstOrDefault(field =>
                Clean(field.Name).Equals("type", StringComparison.OrdinalIgnoreCase));
            RuntimeTagFieldValue? frame = fields.FirstOrDefault(field =>
                Clean(field.Name).Equals("frame", StringComparison.OrdinalIgnoreCase));
            if (type is null || frame is null || type.Size != sizeof(short) ||
                frame.Size != sizeof(short))
                continue;

            short eventType = BinaryPrimitives.ReadInt16LittleEndian(
                _memory.ReadBytes(type.Address, sizeof(short)));
            byte[] original = _memory.ReadBytes(frame.Address, sizeof(short));
            short originalFrame = BinaryPrimitives.ReadInt16LittleEndian(original);
            if (eventType == AllowInterruptionEvent &&
                originalFrame > 0 &&
                !_snapshots.ContainsKey(frame.Address))
            {
                pending.TryAdd(frame.Address, original);
            }
        }
    }

    private static string Clean(string name)
    {
        int suffix = name.IndexOfAny(['#', '{', ':', '^', '*', '!', '~']);
        return (suffix >= 0 ? name[..suffix] : name).Trim();
    }

    private long? ResolveOffset(uint encodedOffset) =>
        _memory.TryResolveOffset(encodedOffset, out long address)
            ? address
            : null;

    private void EnsureConnected()
    {
        if (!_memory.IsConnected)
            _memory.Connect();
    }

    private void EnsureDefinitions()
    {
        if (_definitions.SchemaCount == 0)
        {
            _definitions.LoadDirectory(
                RuntimeTagDefinitionLocator.ResolveCampaignEvolved());
        }
    }

    private void ResetForNewProcess()
    {
        if (_processId == _memory.ProcessId)
            return;
        _snapshots.Clear();
        _processId = _memory.ProcessId;
    }

    private sealed record EventSnapshot(byte[] Original, byte[] Patched);
}
