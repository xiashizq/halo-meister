using System.Security.Cryptography;
using System.Text;

namespace HaloMeister.Core;

public enum WgsGameSaveKind
{
    Unknown,
    Checkpoint,
    Progression,
}

public sealed record WgsGameSaveInfo(
    string Path,
    WgsGameSaveKind Kind,
    long Size,
    DateTime LastWriteTimeUtc,
    string Sha256,
    int? GvasOffset,
    string? Build,
    string? ScenarioCode,
    string? ScenarioTitle,
    string? Difficulty,
    string? InternalCheckpoint,
    IReadOnlyList<string> ActiveSkulls,
    int? CompressedChunkCount,
    long? UncompressedSimulationSize,
    int? CompressedDataOffset,
    string FormatDetail)
{
    public string KindLabel => Kind switch
    {
        WgsGameSaveKind.Checkpoint => "Checkpoint / resume",
        WgsGameSaveKind.Progression => "Local progression",
        _ => "Unknown",
    };

    public string ScenarioDisplay => ScenarioCode is null
        ? "Unknown scenario"
        : ScenarioTitle is null ? ScenarioCode : $"{ScenarioCode} — {ScenarioTitle}";
}

public static class WgsGameSave
{
    private static readonly byte[] HaloCevoMagic = "HALOCEVO"u8.ToArray();
    private static readonly byte[] GvasMagic = "GVAS"u8.ToArray();

    public static WgsGameSaveInfo Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes = File.ReadAllBytes(path);
        return Inspect(path, bytes, File.GetLastWriteTimeUtc(path));
    }

    public static WgsGameSaveInfo Inspect(
        string sourceLabel,
        ReadOnlySpan<byte> bytes,
        DateTime? lastWriteTimeUtc = null)
    {
        WgsGameSaveKind kind;
        string detail;

        if (bytes.StartsWith(HaloCevoMagic))
        {
            kind = WgsGameSaveKind.Checkpoint;
            detail = "HALOCEVO wrapper with embedded Unreal GVAS SaveSlotGame";
        }
        else if (bytes.StartsWith(GvasMagic) &&
                 ContainsAscii(bytes, "/Script/BlamEngine.BlamProgressLocalPlayerSaveGame"))
        {
            kind = WgsGameSaveKind.Progression;
            detail = "Unreal GVAS BlamProgressLocalPlayerSaveGame";
        }
        else
        {
            kind = WgsGameSaveKind.Unknown;
            detail = "Unrecognized WGS data";
        }

        int gvasOffset = IndexOf(bytes, GvasMagic);
        IReadOnlyList<AsciiRun> headerStrings = ExtractAsciiRuns(
            bytes[..Math.Min(bytes.Length, 16 * 1024)], 4);

        string? build = headerStrings
            .Select(run => run.Text)
            .FirstOrDefault(text => text.StartsWith("++Meteorite", StringComparison.Ordinal));

        Mission? mission = null;
        if (kind == WgsGameSaveKind.Checkpoint)
        {
            ReadOnlySpan<byte> scenarioHeader = bytes[..Math.Min(bytes.Length, 8 * 1024)];
            int earliestMissionOffset = int.MaxValue;
            foreach (Mission candidate in Catalog.Missions)
            {
                int offset = IndexOf(scenarioHeader, Encoding.ASCII.GetBytes(candidate.Code));
                if (offset < 0 || offset >= earliestMissionOffset)
                    continue;
                earliestMissionOffset = offset;
                mission = candidate;
            }
        }

        byte[] metadataBytes = bytes[..Math.Min(bytes.Length, 8 * 1024)].ToArray();
        string? difficulty = null;
        foreach (string value in Catalog.Difficulties)
        {
            if (value is "Remix" or "Remix.Deathless")
                continue;
            if (!ContainsAscii(metadataBytes, $"::{value}"))
                continue;
            difficulty = value;
            break;
        }

        string? checkpoint = headerStrings
            .Select(run => run.Text)
            .FirstOrDefault(IsInternalCheckpointName);

        var skulls = new List<string>();
        if (kind == WgsGameSaveKind.Checkpoint)
        {
            foreach (string skull in Catalog.Skulls)
            {
                if (ContainsAscii(metadataBytes, skull))
                    skulls.Add(skull);
            }
        }

        CompressionLayout? compression = kind == WgsGameSaveKind.Checkpoint
            ? TryReadCompressionLayout(bytes)
            : null;

        return new WgsGameSaveInfo(
            sourceLabel,
            kind,
            bytes.Length,
            lastWriteTimeUtc ?? DateTime.MinValue,
            Convert.ToHexString(SHA256.HashData(bytes)),
            gvasOffset >= 0 ? gvasOffset : null,
            build,
            mission?.Code,
            mission?.Title,
            difficulty,
            checkpoint,
            skulls,
            compression?.ChunkCount,
            compression?.UncompressedSize,
            compression?.DataOffset,
            detail);
    }

    private static CompressionLayout? TryReadCompressionLayout(ReadOnlySpan<byte> bytes)
    {
        const int tableOffset = 0x30;
        const int descriptorSize = 16;
        long compressedTotal = 0;
        long uncompressedTotal = 0;

        for (int count = 1; count <= 4096; count++)
        {
            int descriptorOffset = tableOffset + ((count - 1) * descriptorSize);
            int dataOffset = tableOffset + (count * descriptorSize);
            if (descriptorOffset + descriptorSize > bytes.Length) return null;

            ReadOnlySpan<byte> descriptor = bytes.Slice(descriptorOffset, descriptorSize);
            int compressed = ReadUInt24LittleEndian(descriptor[1..4]);
            int uncompressed = ReadUInt24LittleEndian(descriptor[9..12]);
            if (compressed <= 0 || uncompressed <= 0 || uncompressed > 0x20000)
                return null;

            compressedTotal += compressed;
            uncompressedTotal += uncompressed;
            long remaining = bytes.Length - dataOffset;

            // All three observed saves have one leading byte before the concatenated
            // compressed blocks. Accept no-prefix too so the reader is build-tolerant.
            if (compressedTotal == remaining || compressedTotal == remaining - 1)
                return new CompressionLayout(count, dataOffset, compressedTotal, uncompressedTotal);
            if (compressedTotal > remaining)
                return null;
        }

        return null;
    }

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> value)
        => value[0] | (value[1] << 8) | (value[2] << 16);

    private static bool IsInternalCheckpointName(string value)
    {
        if (value.Length is < 5 or > 96) return false;

        return value.Contains("landing_zone", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("landing_z", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("zs_", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("ins_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAscii(ReadOnlySpan<byte> data, string value)
        => IndexOf(data, Encoding.ASCII.GetBytes(value)) >= 0;

    private static int IndexOf(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern)
    {
        if (pattern.Length == 0) return 0;
        if (pattern.Length > data.Length) return -1;

        for (int i = 0; i <= data.Length - pattern.Length; i++)
        {
            if (data.Slice(i, pattern.Length).SequenceEqual(pattern))
                return i;
        }

        return -1;
    }

    private static IReadOnlyList<AsciiRun> ExtractAsciiRuns(ReadOnlySpan<byte> bytes, int minimumLength)
    {
        var result = new List<AsciiRun>();
        int start = -1;

        for (int i = 0; i <= bytes.Length; i++)
        {
            bool printable = i < bytes.Length && bytes[i] is >= 0x20 and <= 0x7E;
            if (printable)
            {
                if (start < 0) start = i;
                continue;
            }

            if (start >= 0 && i - start >= minimumLength)
                result.Add(new AsciiRun(start, Encoding.ASCII.GetString(bytes[start..i])));
            start = -1;
        }

        return result;
    }

    private sealed record AsciiRun(int Offset, string Text);
    private sealed record CompressionLayout(
        int ChunkCount,
        int DataOffset,
        long CompressedSize,
        long UncompressedSize);
}
