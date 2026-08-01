using System.Buffers.Binary;

namespace HaloMeister.Core;

/// <summary>
/// The saved world actor table, found at
/// <c>UnrealWorldSaveGame.ActorState.SavedActors</c>.
///
/// The array value is an int32 count followed by that many records:
///
/// <code>
/// Record = PropertyList          // Class, BlamObjectGameStateIdentifier, Components
///          'None'
///          int32 ActorDataSize
///          byte[ActorDataSize]   // the actor's own saved property block
/// </code>
///
/// This is where every weapon, item, vehicle and character in a checkpoint is
/// listed, each with its blueprint class and a stable game-state identifier.
/// The native Blam simulation blob holds no weapon names at all, so this table
/// is the only place object identity is expressed as readable data.
/// </summary>
public sealed class BlamActorTable
{
    private readonly BlamPropertyNode _node;
    private readonly List<BlamActorRecord> _records;

    private BlamActorTable(BlamPropertyNode node, List<BlamActorRecord> records)
    {
        _node = node;
        _records = records;
        Records = records;
    }

    public IReadOnlyList<BlamActorRecord> Records { get; }

    /// <summary>
    /// Swaps two saved actor records while preserving each record's complete
    /// property and opaque data block. Controlled A30 checkpoint pairs show
    /// the two player weapon records exchanging positions when the equipped
    /// weapon changes.
    /// </summary>
    public void SwapRecordsByGameStateId(short first, short second)
    {
        int firstIndex = _records.FindIndex(record => record.GameStateId == first);
        int secondIndex = _records.FindIndex(record => record.GameStateId == second);
        if (firstIndex < 0 || secondIndex < 0)
            throw new InvalidDataException(
                $"Could not find both saved actors ({first}, {second}).");
        if (firstIndex == secondIndex) return;
        (_records[firstIndex], _records[secondIndex]) =
            (_records[secondIndex], _records[firstIndex]);
    }

    public static bool TryParse(BlamSaveDocument document, out BlamActorTable? table)
    {
        table = null;
        if (document.Find("SavedActors") is not { } node) return false;
        if (node.RawValue is not { Length: >= 4 })
            throw new InvalidDataException(
                $"SavedActors was found but holds no array data (children={node.Children?.Count}).");

        table = Parse(node);
        return true;
    }

    public static BlamActorTable Parse(BlamPropertyNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        byte[] value = node.RawValue
            ?? throw new InvalidDataException("SavedActors has no array data.");

        int count = BinaryPrimitives.ReadInt32LittleEndian(value);
        if (count is < 0 or > 100_000)
            throw new InvalidDataException($"SavedActors declares {count} entries.");

        var records = new List<BlamActorRecord>(count);
        var reader = new CevoReader(value, 4);
        for (int index = 0; index < count; index++)
        {
            int start = reader.Offset;
            List<BlamPropertyNode> properties = BlamSaveDocument.ReadList(ref reader, value.Length, 1);
            int dataSize = reader.ReadInt32();
            if (dataSize < 0 || reader.Offset + dataSize > value.Length)
                throw new InvalidDataException($"Actor record {index} declares {dataSize} data bytes.");

            byte[] data = reader.Slice(reader.Offset, dataSize).ToArray();
            reader.Offset += dataSize;

            foreach (BlamPropertyNode property in properties) property.Rebase(node.ValueOffset);
            records.Add(new BlamActorRecord(index, node.ValueOffset + start, properties, data));
        }

        return new BlamActorTable(node, records);
    }

    /// <summary>Writes the table back into the property it came from.</summary>
    public void Apply()
    {
        using var stream = new MemoryStream(_node.RawValue?.Length ?? 4096);
        CevoWriter.WriteInt32(stream, Records.Count);
        foreach (BlamActorRecord record in Records) record.Write(stream);
        _node.RawValue = stream.ToArray();
    }
}

public sealed class BlamActorRecord
{
    internal BlamActorRecord(int index, int offset, List<BlamPropertyNode> properties, byte[] actorData)
    {
        Index = index;
        Offset = offset;
        Properties = properties;
        ActorData = actorData;
    }

    public int Index { get; }

    /// <summary>Payload-absolute offset of this record.</summary>
    public int Offset { get; }

    public List<BlamPropertyNode> Properties { get; }

    /// <summary>The actor's own saved property block, kept opaque.</summary>
    public byte[] ActorData { get; }

    private BlamPropertyNode? ClassProperty
        => Properties.FirstOrDefault(property => property.Name == "Class");

    public string? ClassPath => ClassProperty?.AsSoftObject()?.Path;

    public string? ClassName => ClassProperty?.AsSoftObject()?.Name;

    /// <summary>
    /// Stable identifier shared with the native simulation. This is how the
    /// Blam side refers to the object, since it stores no names.
    /// </summary>
    public short? GameStateId
        => Properties.FirstOrDefault(p => p.Name == "BlamObjectGameStateIdentifier")?.AsInt16();

    public bool IsWeapon => ClassName?.Contains("WeaponActor", StringComparison.Ordinal) == true;

    public bool IsEquipment => ClassName?.Contains("EquipmentActor", StringComparison.Ordinal) == true;

    /// <summary>A readable weapon or item name derived from the blueprint class.</summary>
    public string DisplayName
    {
        get
        {
            string? name = ClassName;
            if (string.IsNullOrEmpty(name)) return "Unknown actor";
            if (name.StartsWith("BP_", StringComparison.Ordinal)) name = name[3..];
            if (name.EndsWith("_C", StringComparison.Ordinal)) name = name[..^2];
            foreach (string suffix in (string[])["WeaponActor", "EquipmentActor", "BipedActor", "VehicleActor"])
            {
                if (name.EndsWith(suffix, StringComparison.Ordinal))
                {
                    name = name[..^suffix.Length];
                    break;
                }
            }
            return name.Trim('_').Replace('_', ' ');
        }
    }

    /// <summary>
    /// Repoints this actor at a different blueprint. Changing the class alone
    /// does not change the native simulation object, so callers must treat the
    /// result as unverified until it has been loaded in game.
    /// </summary>
    public void SetClass(string path, string name)
    {
        BlamPropertyNode property = ClassProperty
            ?? throw new InvalidOperationException("This actor record has no Class property.");
        property.SetSoftObject(path, name);
    }

    internal void Write(Stream stream)
    {
        BlamSaveDocument.WriteList(stream, Properties);
        CevoWriter.WriteInt32(stream, ActorData.Length);
        stream.Write(ActorData);
    }

    public override string ToString()
        => $"#{Index} {ClassName} (gsid {GameStateId})";
}
