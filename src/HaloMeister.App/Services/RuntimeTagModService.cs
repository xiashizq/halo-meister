using System.Buffers.Binary;
using System.Text.Json;
using HaloMeister.App.Models;

namespace HaloMeister.App.Services;

public sealed class RuntimeTagModService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public void Save(RuntimeTagModDocument document, string path)
    {
        Validate(document);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporary, path, true);
    }

    public RuntimeTagModDocument Load(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024)
            throw new InvalidDataException("The tag mod is larger than the 16 MiB safety limit.");
        RuntimeTagModDocument document =
            JsonSerializer.Deserialize<RuntimeTagModDocument>(
                File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("The tag mod is empty.");
        Validate(document);
        return document;
    }

    public RuntimeTagModApplyResult Apply(
        RuntimeTagModDocument document,
        IReadOnlyList<RuntimeTagEntry> liveTags,
        RuntimeTagMemoryService memory)
    {
        Validate(document);
        int appliedTags = 0;
        var missing = new List<string>();
        var writes = new List<PlannedWrite>();

        foreach (RuntimeTagModTag modTag in document.Tags)
        {
            RuntimeTagEntry? liveTag = liveTags.FirstOrDefault(tag =>
                tag.Group.Equals(modTag.Group, StringComparison.OrdinalIgnoreCase) &&
                NormalizePath(tag.Name).Equals(
                    NormalizePath(modTag.Name), StringComparison.OrdinalIgnoreCase));
            if (liveTag is null)
            {
                missing.Add($"[{modTag.Group}] {modTag.Name}");
                continue;
            }
            if (liveTag.DataAddress <= 0)
                throw new InvalidDataException(
                    $"[{liveTag.Group}] {liveTag.Name} has no resolvable root data.");

            foreach (RuntimeTagModPatch patch in modTag.Patches)
            {
                long contextAddress = liveTag.DataAddress;
                foreach (RuntimeTagModBlockStep step in patch.Blocks)
                {
                    if (step.Offset < 0 || step.Element < 0 ||
                        step.ElementSize <= 0 || string.IsNullOrWhiteSpace(step.Definition))
                        throw InvalidPatch(liveTag, patch, "invalid block traversal");

                    byte[] header = memory.ReadBytes(
                        checked(contextAddress + step.Offset), 12);
                    int count = BinaryPrimitives.ReadInt32LittleEndian(header);
                    uint encodedOffset =
                        BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                    if (step.Element >= count)
                        throw InvalidPatch(
                            liveTag, patch,
                            $"block '{step.Definition}' has {count} element(s), not element {step.Element}");
                    long blockAddress = memory.ResolveOffset(encodedOffset);
                    contextAddress = checked(
                        blockAddress + (long)step.Element * step.ElementSize);
                }

                if (patch.Offset < 0 || patch.Size <= 0)
                    throw InvalidPatch(liveTag, patch, "invalid field range");
                long fieldAddress = checked(contextAddress + patch.Offset);
                byte[] bytes;
                if (!string.IsNullOrWhiteSpace(patch.ReferenceName))
                {
                    RuntimeTagEntry target = liveTags.FirstOrDefault(tag =>
                        tag.Group.Equals(
                            patch.ReferenceGroup, StringComparison.OrdinalIgnoreCase) &&
                        NormalizePath(tag.Name).Equals(
                            NormalizePath(patch.ReferenceName),
                            StringComparison.OrdinalIgnoreCase))
                        ?? throw InvalidPatch(
                            liveTag, patch,
                            $"reference target [{patch.ReferenceGroup}] {patch.ReferenceName} is not loaded");
                    bytes = memory.BuildTagReference(target);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(patch.Data))
                        throw InvalidPatch(liveTag, patch, "has no patch data");
                    try { bytes = Convert.FromBase64String(patch.Data); }
                    catch (FormatException)
                    {
                        throw InvalidPatch(liveTag, patch, "contains invalid base64 data");
                    }
                }

                if (bytes.Length != patch.Size)
                    throw InvalidPatch(
                        liveTag, patch,
                        $"declares {patch.Size} byte(s) but contains {bytes.Length}");
                byte[] original = memory.ReadBytes(fieldAddress, bytes.Length);
                writes.Add(new PlannedWrite(fieldAddress, original, bytes, liveTag, patch));
            }
            appliedTags++;
        }

        int completed = 0;
        try
        {
            foreach (PlannedWrite write in writes)
            {
                memory.WriteVerified(write.Address, write.Value);
                completed++;
            }
        }
        catch (Exception applyError)
        {
            var rollbackErrors = new List<string>();
            for (int index = completed - 1; index >= 0; index--)
            {
                PlannedWrite write = writes[index];
                try { memory.WriteVerified(write.Address, write.Original); }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(
                        $"0x{write.Address:X}: {rollbackError.Message}");
                }
            }

            string rollback = rollbackErrors.Count == 0
                ? "All earlier writes were rolled back."
                : "Rollback also failed at " + string.Join("; ", rollbackErrors) + ".";
            throw new IOException(
                $"The mod failed after {completed:N0} write(s): {applyError.Message} {rollback}",
                applyError);
        }

        return new RuntimeTagModApplyResult(appliedTags, writes.Count, missing);
    }

    private static void Validate(RuntimeTagModDocument document)
    {
        if (!document.Format.Equals(
                RuntimeTagModDocument.CurrentFormat, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Unsupported tag mod format '{document.Format}'.");
        if (document.Version != RuntimeTagModDocument.CurrentVersion)
            throw new InvalidDataException(
                $"Unsupported tag mod version {document.Version}; expected " +
                $"{RuntimeTagModDocument.CurrentVersion}.");
        if (document.Tags.Count == 0)
            throw new InvalidDataException("The tag mod contains no tags.");
        if (document.Tags.Count > 10_000)
            throw new InvalidDataException("The tag mod exceeds the 10,000-tag safety limit.");
        int totalPatches = 0;
        foreach (RuntimeTagModTag tag in document.Tags)
        {
            if (tag.Group.Length != 4 || string.IsNullOrWhiteSpace(tag.Name))
                throw new InvalidDataException("A tag mod entry has an invalid tag identity.");
            if (tag.Patches.Count == 0)
                throw new InvalidDataException(
                    $"[{tag.Group}] {tag.Name} contains no field patches.");
            totalPatches = checked(totalPatches + tag.Patches.Count);
            foreach (RuntimeTagModPatch patch in tag.Patches)
            {
                if (patch.Size is <= 0 or > 4096)
                    throw new InvalidDataException(
                        $"[{tag.Group}] {tag.Name}, field '{patch.Field}' has an invalid size.");
                if (patch.Blocks.Count > 16)
                    throw new InvalidDataException(
                        $"[{tag.Group}] {tag.Name}, field '{patch.Field}' exceeds the " +
                        "16-level block traversal limit.");
            }
        }
        if (totalPatches > 100_000)
            throw new InvalidDataException("The tag mod exceeds the 100,000-patch safety limit.");
    }

    private static InvalidDataException InvalidPatch(
        RuntimeTagEntry tag,
        RuntimeTagModPatch patch,
        string message)
        => new($"[{tag.Group}] {tag.Name}, field '{patch.Field}': {message}.");

    private static string NormalizePath(string path)
        => path.Replace('/', '\\').Trim('\\');

    private sealed record PlannedWrite(
        long Address,
        byte[] Original,
        byte[] Value,
        RuntimeTagEntry Tag,
        RuntimeTagModPatch Patch);
}
