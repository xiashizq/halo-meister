using System.Buffers.Binary;

namespace HaloMeister.Core;

/// <summary>A type descriptor that follows a property's type name (count + name).</summary>
public readonly record struct BlamTypeParam(int Count, string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// One tagged property in the save.
///
/// Wire layout:
///   FString  Name                        ("None" alone terminates a property list)
///   FString  TypeName
///   N x (int32 Count, FString Value)     N depends on TypeName (see BlamReader.TypeParamCount)
///   int32    Index                       always 0 in observed saves
///   int32    PayloadSize
///   byte     Flags
///   int32    ArrayIndex                  only present when (Flags &amp; 0x01) != 0
///   byte[PayloadSize] Payload
///
/// Flags bits observed in the wild:
///   0x01  payload is one element of a fixed-size array; an int32 index precedes it
///   0x08  the struct uses a custom (non-tagged) serializer, e.g. GameplayTagContainer
///   0x10  the value of a BoolProperty is true
/// </summary>
public sealed class BlamProperty
{
    public const byte FlagHasArrayIndex = 0x01;
    public const byte FlagCustomStruct = 0x08;
    public const byte FlagBoolValue = 0x10;

    public string Name { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public List<BlamTypeParam> TypeParams { get; } = new();
    public int Index { get; set; }
    public byte Flags { get; set; }
    public int? ArrayIndex { get; set; }

    /// <summary>Nested properties, for a StructProperty that uses tagged serialization.</summary>
    public List<BlamProperty>? Children { get; set; }

    /// <summary>True when the child list ended with an explicit "None" marker.</summary>
    public bool ChildrenTerminated { get; set; } = true;

    /// <summary>Tag list, for a GameplayTagContainer struct.</summary>
    public List<string>? Tags { get; set; }

    /// <summary>Element list, for an ArrayProperty of StrProperty.</summary>
    public List<string>? StringArray { get; set; }

    /// <summary>Raw payload for every other type (int, bool, unknown structs, ...).</summary>
    public byte[] Raw { get; set; } = Array.Empty<byte>();

    /// <summary>The struct/inner type name, e.g. "BlamGameProgression" or "StrProperty".</summary>
    public string? StructTypeName => TypeParams.Count > 0 ? TypeParams[0].Value : null;

    public bool HasCustomSerializer => (Flags & FlagCustomStruct) != 0;

    /// <summary>Display name including the array index, e.g. "TrainingBlobBitvectorLow[1]".</summary>
    public string DisplayName => ArrayIndex is { } i ? $"{Name}[{i}]" : Name;

    public bool IsBool => TypeName == "BoolProperty";
    public bool IsInt => TypeName == "IntProperty" && Raw.Length == 4;

    public bool BoolValue
    {
        get => (Flags & FlagBoolValue) != 0;
        set => Flags = value ? (byte)(Flags | FlagBoolValue) : (byte)(Flags & ~FlagBoolValue);
    }

    public int IntValue
    {
        get => Raw.Length == 4 ? BinaryPrimitives.ReadInt32LittleEndian(Raw) : 0;
        set
        {
            byte[] b = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(b, value);
            Raw = b;
        }
    }

    /// <summary>Best-effort human readable value, used by the raw property inspector.</summary>
    public string ValuePreview
    {
        get
        {
            if (IsBool) return BoolValue ? "true" : "false";
            if (IsInt) return IntValue.ToString();
            if (Tags is { } t) return $"{t.Count} tag(s)";
            if (StringArray is { } a) return a.Count == 0 ? "(empty)" : string.Join(", ", a);
            if (Children is { } c) return $"{c.Count} field(s)";
            return $"{Raw.Length} byte(s)";
        }
    }

    /// <summary>Depth-first walk over this property and everything beneath it.</summary>
    public IEnumerable<BlamProperty> Descend()
    {
        yield return this;
        if (Children is null) yield break;
        foreach (BlamProperty child in Children)
        {
            foreach (BlamProperty n in child.Descend()) yield return n;
        }
    }
}
