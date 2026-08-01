namespace HaloMeister.Core;

/// <summary>
/// The decompressed save payload: a version byte, a "None"-terminated property list,
/// and a small trailer that is preserved verbatim.
/// </summary>
public sealed class BlamDocument
{
    public byte Version { get; set; }
    public List<BlamProperty> Root { get; set; } = new();
    public bool RootTerminated { get; set; } = true;
    public byte[] Trailer { get; set; } = Array.Empty<byte>();

    public IEnumerable<BlamProperty> AllProperties()
        => Root.SelectMany(p => p.Descend());

    /// <summary>Finds a property by slash-separated path, e.g. "GameProfile/PlayerTraining".</summary>
    public BlamProperty? Find(string path)
    {
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        List<BlamProperty>? level = Root;
        BlamProperty? current = null;

        foreach (string part in parts)
        {
            if (level is null) return null;
            current = level.FirstOrDefault(p =>
                string.Equals(p.DisplayName, part, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Name, part, StringComparison.OrdinalIgnoreCase));
            if (current is null) return null;
            level = current.Children;
        }

        return current;
    }

    public byte[] Serialize() => BlamWriter.Write(this);

    public static BlamDocument Parse(byte[] payload) => BlamReader.Read(payload);
}

public static class BlamReader
{
    /// <summary>
    /// How many (int32, FString) type descriptors follow the type name.
    /// Struct types carry both the struct name and its owning script package.
    /// </summary>
    internal static int TypeParamCount(string typeName) => typeName switch
    {
        "StructProperty" => 2,
        "MapProperty" => 2,
        "ArrayProperty" => 1,
        "SetProperty" => 1,
        "EnumProperty" => 1,
        "ByteProperty" => 1,
        _ => 0,
    };

    public static BlamDocument Read(byte[] payload)
    {
        if (payload.Length < 1)
            throw new BlamFormatException("Payload is empty.");

        var doc = new BlamDocument { Version = payload[0] };
        int pos = 1;
        (doc.Root, doc.RootTerminated) = ReadPropertyList(payload, ref pos, payload.Length);
        doc.Trailer = payload[pos..];
        return doc;
    }

    private static (List<BlamProperty> Properties, bool Terminated) ReadPropertyList(
        byte[] data, ref int pos, int end)
    {
        var list = new List<BlamProperty>();

        while (pos < end)
        {
            int propStart = pos;
            string name = BlamPrimitives.ReadString(data, ref pos);

            if (name == "None")
                return (list, true);

            if (name.Length == 0)
                throw new BlamFormatException($"Empty property name at offset 0x{propStart:X}.", propStart);

            var prop = new BlamProperty { Name = name };
            prop.TypeName = BlamPrimitives.ReadString(data, ref pos);

            int paramCount = TypeParamCount(prop.TypeName);
            for (int i = 0; i < paramCount; i++)
            {
                int count = BlamPrimitives.ReadInt32(data, ref pos);
                string value = BlamPrimitives.ReadString(data, ref pos);
                prop.TypeParams.Add(new BlamTypeParam(count, value));
            }

            prop.Index = BlamPrimitives.ReadInt32(data, ref pos);
            int size = BlamPrimitives.ReadInt32(data, ref pos);
            prop.Flags = BlamPrimitives.ReadByte(data, ref pos);

            if ((prop.Flags & BlamProperty.FlagHasArrayIndex) != 0)
                prop.ArrayIndex = BlamPrimitives.ReadInt32(data, ref pos);

            if (size < 0 || pos + size > end)
            {
                throw new BlamFormatException(
                    $"Property '{prop.Name}' ({prop.TypeName}) at offset 0x{propStart:X} declares a " +
                    $"payload of {size} byte(s), which does not fit in the remaining {end - pos} byte(s). " +
                    "The file is probably corrupt or uses an unknown property type.", propStart);
            }

            byte[] body = data[pos..(pos + size)];
            pos += size;

            DecodeBody(prop, body);
            list.Add(prop);
        }

        return (list, false);
    }

    private static void DecodeBody(BlamProperty prop, byte[] body)
    {
        switch (prop.TypeName)
        {
            case "StructProperty" when prop.HasCustomSerializer
                                       && prop.StructTypeName == "GameplayTagContainer":
                prop.Tags = ReadStringList(body, prop);
                return;

            case "StructProperty" when !prop.HasCustomSerializer:
            {
                int inner = 0;
                (List<BlamProperty> children, bool terminated) = ReadPropertyList(body, ref inner, body.Length);
                if (inner != body.Length)
                {
                    // Trailing bytes inside a struct we thought we understood: keep it raw
                    // rather than risk writing something the game cannot load.
                    prop.Raw = body;
                    return;
                }
                prop.Children = children;
                prop.ChildrenTerminated = terminated;
                return;
            }

            case "ArrayProperty" when prop.StructTypeName == "StrProperty":
                prop.StringArray = ReadStringList(body, prop);
                return;

            default:
                prop.Raw = body;
                return;
        }
    }

    private static List<string>? ReadStringList(byte[] body, BlamProperty prop)
    {
        try
        {
            int pos = 0;
            int count = BlamPrimitives.ReadInt32(body, ref pos);
            if (count < 0 || count > 1_000_000) throw new BlamFormatException("Implausible element count.");

            var items = new List<string>(count);
            for (int i = 0; i < count; i++)
                items.Add(BlamPrimitives.ReadString(body, ref pos));

            if (pos != body.Length) throw new BlamFormatException("Trailing bytes after element list.");
            return items;
        }
        catch (BlamFormatException)
        {
            // Unknown variant: fall back to preserving the bytes untouched.
            prop.Raw = body;
            return null;
        }
    }
}

public static class BlamWriter
{
    public static byte[] Write(BlamDocument doc)
    {
        var output = new List<byte> { doc.Version };
        WritePropertyList(output, doc.Root, doc.RootTerminated);
        output.AddRange(doc.Trailer);
        return output.ToArray();
    }

    private static void WritePropertyList(List<byte> dst, List<BlamProperty> properties, bool terminated)
    {
        foreach (BlamProperty prop in properties)
            WriteProperty(dst, prop);

        if (terminated)
            BlamPrimitives.WriteString(dst, "None");
    }

    private static void WriteProperty(List<byte> dst, BlamProperty prop)
    {
        byte[] body = EncodeBody(prop);

        BlamPrimitives.WriteString(dst, prop.Name);
        BlamPrimitives.WriteString(dst, prop.TypeName);

        foreach (BlamTypeParam param in prop.TypeParams)
        {
            BlamPrimitives.WriteInt32(dst, param.Count);
            BlamPrimitives.WriteString(dst, param.Value);
        }

        BlamPrimitives.WriteInt32(dst, prop.Index);
        BlamPrimitives.WriteInt32(dst, body.Length);
        dst.Add(prop.Flags);

        if ((prop.Flags & BlamProperty.FlagHasArrayIndex) != 0)
            BlamPrimitives.WriteInt32(dst, prop.ArrayIndex ?? 0);

        dst.AddRange(body);
    }

    private static byte[] EncodeBody(BlamProperty prop)
    {
        if (prop.Tags is { } tags) return EncodeStringList(tags);
        if (prop.StringArray is { } array) return EncodeStringList(array);

        if (prop.Children is { } children)
        {
            var inner = new List<byte>();
            WritePropertyList(inner, children, prop.ChildrenTerminated);
            return inner.ToArray();
        }

        return prop.Raw;
    }

    private static byte[] EncodeStringList(List<string> items)
    {
        var buffer = new List<byte>();
        BlamPrimitives.WriteInt32(buffer, items.Count);
        foreach (string item in items)
            BlamPrimitives.WriteString(buffer, item);
        return buffer.ToArray();
    }
}
