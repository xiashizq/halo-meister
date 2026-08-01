using System.Collections.ObjectModel;

namespace HaloMeister.App.Models;

public sealed record RuntimeTagEntry(
    int Index,
    uint Datum,
    string Group,
    string Name,
    long NameAddress,
    int RootCount,
    uint DataOffset,
    uint DefinitionOffset,
    long DataAddress,
    long DefinitionAddress)
{
    public string DisplayName => $"{Name}  [{Group}]";
    public string AddressDisplay => $"0x{DataAddress:X}";
    public string LeafName
    {
        get
        {
            int separator = Math.Max(Name.LastIndexOf('\\'), Name.LastIndexOf('/'));
            return separator >= 0 ? Name[(separator + 1)..] : Name;
        }
    }
    public string ReferenceDetail => $"[{Group}]  {Name}";
}

public sealed class RuntimeTagFieldValue
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required int Offset { get; init; }
    public required int Size { get; init; }
    public required long Address { get; init; }
    public required string Value { get; set; }
    public bool CanWrite { get; init; }
    public IReadOnlyList<string> AllowedTagGroups { get; init; } = [];
    public int ReferencedTagIndex { get; init; } = -1;
    public bool IsTagReference => Type == "tag_reference";
    public string? ChildBlockDefinition { get; init; }
    public int ChildCount { get; init; }
    public long ChildAddress { get; init; }
    public int ChildElementSize { get; init; }
    public bool CanOpenBlock =>
        ChildBlockDefinition is not null && ChildCount > 0 && ChildAddress > 0 && ChildElementSize > 0;
    public string OffsetDisplay => $"+0x{Offset:X}";
    public string AddressDisplay => $"0x{Address:X}";
}

public sealed class RuntimeTagViewState
{
    public ObservableCollection<RuntimeTagEntry> Tags { get; } = new();
    public ObservableCollection<RuntimeTagFieldValue> Fields { get; } = new();
}

public sealed record RuntimeTagTreeItem(
    string DisplayName,
    string Detail,
    RuntimeTagEntry? Tag)
{
    public bool IsFolder => Tag is null;
}
