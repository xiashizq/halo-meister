using System.Collections.ObjectModel;
using HaloMeister.App.Services;

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
    public string SizeDisplay => $"{Size} B";
    public string TypeDisplay => string.Join(" ", Type.Replace('_', ' ')
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
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

/// <summary>
/// A field or reference edit that has been reviewed by the user but has not
/// yet been written to the running game.
/// </summary>
public sealed record RuntimeTagEditPatch(
    RuntimeTagFieldValue Field,
    byte[] Expected,
    byte[] Value,
    IReadOnlyList<RuntimeTagModBlockStep> Blocks,
    RuntimeTagEntry? ReferenceTarget)
{
    public string DisplayName => $"{Field.Name} ({Field.Type})";
    public string Detail => $"{Field.AddressDisplay} · {Value.Length} byte(s)";
}

/// <summary>
/// The per-open-tag state for staged runtime edits. A commit is deliberately
/// short lived: only its latest successful transaction can be undone.
/// </summary>
public sealed class RuntimeTagEditSession(RuntimeTagEntry tag)
{
    private readonly Dictionary<long, RuntimeTagEditPatch> _patches = [];

    public RuntimeTagEntry Tag { get; } = tag;
    public IReadOnlyCollection<RuntimeTagEditPatch> Patches => _patches.Values;
    public IReadOnlyList<RuntimeMemoryWrite>? LastCommit { get; private set; }
    public bool HasChanges => _patches.Count > 0;
    public bool CanUndo => LastCommit is { Count: > 0 };

    public void Stage(RuntimeTagEditPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (patch.Expected.Length == 0 || patch.Expected.Length != patch.Value.Length)
            throw new ArgumentException("A staged edit must have equally sized byte buffers.", nameof(patch));

        RuntimeTagEditPatch? overlap = _patches.Values.FirstOrDefault(existing =>
            existing.Field.Address != patch.Field.Address &&
            RangesOverlap(existing.Field.Address, existing.Value.Length,
                patch.Field.Address, patch.Value.Length));
        if (overlap is not null)
            throw new InvalidOperationException(
                $"'{patch.Field.Name}' overlaps the staged edit '{overlap.Field.Name}'.");

        if (_patches.TryGetValue(patch.Field.Address, out RuntimeTagEditPatch? existing))
            patch = patch with { Expected = existing.Expected };
        _patches[patch.Field.Address] = patch;
    }

    public void Discard() => _patches.Clear();

    public IReadOnlyList<RuntimeMemoryWrite> TakePendingWrites()
        => _patches.Values
            .OrderBy(patch => patch.Field.Address)
            .Select(patch => new RuntimeMemoryWrite(
                patch.Field.Address, patch.Expected, patch.Value))
            .ToArray();

    public void MarkCommitted(IReadOnlyList<RuntimeMemoryWrite> writes)
    {
        LastCommit = writes;
        _patches.Clear();
    }

    public IReadOnlyList<RuntimeMemoryWrite> TakeUndoWrites()
    {
        if (LastCommit is not { Count: > 0 } writes)
            throw new InvalidOperationException("There is no runtime tag transaction to undo.");

        return writes
            .OrderBy(write => write.Address)
            .Select(write => new RuntimeMemoryWrite(write.Address, write.Value, write.Expected))
            .ToArray();
    }

    public void MarkUndone() => LastCommit = null;

    private static bool RangesOverlap(long firstAddress, int firstLength, long secondAddress, int secondLength)
    {
        long firstEnd = checked(firstAddress + firstLength);
        long secondEnd = checked(secondAddress + secondLength);
        return firstAddress < secondEnd && secondAddress < firstEnd;
    }
}
