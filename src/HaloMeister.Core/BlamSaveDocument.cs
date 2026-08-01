using System.Buffers.Binary;
using System.Text;

namespace HaloMeister.Core;

/// <summary>
/// Reads and rewrites the property tree inside a decoded HALOCEVO payload.
///
/// Campaign Evolved does not use stock Unreal property serialisation. A
/// property is written as:
///
/// <code>
/// Property   = FString Name
///              TypeDescriptor Type
///              int32 Size
///              uint8 Flags
///              byte[Size] Value
///
/// TypeDescriptor = FString Name
///                  int32 ParameterCount
///                  TypeDescriptor[ParameterCount]
/// </code>
///
/// A property list is terminated by the bare name "None". Container values are
/// themselves property lists; a value is only treated as one when it parses
/// cleanly and consumes exactly <c>Size</c> bytes, which keeps genuinely
/// native structs such as DateTime opaque.
///
/// Only the structured portion is modelled. The multi-megabyte
/// <c>BlamSaveGame</c> object is the native Blam simulation and stays as
/// opaque bytes.
/// </summary>
public sealed class BlamSaveDocument
{
    private BlamSaveDocument(byte[] header, byte[] trailer, List<BlamPropertyNode> root)
    {
        Header = header;
        Trailer = trailer;
        Root = root;
    }

    /// <summary>Everything up to and including the byte after the save class name.</summary>
    public byte[] Header { get; }

    /// <summary>Whatever follows the terminator of the root property list.</summary>
    public byte[] Trailer { get; }

    public List<BlamPropertyNode> Root { get; }

    public static BlamSaveDocument Parse(ReadOnlySpan<byte> payload)
    {
        if (!payload.StartsWith("GVAS"u8))
            throw new InvalidDataException("The checkpoint payload does not begin with GVAS.");

        var reader = new CevoReader(payload, 4);
        int saveGameVersion = reader.ReadInt32();
        reader.ReadInt32();                                  // package version (UE4)
        if (saveGameVersion >= 3) reader.ReadInt32();        // package version (UE5)
        reader.Skip(10);                                     // engine version
        reader.ReadString();                                 // branch
        reader.ReadInt32();                                  // custom version format
        int customVersions = reader.ReadInt32();
        reader.Skip(checked(customVersions * 20));
        reader.ReadString();                                 // save game class
        reader.Skip(1);                                      // list marker

        byte[] header = payload[..reader.Offset].ToArray();
        List<BlamPropertyNode> root = ReadList(ref reader, payload.Length, 0);
        byte[] trailer = payload[reader.Offset..].ToArray();
        return new BlamSaveDocument(header, trailer, root);
    }

    public byte[] Serialize()
    {
        using var stream = new MemoryStream(Header.Length + Trailer.Length + 4096);
        stream.Write(Header);
        WriteList(stream, Root);
        stream.Write(Trailer);
        return stream.ToArray();
    }

    /// <summary>Finds the first property with this name anywhere in the tree.</summary>
    public BlamPropertyNode? Find(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Find(Root, name);
    }

    private static BlamPropertyNode? Find(IEnumerable<BlamPropertyNode> nodes, string name)
    {
        foreach (BlamPropertyNode node in nodes)
        {
            if (node.Name == name) return node;
            if (node.Children is { } children && Find(children, name) is { } match) return match;
        }
        return null;
    }

    internal static List<BlamPropertyNode> ReadList(ref CevoReader reader, int end, int depth)
    {
        if (depth > 16) throw new InvalidDataException("The property tree is nested too deeply.");

        var nodes = new List<BlamPropertyNode>();
        while (true)
        {
            if (reader.Offset >= end) throw new InvalidDataException("A property list ran past its extent.");
            BlamString name = reader.ReadString();
            if (name.Value == "None") return nodes;
            if (name.Value.Length == 0)
                throw new InvalidDataException($"Empty property name at 0x{reader.Offset:X}.");

            BlamTypeDescriptor type = ReadType(ref reader, 0);
            int size = reader.ReadInt32();
            byte flags = reader.ReadByte();
            if (size < 0 || reader.Offset + size > end)
                throw new InvalidDataException(
                    $"Property '{name.Value}' declares {size} value bytes, which overruns its container.");

            int valueOffset = reader.Offset;
            var node = new BlamPropertyNode(name, type, flags)
            {
                ValueOffset = valueOffset,
                ValueSize = size,
            };
            ReadValue(ref reader, node, size, depth);
            reader.Offset = valueOffset + size;
            nodes.Add(node);
        }
    }

    private static void ReadValue(ref CevoReader reader, BlamPropertyNode node, int size, int depth)
    {
        int start = reader.Offset;
        int end = start + size;
        ReadOnlySpan<byte> raw = reader.Slice(start, size);

        // A container's property list is followed by a remainder: four bytes
        // for an inline object, but several hundred for some structs. Keeping
        // it verbatim is what lets an edited tree serialise back exactly, so
        // its length is not constrained. A list must yield at least one
        // property to be accepted, which stops opaque blobs being mistaken
        // for containers.
        if (node.Type.Name == "ObjectProperty" && size > 8 &&
            BinaryPrimitives.ReadInt32LittleEndian(raw) == 1)
        {
            // Inline subobject: int32 1, class path, one marker byte, list.
            var probe = new CevoReader(reader.Buffer, start + 4);
            try
            {
                BlamString cls = probe.ReadString();
                probe.Skip(1);
                List<BlamPropertyNode> children = ReadList(ref probe, end, depth + 1);
                if (children.Count > 0 && probe.Offset <= end)
                {
                    node.ObjectClass = cls;
                    node.Children = children;
                    node.Tail = reader.Slice(probe.Offset, end - probe.Offset).ToArray();
                    return;
                }
            }
            catch (InvalidDataException) { /* fall through to raw */ }
        }

        if (node.Type.Name is "StructProperty" or "ObjectProperty" && size > 4)
        {
            var probe = new CevoReader(reader.Buffer, start);
            try
            {
                List<BlamPropertyNode> children = ReadList(ref probe, end, depth + 1);
                if (children.Count > 0 && probe.Offset <= end)
                {
                    node.Children = children;
                    node.Tail = reader.Slice(probe.Offset, end - probe.Offset).ToArray();
                    return;
                }
            }
            catch (InvalidDataException) { /* fall through to raw */ }
        }

        node.RawValue = raw.ToArray();
    }

    private static BlamTypeDescriptor ReadType(ref CevoReader reader, int depth)
    {
        if (depth > 8) throw new InvalidDataException("A property type is nested too deeply.");
        BlamString name = reader.ReadString();
        int count = reader.ReadInt32();
        if (count is < 0 or > 8)
            throw new InvalidDataException($"Type '{name.Value}' declares {count} parameters.");

        var parameters = new BlamTypeDescriptor[count];
        for (int index = 0; index < count; index++) parameters[index] = ReadType(ref reader, depth + 1);
        return new BlamTypeDescriptor(name, parameters);
    }

    internal static void WriteList(Stream stream, List<BlamPropertyNode> nodes)
    {
        foreach (BlamPropertyNode node in nodes) node.Write(stream);
        CevoWriter.WriteString(stream, BlamString.Ascii("None"));
    }
}

public sealed class BlamPropertyNode
{
    internal BlamPropertyNode(BlamString name, BlamTypeDescriptor type, byte flags)
    {
        NameString = name;
        Type = type;
        Flags = flags;
    }

    internal BlamString NameString { get; }
    public string Name => NameString.Value;
    public BlamTypeDescriptor Type { get; }
    public byte Flags { get; private set; }

    /// <summary>Offset of this property's value in the payload it was parsed from.</summary>
    public int ValueOffset { get; internal set; }

    /// <summary>Value length as originally parsed. Stale once the tree is edited.</summary>
    public int ValueSize { get; internal set; }

    /// <summary>Opaque value bytes, when this property was not decoded further.</summary>
    public byte[]? RawValue { get; set; }

    /// <summary>Class path when this is an inline subobject.</summary>
    internal BlamString? ObjectClass { get; set; }

    public string? ObjectClassPath => ObjectClass?.Value;

    public List<BlamPropertyNode>? Children { get; internal set; }

    /// <summary>Bytes between this container's list terminator and its end.</summary>
    internal byte[] Tail { get; set; } = [];

    public IEnumerable<BlamPropertyNode> Descendants()
    {
        foreach (BlamPropertyNode child in Children ?? [])
        {
            yield return child;
            foreach (BlamPropertyNode inner in child.Descendants()) yield return inner;
        }
    }

    /// <summary>Reads this property's value as a single length-prefixed string.</summary>
    public string? AsString()
    {
        if (RawValue is not { Length: >= 4 }) return null;
        var reader = new CevoReader(RawValue, 0);
        try { return reader.ReadString().Value; }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>Replaces a single length-prefixed string value.</summary>
    public void SetString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (RawValue is not { Length: >= 4 })
            throw new InvalidOperationException($"'{Name}' does not hold a string value.");

        var reader = new CevoReader(RawValue, 0);
        BlamString original = reader.ReadString();
        int tailOffset = reader.Offset;
        byte[] tail = RawValue[tailOffset..];

        using var stream = new MemoryStream(value.Length + tail.Length + 8);
        CevoWriter.WriteString(stream, original.WithValue(value));
        stream.Write(tail);
        RawValue = stream.ToArray();
    }

    public int? AsInt32()
        => RawValue is { Length: 4 } ? BinaryPrimitives.ReadInt32LittleEndian(RawValue) : null;

    public void SetInt32(int value)
    {
        if (RawValue is not { Length: 4 })
            throw new InvalidOperationException($"'{Name}' does not hold an int32 value.");
        BinaryPrimitives.WriteInt32LittleEndian(RawValue, value);
    }

    public short? AsInt16()
        => RawValue is { Length: 2 } ? BinaryPrimitives.ReadInt16LittleEndian(RawValue) : null;

    /// <summary>
    /// A SoftObjectProperty value is an asset path, an asset name and a
    /// trailing int32.
    /// </summary>
    public (string Path, string Name)? AsSoftObject()
    {
        if (RawValue is not { Length: >= 8 }) return null;
        var reader = new CevoReader(RawValue, 0);
        try { return (reader.ReadString().Value, reader.ReadString().Value); }
        catch (InvalidDataException) { return null; }
    }

    public void SetSoftObject(string path, string name)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(name);
        if (RawValue is not { Length: >= 8 })
            throw new InvalidOperationException($"'{Name}' does not hold a soft object reference.");

        var reader = new CevoReader(RawValue, 0);
        BlamString originalPath = reader.ReadString();
        BlamString originalName = reader.ReadString();
        byte[] tail = RawValue[reader.Offset..];

        using var stream = new MemoryStream(path.Length + name.Length + tail.Length + 16);
        CevoWriter.WriteString(stream, originalPath.WithValue(path));
        CevoWriter.WriteString(stream, originalName.WithValue(name));
        stream.Write(tail);
        RawValue = stream.ToArray();
    }

    /// <summary>Shifts recorded offsets so nested reads report payload-absolute positions.</summary>
    internal void Rebase(int delta)
    {
        ValueOffset += delta;
        foreach (BlamPropertyNode child in Children ?? []) child.Rebase(delta);
    }

    /// <summary>
    /// BoolProperty carries no value bytes at all; its state lives in the tag
    /// flags. Every observed instance is 0x10 when set and 0x00 when clear.
    /// </summary>
    public bool? AsBool()
        => Type.Name == "BoolProperty" ? Flags != 0 : null;

    public void SetBool(bool value)
    {
        if (Type.Name != "BoolProperty")
            throw new InvalidOperationException($"'{Name}' is a {Type.Name}, not a BoolProperty.");
        Flags = value ? (byte)0x10 : (byte)0x00;
    }

    internal void Write(Stream stream)
    {
        CevoWriter.WriteString(stream, NameString);
        Type.Write(stream);

        byte[] value = BuildValue();
        CevoWriter.WriteInt32(stream, value.Length);
        stream.WriteByte(Flags);
        stream.Write(value);
    }

    private byte[] BuildValue()
    {
        if (Children is null) return RawValue ?? [];

        using var stream = new MemoryStream(256);
        if (ObjectClass is { } cls)
        {
            CevoWriter.WriteInt32(stream, 1);
            CevoWriter.WriteString(stream, cls);
            stream.WriteByte(0);
        }
        BlamSaveDocument.WriteList(stream, Children);
        stream.Write(Tail);
        return stream.ToArray();
    }

    public override string ToString()
        => $"{Name} : {Type}";
}

public sealed class BlamTypeDescriptor
{
    internal BlamTypeDescriptor(BlamString name, IReadOnlyList<BlamTypeDescriptor> parameters)
    {
        NameString = name;
        Parameters = parameters;
    }

    internal BlamString NameString { get; }
    public string Name => NameString.Value;
    public IReadOnlyList<BlamTypeDescriptor> Parameters { get; }

    internal void Write(Stream stream)
    {
        CevoWriter.WriteString(stream, NameString);
        CevoWriter.WriteInt32(stream, Parameters.Count);
        foreach (BlamTypeDescriptor parameter in Parameters) parameter.Write(stream);
    }

    public override string ToString()
        => Parameters.Count == 0 ? Name : $"{Name}<{string.Join(",", Parameters)}>";
}

/// <summary>
/// A length-prefixed string. The encoding is carried so that rewriting a
/// value cannot silently change an ASCII string into UTF-16 or vice versa.
/// </summary>
public readonly record struct BlamString(string Value, bool IsUnicode)
{
    public static BlamString Ascii(string value) => new(value, false);

    public BlamString WithValue(string value) => new(value, IsUnicode);
}

internal ref struct CevoReader(ReadOnlySpan<byte> buffer, int offset)
{
    public ReadOnlySpan<byte> Buffer { get; } = buffer;
    public int Offset { get; set; } = offset;

    public void Skip(int count)
    {
        if (count < 0 || Offset + count > Buffer.Length)
            throw new InvalidDataException("The checkpoint payload ended unexpectedly.");
        Offset += count;
    }

    public ReadOnlySpan<byte> Slice(int start, int length)
    {
        if (start < 0 || length < 0 || start + length > Buffer.Length)
            throw new InvalidDataException("The checkpoint payload ended unexpectedly.");
        return Buffer.Slice(start, length);
    }

    public byte ReadByte()
    {
        if (Offset >= Buffer.Length) throw new InvalidDataException("The checkpoint payload ended unexpectedly.");
        return Buffer[Offset++];
    }

    public int ReadInt32()
    {
        if (Offset + 4 > Buffer.Length) throw new InvalidDataException("The checkpoint payload ended unexpectedly.");
        int value = BinaryPrimitives.ReadInt32LittleEndian(Buffer[Offset..]);
        Offset += 4;
        return value;
    }

    public BlamString ReadString()
    {
        int length = ReadInt32();
        if (length == 0) return BlamString.Ascii(string.Empty);

        if (length > 0)
        {
            if (length > 1 << 20) throw new InvalidDataException($"Implausible string length {length}.");
            ReadOnlySpan<byte> bytes = Slice(Offset, length);
            if (bytes[^1] != 0) throw new InvalidDataException("A string was not null terminated.");
            Offset += length;
            return new BlamString(Encoding.UTF8.GetString(bytes[..^1]), IsUnicode: false);
        }

        int chars = -length;
        if (chars > 1 << 20) throw new InvalidDataException($"Implausible string length {length}.");
        ReadOnlySpan<byte> raw = Slice(Offset, chars * 2);
        Offset += chars * 2;
        return new BlamString(Encoding.Unicode.GetString(raw[..^2]), IsUnicode: true);
    }
}

internal static class CevoWriter
{
    public static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    public static void WriteString(Stream stream, BlamString value)
    {
        if (value.Value.Length == 0 && !value.IsUnicode)
        {
            WriteInt32(stream, 0);
            return;
        }

        if (value.IsUnicode)
        {
            byte[] bytes = Encoding.Unicode.GetBytes(value.Value);
            WriteInt32(stream, -(value.Value.Length + 1));
            stream.Write(bytes);
            stream.Write("\0\0"u8);
            return;
        }

        byte[] ascii = Encoding.UTF8.GetBytes(value.Value);
        WriteInt32(stream, ascii.Length + 1);
        stream.Write(ascii);
        stream.WriteByte(0);
    }
}
