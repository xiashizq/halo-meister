using System.Buffers.Binary;
using HaloMeister.App.Models;

namespace HaloMeister.App.Services;

public sealed record SuperPunchResult(int EffectCount, float Multiplier);

public sealed class SuperPunchService
{
    private const int MaxBlockElements = 64;
    private readonly RuntimeTagMemoryService _memory = RuntimeTagMemoryService.Current;
    private readonly RuntimeTagDefinitionService _definitions = new();
    private readonly Dictionary<long, AccelerationSnapshot> _accelerationSnapshots = [];
    private int _processId;

    public static SuperPunchService Current { get; } = new();
    public bool IsActive => _accelerationSnapshots.Count > 0;

    public SuperPunchResult Enable(float multiplier)
    {
        if (!float.IsFinite(multiplier) || multiplier is < 2f or > 100f)
            throw new ArgumentOutOfRangeException(
                nameof(multiplier),
                "Super Punch strength must be between 2x and 100x.");

        EnsureConnected();
        EnsureDefinitions();
        if (_processId != _memory.ProcessId)
        {
            _accelerationSnapshots.Clear();
            _processId = _memory.ProcessId;
        }

        IReadOnlyList<RuntimeTagEntry> tags = _memory.ReadTags();
        Dictionary<int, RuntimeTagEntry> tagsByIndex =
            tags.ToDictionary(tag => tag.Index);
        HashSet<int> meleeDamageIndices = FindMeleeDamageTags(tags, tagsByIndex);
        if (meleeDamageIndices.Count == 0)
        {
            throw new InvalidOperationException(
                "No loaded weapon melee-damage effects were found. Load or resume a campaign checkpoint, then try again.");
        }

        var writes = new List<(long Address, byte[] Original, byte[] Boosted)>();
        foreach (int tagIndex in meleeDamageIndices)
        {
            if (!tagsByIndex.TryGetValue(tagIndex, out RuntimeTagEntry? damage) ||
                damage.DataAddress <= 0)
                continue;

            RuntimeTagFieldValue? acceleration = _definitions
                .ReadRootFields(
                    damage.Group,
                    damage.DataAddress,
                    _memory.ReadBytes,
                    ResolveOffset)
                .FirstOrDefault(field =>
                    field.Type == "real" &&
                    field.Name.StartsWith(
                        "instantaneous acceleration",
                        StringComparison.OrdinalIgnoreCase));
            if (acceleration is null || acceleration.Size != sizeof(float))
                continue;

            byte[] current = _memory.ReadBytes(acceleration.Address, sizeof(float));
            byte[] original = _accelerationSnapshots.TryGetValue(
                acceleration.Address,
                out AccelerationSnapshot? saved)
                ? saved.Original
                : current;
            float baseline = BinaryPrimitives.ReadSingleLittleEndian(original);
            if (!float.IsFinite(baseline) || baseline <= 0f)
                continue;

            float boosted = Math.Min(baseline * multiplier, 10_000f);
            byte[] value = new byte[sizeof(float)];
            BinaryPrimitives.WriteSingleLittleEndian(value, boosted);
            writes.Add((acceleration.Address, original, value));
        }

        if (writes.Count == 0)
        {
            throw new InvalidOperationException(
                "Loaded melee effects were found, but none had a positive instantaneous-acceleration value to amplify.");
        }

        foreach ((long address, byte[] original, byte[] boosted) in writes)
        {
            _memory.WriteVerified(address, boosted);
            _accelerationSnapshots[address] =
                new AccelerationSnapshot(original, boosted);
        }
        return new SuperPunchResult(writes.Count, multiplier);
    }

    public int Restore()
    {
        if (_accelerationSnapshots.Count == 0)
            return 0;
        EnsureConnected();
        if (_processId != _memory.ProcessId)
        {
            _accelerationSnapshots.Clear();
            _processId = _memory.ProcessId;
            return 0;
        }

        int restored = 0;
        foreach ((long address, AccelerationSnapshot snapshot) in
                 _accelerationSnapshots.ToArray())
        {
            byte[] current;
            try { current = _memory.ReadBytes(address, sizeof(float)); }
            catch
            {
                _accelerationSnapshots.Remove(address);
                continue;
            }
            if (!current.AsSpan().SequenceEqual(snapshot.Boosted))
            {
                _accelerationSnapshots.Remove(address);
                continue;
            }
            _memory.WriteVerified(address, snapshot.Original);
            _accelerationSnapshots.Remove(address);
            restored++;
        }
        return restored;
    }

    private HashSet<int> FindMeleeDamageTags(
        IReadOnlyList<RuntimeTagEntry> tags,
        IReadOnlyDictionary<int, RuntimeTagEntry> tagsByIndex)
    {
        var result = new HashSet<int>();
        foreach (RuntimeTagEntry weapon in tags.Where(tag =>
                     tag.Group.Equals("weap", StringComparison.OrdinalIgnoreCase) &&
                     tag.DataAddress > 0))
        {
            IReadOnlyList<RuntimeTagFieldValue> root = _definitions.ReadRootFields(
                weapon.Group,
                weapon.DataAddress,
                _memory.ReadBytes,
                ResolveOffset);
            CollectMeleeDamageReferences(
                weapon.Group,
                root,
                tagsByIndex,
                result,
                false,
                0);
        }
        return result;
    }

    private void CollectMeleeDamageReferences(
        string group,
        IReadOnlyList<RuntimeTagFieldValue> fields,
        IReadOnlyDictionary<int, RuntimeTagEntry> tagsByIndex,
        ISet<int> result,
        bool meleeContext,
        int depth)
    {
        if (depth > 3)
            return;

        foreach (RuntimeTagFieldValue field in fields)
        {
            bool isMelee = meleeContext ||
                           field.Name.Contains("melee", StringComparison.OrdinalIgnoreCase);
            if (field.Type == "tag_reference" &&
                isMelee &&
                field.Name.Contains("damage", StringComparison.OrdinalIgnoreCase) &&
                tagsByIndex.TryGetValue(field.ReferencedTagIndex, out RuntimeTagEntry? target) &&
                target.Group.Equals("jpt!", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(target.Index);
            }

            if (field.Type != "block" ||
                field.ChildBlockDefinition is null ||
                field.ChildAddress <= 0 ||
                field.ChildElementSize <= 0 ||
                (!isMelee && !field.Name.Contains(
                    "damage parameters",
                    StringComparison.OrdinalIgnoreCase)))
                continue;

            int count = Math.Min(field.ChildCount, MaxBlockElements);
            for (int element = 0; element < count; element++)
            {
                IReadOnlyList<RuntimeTagFieldValue> children =
                    _definitions.ReadBlockFields(
                        group,
                        field.ChildBlockDefinition,
                        field.ChildAddress,
                        element,
                        _memory.ReadBytes,
                        ResolveOffset);
                CollectMeleeDamageReferences(
                    group,
                    children,
                    tagsByIndex,
                    result,
                    isMelee,
                    depth + 1);
            }
        }
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
        if (_definitions.SchemaCount > 0)
            return;
        _definitions.LoadDirectory(
            RuntimeTagDefinitionLocator.ResolveCampaignEvolved());
    }

    private sealed record AccelerationSnapshot(byte[] Original, byte[] Boosted);
}
