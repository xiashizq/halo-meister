using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HaloMeister.App.Models;

namespace HaloMeister.App.Services;

public sealed class RuntimeTagDefinitionService
{
    private readonly Dictionary<string, TagSchema> _schemas =
        new(StringComparer.OrdinalIgnoreCase);

    public string? DirectoryPath { get; private set; }
    public int SchemaCount => _schemas.Count;

    public void LoadDirectory(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Tag definition directory not found: {path}");

        var rawSchemas = new Dictionary<string, RawTagSchema>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(path, "*.json"))
        {
            if (Path.GetFileName(file).StartsWith('_')) continue;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(file));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("tag", out JsonElement tagElement) ||
                !root.TryGetProperty("block", out JsonElement blockElement) ||
                !root.TryGetProperty("blocks", out JsonElement blocksElement) ||
                !root.TryGetProperty("structs", out JsonElement structsElement))
                continue;

            string? tag = tagElement.GetString();
            string? rootBlock = blockElement.GetString();
            if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(rootBlock)) continue;

            rawSchemas[NormalizeGroup(tag)] = new RawTagSchema(
                NormalizeGroup(
                    root.TryGetProperty("parent_tag", out JsonElement parent)
                        ? parent.GetString() ?? ""
                        : ""),
                rootBlock,
                blocksElement.Clone(),
                structsElement.Clone(),
                root.TryGetProperty("arrays", out JsonElement arrays) ? arrays.Clone() : default);
        }

        _schemas.Clear();
        foreach (string tag in rawSchemas.Keys)
            BuildSchema(tag, rawSchemas, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        DirectoryPath = path;
    }

    public int? GetRootSize(string group)
    {
        if (!TryGetRootStruct(group, out _, out JsonElement rootStruct)) return null;
        return rootStruct.TryGetProperty("size", out JsonElement size) ? size.GetInt32() : null;
    }

    public bool HasSchema(string group) => _schemas.ContainsKey(NormalizeGroup(group));

    /// <summary>
    /// Returns whether a concrete runtime tag group satisfies one of a
    /// reference field's allowed groups. Baboon uses abstract parent groups
    /// such as [unit]; live entries use concrete children such as [bipd] and
    /// [vehi], so an exact four-CC comparison is insufficient.
    /// </summary>
    public bool IsTagGroupCompatible(
        string candidateGroup,
        IReadOnlyCollection<string> allowedGroups)
    {
        if (allowedGroups.Count == 0) return true;

        string group = NormalizeGroup(candidateGroup);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (group.Length > 0 && visited.Add(group))
        {
            if (allowedGroups.Contains(group, StringComparer.OrdinalIgnoreCase))
                return true;
            if (!_schemas.TryGetValue(group, out TagSchema? schema))
                break;
            group = schema.ParentGroup;
        }
        return false;
    }

    public IReadOnlyList<RuntimeTagFieldValue> ReadRootFields(
        string group,
        long baseAddress,
        Func<long, int, byte[]> read,
        Func<uint, long?> resolve)
    {
        if (!TryGetRootStruct(group, out TagSchema? schema, out JsonElement rootStruct))
            return [];

        var fields = new List<RuntimeTagFieldValue>();
        WalkStruct(
            schema!, rootStruct, baseAddress, 0, "", read, resolve,
            fields, new HashSet<string>());
        return fields;
    }

    public IReadOnlyList<RuntimeTagFieldValue> ReadBlockFields(
        string group,
        string blockDefinition,
        long blockAddress,
        int elementIndex,
        Func<long, int, byte[]> read,
        Func<uint, long?> resolve)
    {
        if (!_schemas.TryGetValue(NormalizeGroup(group), out TagSchema? schema) ||
            !schema.Blocks.TryGetValue(blockDefinition, out JsonElement block) ||
            !block.TryGetProperty("struct", out JsonElement structNameElement))
            return [];
        string? structName = structNameElement.GetString();
        if (structName is null ||
            !schema.Structs.TryGetValue(structName, out JsonElement structure) ||
            !structure.TryGetProperty("size", out JsonElement sizeElement))
            return [];
        int elementSize = sizeElement.GetInt32();
        long elementAddress = checked(blockAddress + (long)elementIndex * elementSize);
        var fields = new List<RuntimeTagFieldValue>();
        WalkStruct(
            schema, structure, elementAddress, 0, "", read, resolve,
            fields, new HashSet<string>());
        return fields;
    }

    public byte[] ParseValue(RuntimeTagFieldValue field, string text)
    {
        string value = text.Trim();
        byte[] bytes = new byte[field.Size];
        switch (field.Type)
        {
            case "real":
            case "real_fraction":
            case "angle":
                BinaryPrimitives.WriteSingleLittleEndian(
                    bytes, float.Parse(value, CultureInfo.InvariantCulture));
                return bytes;
            case "char_integer":
            case "char_block_index":
            case "char_enum":
                bytes[0] = unchecked((byte)sbyte.Parse(value, CultureInfo.InvariantCulture));
                return bytes;
            case "byte_integer":
                bytes[0] = byte.Parse(value, CultureInfo.InvariantCulture);
                return bytes;
            case "byte_flags":
                bytes[0] = (byte)ParseUnsigned(value);
                return bytes;
            case "short_integer":
            case "short_block_index":
            case "short_enum":
                BinaryPrimitives.WriteInt16LittleEndian(
                    bytes, short.Parse(value, CultureInfo.InvariantCulture));
                return bytes;
            case "word_integer":
            case "word_flags":
                BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)ParseUnsigned(value));
                return bytes;
            case "long_integer":
            case "long_block_index":
            case "long_enum":
                BinaryPrimitives.WriteInt32LittleEndian(
                    bytes, int.Parse(value, CultureInfo.InvariantCulture));
                return bytes;
            case "dword_integer":
            case "long_flags":
            case "long_block_flags":
            case "string_id":
            case "tag":
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)ParseUnsigned(value));
                return bytes;
            case "int64_integer":
                BinaryPrimitives.WriteInt64LittleEndian(
                    bytes, long.Parse(value, CultureInfo.InvariantCulture));
                return bytes;
            case "string":
            case "long_string":
                byte[] encoded = Encoding.UTF8.GetBytes(value);
                if (encoded.Length >= bytes.Length)
                    throw new FormatException($"Text must be shorter than {bytes.Length} UTF-8 bytes.");
                encoded.CopyTo(bytes, 0);
                return bytes;
            default:
                if (IsFloatComposite(field.Type))
                {
                    string[] parts = value.Split(
                        ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length * 4 != field.Size)
                        throw new FormatException(
                            $"Expected {field.Size / 4} comma-separated numbers.");
                    for (int i = 0; i < parts.Length; i++)
                        BinaryPrimitives.WriteSingleLittleEndian(
                            bytes.AsSpan(i * 4, 4),
                            float.Parse(parts[i], CultureInfo.InvariantCulture));
                    return bytes;
                }
                throw new NotSupportedException($"{field.Type} fields are read-only.");
        }
    }

    private bool TryGetRootStruct(
        string group,
        out TagSchema? schema,
        out JsonElement rootStruct)
    {
        rootStruct = default;
        if (!_schemas.TryGetValue(NormalizeGroup(group), out schema)) return false;
        if (!schema.Blocks.TryGetValue(schema.RootBlock, out JsonElement block) ||
            !block.TryGetProperty("struct", out JsonElement structNameElement))
            return false;
        string? structName = structNameElement.GetString();
        return structName is not null && schema.Structs.TryGetValue(structName, out rootStruct);
    }

    private static int WalkStruct(
        TagSchema schema,
        JsonElement structure,
        long baseAddress,
        int baseOffset,
        string prefix,
        Func<long, int, byte[]> read,
        Func<uint, long?> resolve,
        List<RuntimeTagFieldValue> output,
        HashSet<string> stack)
    {
        if (!structure.TryGetProperty("fields", out JsonElement fields)) return 0;
        int offset = 0;
        foreach (JsonElement field in fields.EnumerateArray())
        {
            string type = field.GetProperty("type").GetString() ?? "";
            if (type is "terminator" or "explanation") continue;
            string name = CleanName(
                field.TryGetProperty("name", out JsonElement nameElement)
                    ? nameElement.GetString()
                    : null);

            if (type == "struct" &&
                field.TryGetProperty("definition", out JsonElement inlineDefinition))
            {
                string? structName = inlineDefinition.GetString();
                if (structName is not null &&
                    schema.Structs.TryGetValue(structName, out JsonElement child) &&
                    stack.Add(structName))
                {
                    offset += WalkStruct(
                        schema, child, baseAddress + offset, baseOffset + offset,
                        prefix + name + " / ", read, resolve, output, stack);
                    stack.Remove(structName);
                    continue;
                }
            }

            int size = GetFieldSize(schema, field);
            if (size < 0) break;
            if (size == 0) continue;

            long address = baseAddress + offset;
            byte[] bytes;
            try { bytes = read(address, size); }
            catch { bytes = []; }
            string? childDefinition = type == "block" &&
                                      field.TryGetProperty("definition", out JsonElement blockDefinition)
                ? blockDefinition.GetString()
                : null;
            int childCount = 0;
            long childAddress = 0;
            int childElementSize = 0;
            IReadOnlyList<string> allowedTagGroups = [];
            int referencedTagIndex = -1;
            if (type == "tag_reference")
            {
                if (field.TryGetProperty("definition", out JsonElement referenceDefinition) &&
                    referenceDefinition.ValueKind == JsonValueKind.Object &&
                    referenceDefinition.TryGetProperty("allowed", out JsonElement allowed) &&
                    allowed.ValueKind == JsonValueKind.Array)
                    allowedTagGroups = allowed.EnumerateArray()
                        .Select(item => NormalizeGroup(item.GetString() ?? ""))
                        .Where(item => item.Length == 4 && item.All(character => character is >= ' ' and <= '~'))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                if (bytes.Length >= 16)
                    referencedTagIndex =
                        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12, 2));
            }
            if (childDefinition is not null && bytes.Length >= 12)
            {
                childCount = BinaryPrimitives.ReadInt32LittleEndian(bytes);
                uint childOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
                childAddress = resolve(childOffset) ?? 0;
                if (schema.Blocks.TryGetValue(childDefinition, out JsonElement childBlock) &&
                    childBlock.TryGetProperty("struct", out JsonElement childStructElement) &&
                    childStructElement.GetString() is string childStructName &&
                    schema.Structs.TryGetValue(childStructName, out JsonElement childStruct) &&
                    childStruct.TryGetProperty("size", out JsonElement childSize))
                    childElementSize = childSize.GetInt32();
            }

            output.Add(new RuntimeTagFieldValue
            {
                Name = prefix + (string.IsNullOrWhiteSpace(name) ? $"<{type}>" : name),
                Type = type,
                Offset = baseOffset + offset,
                Size = size,
                Address = address,
                Value = FormatValue(type, bytes),
                CanWrite = IsWritable(type),
                AllowedTagGroups = allowedTagGroups,
                ReferencedTagIndex = referencedTagIndex,
                ChildBlockDefinition = childDefinition,
                ChildCount = childCount,
                ChildAddress = childAddress,
                ChildElementSize = childElementSize,
            });
            offset += size;
        }
        return structure.TryGetProperty("size", out JsonElement declaredSize)
            ? declaredSize.GetInt32()
            : offset;
    }

    private static int GetFieldSize(TagSchema schema, JsonElement field)
    {
        string type = field.GetProperty("type").GetString() ?? "";
        return type switch
        {
            "char_integer" or "byte_integer" or "char_block_index" or "char_enum" or
                "byte_flags" => 1,
            "short_integer" or "word_integer" or "short_block_index" or
                "custom_short_block_index" or "short_enum" or "word_flags" => 2,
            "long_integer" or "dword_integer" or "long_block_index" or
                "custom_long_block_index" or "long_enum" or "long_flags" or
                "long_block_flags" or "string_id" or "tag" or "real" or
                "real_fraction" or "angle" or "rgb_color" or "argb_color" => 4,
            "int64_integer" or "real_bounds" or "angle_bounds" or "fraction_bounds" or
                "real_point_2d" or "real_vector_2d" or "real_euler_angles_2d" => 8,
            "real_point_3d" or "real_vector_3d" or "real_euler_angles_3d" or
                "real_plane_2d" or "real_rgb_color" => 12,
            "real_quaternion" or "real_plane_3d" or "real_argb_color" => 16,
            "short_bounds" => 4,
            "rectangle_2d" => 8,
            "string" => 32,
            "long_string" => 256,
            "tag_reference" => 16,
            "block" => 12,
            "data" => 20,
            "tag_resource" => 8,
            "tag_interop" => 12,
            "pad" or "skip" =>
                field.TryGetProperty("definition", out JsonElement count) ? count.GetInt32() : 0,
            "array" => GetArraySize(schema, field),
            "custom" => 0,
            _ => -1,
        };
    }

    private static int GetArraySize(TagSchema schema, JsonElement field)
    {
        if (!field.TryGetProperty("definition", out JsonElement definitionElement))
            return -1;
        string? definition = definitionElement.GetString();
        if (definition is null ||
            !schema.Arrays.TryGetValue(definition, out JsonElement array) ||
            !array.TryGetProperty("count", out JsonElement countElement) ||
            !array.TryGetProperty("struct", out JsonElement structElement))
            return -1;
        string? structName = structElement.GetString();
        if (structName is null ||
            !schema.Structs.TryGetValue(structName, out JsonElement elementStruct) ||
            !elementStruct.TryGetProperty("size", out JsonElement sizeElement))
            return -1;
        return countElement.GetInt32() * sizeElement.GetInt32();
    }

    private static string FormatValue(string type, byte[] bytes)
    {
        if (bytes.Length == 0) return "<unreadable>";
        ReadOnlySpan<byte> span = bytes;
        try
        {
            return type switch
            {
                "real" or "real_fraction" or "angle" =>
                    BinaryPrimitives.ReadSingleLittleEndian(span)
                        .ToString("G9", CultureInfo.InvariantCulture),
                "char_integer" or "char_block_index" or "char_enum" =>
                    unchecked((sbyte)bytes[0]).ToString(),
                "byte_integer" => bytes[0].ToString(),
                "byte_flags" => $"0x{bytes[0]:X2}",
                "short_integer" or "short_block_index" or "short_enum" =>
                    BinaryPrimitives.ReadInt16LittleEndian(span).ToString(),
                "word_integer" => BinaryPrimitives.ReadUInt16LittleEndian(span).ToString(),
                "word_flags" => $"0x{BinaryPrimitives.ReadUInt16LittleEndian(span):X4}",
                "long_integer" or "long_block_index" or "long_enum" =>
                    BinaryPrimitives.ReadInt32LittleEndian(span).ToString(),
                "dword_integer" => BinaryPrimitives.ReadUInt32LittleEndian(span).ToString(),
                "long_flags" or "long_block_flags" or "string_id" or "tag" =>
                    $"0x{BinaryPrimitives.ReadUInt32LittleEndian(span):X8}",
                "int64_integer" => BinaryPrimitives.ReadInt64LittleEndian(span).ToString(),
                "string" or "long_string" => Encoding.UTF8.GetString(
                    bytes, 0,
                    Array.IndexOf(bytes, (byte)0) is int zero and >= 0 ? zero : bytes.Length),
                "block" => FormatBlock(bytes),
                "tag_reference" => Convert.ToHexString(bytes),
                _ when IsFloatComposite(type) => FormatFloatComposite(bytes),
                _ => Convert.ToHexString(bytes),
            };
        }
        catch { return Convert.ToHexString(bytes); }
    }

    private static string FormatBlock(byte[] bytes)
    {
        if (bytes.Length < 12) return Convert.ToHexString(bytes);
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        uint definition = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
        return $"count={count}, data=0x{offset:X8}, def=0x{definition:X8}";
    }

    private static string FormatFloatComposite(byte[] bytes)
    {
        var values = new string[bytes.Length / 4];
        for (int i = 0; i < values.Length; i++)
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4, 4))
                .ToString("G9", CultureInfo.InvariantCulture);
        return string.Join(", ", values);
    }

    private static bool IsWritable(string type) => type is
        "real" or "real_fraction" or "angle" or "char_integer" or "byte_integer" or
        "char_block_index" or "char_enum" or "byte_flags" or "short_integer" or
        "word_integer" or "short_block_index" or "short_enum" or "word_flags" or
        "long_integer" or "dword_integer" or "long_block_index" or "long_enum" or
        "long_flags" or "long_block_flags" or "string_id" or "tag" or "int64_integer" or
        "string" or "long_string" or "real_bounds" or "angle_bounds" or
        "fraction_bounds" or "real_point_2d" or "real_vector_2d" or
        "real_euler_angles_2d" or "real_point_3d" or "real_vector_3d" or
        "real_euler_angles_3d" or "real_plane_2d" or "real_rgb_color" or
        "real_quaternion" or "real_plane_3d" or "real_argb_color";

    private static bool IsFloatComposite(string type) => type is
        "real_bounds" or "angle_bounds" or "fraction_bounds" or "real_point_2d" or
        "real_vector_2d" or "real_euler_angles_2d" or "real_point_3d" or
        "real_vector_3d" or "real_euler_angles_3d" or "real_plane_2d" or
        "real_rgb_color" or "real_quaternion" or "real_plane_3d" or "real_argb_color";

    private static ulong ParseUnsigned(string value)
        => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : ulong.Parse(value, CultureInfo.InvariantCulture);

    private static string CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        int help = name.IndexOf('#');
        if (help >= 0) name = name[..help];
        return name.TrimEnd('!', '*', '^', '~', '`').Trim();
    }

    private static string NormalizeGroup(string group)
        => group.Trim().TrimEnd('\0');

    private TagSchema BuildSchema(
        string group,
        IReadOnlyDictionary<string, RawTagSchema> rawSchemas,
        HashSet<string> stack)
    {
        group = NormalizeGroup(group);
        if (_schemas.TryGetValue(group, out TagSchema? existing)) return existing;
        if (!rawSchemas.TryGetValue(group, out RawTagSchema? raw))
            throw new InvalidDataException($"Missing parent tag definition [{group}].");
        if (!stack.Add(group))
            throw new InvalidDataException($"Tag definition inheritance cycle at [{group}].");

        var blocks = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var structs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var arrays = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(raw.ParentGroup))
        {
            TagSchema parent = BuildSchema(raw.ParentGroup, rawSchemas, stack);
            Copy(parent.Blocks, blocks);
            Copy(parent.Structs, structs);
            Copy(parent.Arrays, arrays);
        }
        CopyProperties(raw.Blocks, blocks);
        CopyProperties(raw.Structs, structs);
        CopyProperties(raw.Arrays, arrays);

        stack.Remove(group);
        var schema = new TagSchema(raw.ParentGroup, raw.RootBlock, blocks, structs, arrays);
        _schemas[group] = schema;
        return schema;
    }

    private static void Copy(
        IReadOnlyDictionary<string, JsonElement> source,
        IDictionary<string, JsonElement> destination)
    {
        foreach ((string key, JsonElement value) in source) destination[key] = value;
    }

    private static void CopyProperties(
        JsonElement source,
        IDictionary<string, JsonElement> destination)
    {
        if (source.ValueKind != JsonValueKind.Object) return;
        foreach (JsonProperty property in source.EnumerateObject())
            destination[property.Name] = property.Value.Clone();
    }

    private sealed record RawTagSchema(
        string ParentGroup,
        string RootBlock,
        JsonElement Blocks,
        JsonElement Structs,
        JsonElement Arrays);

    private sealed record TagSchema(
        string ParentGroup,
        string RootBlock,
        IReadOnlyDictionary<string, JsonElement> Blocks,
        IReadOnlyDictionary<string, JsonElement> Structs,
        IReadOnlyDictionary<string, JsonElement> Arrays);
}
