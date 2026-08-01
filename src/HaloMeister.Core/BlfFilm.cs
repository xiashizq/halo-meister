using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HaloMeister.Core;

/// <summary>A top-level chunk in a Halo saved-film BLF container.</summary>
public sealed record BlfFilmChunk(
    string Tag,
    long Offset,
    long DeclaredLength,
    long ActualLength,
    ushort MajorVersion,
    ushort MinorVersion,
    bool LittleEndianLength = false);

/// <summary>
/// Reads the Halo: Campaign Evolved saved films written below Meteorite's BlamData
/// directory. Metadata is decoded, while the proprietary deterministic replay stream
/// in the <c>flmd</c> chunk is deliberately preserved as opaque bytes.
/// </summary>
public sealed class BlfFilm
{
    private const int FixedMetadataEnd = 0x1F9A8;
    private const int FilmDataHeaderLength = 12;

    private static readonly Regex DifficultyPattern =
        new(@"\son\s(?<difficulty>[^,]+),", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly byte[] _fileBytes;

    public string? SourcePath { get; }
    public long FileLength => _fileBytes.LongLength;
    public string Sha256 { get; }
    public string ContainerDescription { get; }
    public string Title { get; }
    public string Description { get; }
    public string Difficulty { get; }
    public string Author { get; }
    public string LastModifiedBy { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset ModifiedAtUtc { get; }
    public string BuildSession { get; }
    public string ScenarioPath { get; }
    public string RallyPoint { get; }
    public long FilmDataOffset { get; }
    public long FilmDataLength { get; }
    public int PaddingLength { get; }
    public bool HasNonZeroSignature { get; }
    public IReadOnlyList<BlfFilmChunk> Chunks { get; }

    private BlfFilm(
        byte[] fileBytes,
        string? sourcePath,
        IReadOnlyList<BlfFilmChunk> chunks,
        long filmDataOffset,
        long filmDataLength,
        int paddingLength)
    {
        _fileBytes = fileBytes;
        SourcePath = sourcePath;
        Chunks = chunks;
        FilmDataOffset = filmDataOffset;
        FilmDataLength = filmDataLength;
        PaddingLength = paddingLength;

        Sha256 = Convert.ToHexString(SHA256.HashData(fileBytes));
        ContainerDescription = ReadAsciiZ(fileBytes, 0x0E, 0x22);
        Author = ReadAsciiZ(fileBytes, 0x88, 20);
        LastModifiedBy = ReadAsciiZ(fileBytes, 0xAC, 20);
        Title = ReadUtf16LeZ(fileBytes, 0xC0, 0x100);
        Description = ReadUtf16LeZ(fileBytes, 0x1C0, 0x100);
        BuildSession = ReadAsciiZ(fileBytes, 0x3D4, 0x100);
        ScenarioPath = ReadAsciiZ(fileBytes, 0x92C, 0x100);
        RallyPoint = ReadAsciiZ(fileBytes, 0xA30, 0x100);

        uint created = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(0x78, 4));
        uint modified = BinaryPrimitives.ReadUInt32LittleEndian(fileBytes.AsSpan(0x9C, 4));
        CreatedAtUtc = DateTimeOffset.FromUnixTimeSeconds(created);
        ModifiedAtUtc = DateTimeOffset.FromUnixTimeSeconds(modified);

        Match match = DifficultyPattern.Match(Description);
        Difficulty = match.Success ? match.Groups["difficulty"].Value : string.Empty;

        BlfFilmChunk signature = chunks.Single(c => c.Tag == "ssig");
        int signaturePayloadStart = checked((int)signature.Offset + FilmDataHeaderLength);
        int signaturePayloadLength = checked((int)signature.ActualLength - FilmDataHeaderLength);
        HasNonZeroSignature = fileBytes
            .AsSpan(signaturePayloadStart, signaturePayloadLength)
            .IndexOfAnyExcept((byte)0) >= 0;
    }

    public static BlfFilm Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] bytes = File.ReadAllBytes(path);
        return Parse(bytes, Path.GetFullPath(path));
    }

    public static BlfFilm Parse(byte[] bytes, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < FixedMetadataEnd + FilmDataHeaderLength + 17)
            throw Format("File is too short to contain a Campaign Evolved saved film.", bytes.Length);

        var chunks = new List<BlfFilmChunk>(7);
        long offset = 0;

        foreach (string expectedTag in new[] { "_blf", "chdr", "athr", "flmh", "ssig" })
        {
            BlfFilmChunk chunk = ReadBigEndianChunk(bytes, offset, expectedTag);
            chunks.Add(chunk);
            offset = checked(offset + chunk.ActualLength);
        }

        if (offset != FixedMetadataEnd)
        {
            throw Format(
                $"Metadata chunks end at 0x{offset:X}, expected the flmd chunk at 0x{FixedMetadataEnd:X}.",
                offset);
        }

        Require(bytes, offset, FilmDataHeaderLength);
        string filmDataTag = ReadTag(bytes, offset);
        if (filmDataTag != "flmd")
            throw Format($"Expected chunk 'flmd' at 0x{offset:X}, found '{filmDataTag}'.", offset);

        int flmdOffset = checked((int)offset);
        uint storedFilmDataLength =
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(flmdOffset + 4, 4));
        if (storedFilmDataLength < 8)
            throw Format("The flmd stored length is smaller than its remaining header.", offset + 4);

        long actualFilmChunkLength = checked(4L + storedFilmDataLength);
        Require(bytes, offset, actualFilmChunkLength);

        ushort flmdMajor = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(flmdOffset + 8, 2));
        ushort flmdMinor = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(flmdOffset + 10, 2));
        chunks.Add(new BlfFilmChunk(
            "flmd",
            offset,
            storedFilmDataLength,
            actualFilmChunkLength,
            flmdMajor,
            flmdMinor,
            LittleEndianLength: true));

        long filmDataOffset = checked(offset + FilmDataHeaderLength);
        long filmDataLength = checked(actualFilmChunkLength - FilmDataHeaderLength);
        offset = checked(offset + actualFilmChunkLength);

        BlfFilmChunk footer = ReadBigEndianChunk(bytes, offset, "_eof");
        if (footer.ActualLength != 17)
            throw Format($"The _eof chunk is {footer.ActualLength} bytes; expected 17.", offset + 4);
        chunks.Add(footer);
        offset = checked(offset + footer.ActualLength);

        long padding = bytes.LongLength - offset;
        if (padding is < 0 or > 15)
            throw Format($"Saved-film padding is {padding} bytes; expected 0 through 15.", offset);

        ReadOnlySpan<byte> paddingBytes = bytes.AsSpan(checked((int)offset), checked((int)padding));
        if (paddingBytes.IndexOfAnyExcept((byte)0) >= 0)
            throw Format("Saved-film padding contains non-zero bytes.", offset);

        return new BlfFilm(
            bytes,
            sourcePath,
            chunks,
            filmDataOffset,
            filmDataLength,
            checked((int)padding));
    }

    /// <summary>Returns a lossless copy of the opaque replay payload inside the flmd chunk.</summary>
    public byte[] GetFilmData()
    {
        byte[] result = new byte[checked((int)FilmDataLength)];
        Buffer.BlockCopy(_fileBytes, checked((int)FilmDataOffset), result, 0, result.Length);
        return result;
    }

    public void ExtractFilmData(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        using FileStream output = File.Create(outputPath);
        output.Write(_fileBytes, checked((int)FilmDataOffset), checked((int)FilmDataLength));
    }

    private static BlfFilmChunk ReadBigEndianChunk(byte[] bytes, long offset, string expectedTag)
    {
        Require(bytes, offset, FilmDataHeaderLength);
        int pos = checked((int)offset);
        string tag = ReadTag(bytes, offset);
        if (tag != expectedTag)
            throw Format($"Expected chunk '{expectedTag}' at 0x{offset:X}, found '{tag}'.", offset);

        uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos + 4, 4));
        if (length < FilmDataHeaderLength)
            throw Format($"Chunk '{tag}' has invalid length {length}.", offset + 4);
        Require(bytes, offset, length);

        ushort major = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(pos + 8, 2));
        ushort minor = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(pos + 10, 2));
        return new BlfFilmChunk(tag, offset, length, length, major, minor);
    }

    private static string ReadTag(byte[] bytes, long offset)
    {
        Require(bytes, offset, 4);
        return Encoding.ASCII.GetString(bytes, checked((int)offset), 4);
    }

    private static string ReadAsciiZ(byte[] bytes, int offset, int maxLength)
    {
        Require(bytes, offset, maxLength);
        ReadOnlySpan<byte> span = bytes.AsSpan(offset, maxLength);
        int end = span.IndexOf((byte)0);
        if (end >= 0) span = span[..end];
        return Encoding.ASCII.GetString(span);
    }

    private static string ReadUtf16LeZ(byte[] bytes, int offset, int maxBytes)
    {
        Require(bytes, offset, maxBytes);
        ReadOnlySpan<byte> span = bytes.AsSpan(offset, maxBytes);
        int byteLength = 0;
        while (byteLength + 1 < span.Length &&
               (span[byteLength] != 0 || span[byteLength + 1] != 0))
        {
            byteLength += 2;
        }

        return Encoding.Unicode.GetString(span[..byteLength]);
    }

    private static void Require(byte[] bytes, long offset, long length)
    {
        if (offset < 0 || length < 0 || offset > bytes.LongLength - length)
        {
            throw Format(
                $"Unexpected end of saved film at 0x{offset:X}; needed {length} byte(s), " +
                $"file length is {bytes.LongLength}.",
                offset);
        }
    }

    private static BlamFormatException Format(string message, long offset)
        => new(message, offset <= int.MaxValue ? checked((int)offset) : -1);
}
