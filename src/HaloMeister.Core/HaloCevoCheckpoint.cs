using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace HaloMeister.Core;

/// <summary>
/// Reads and rebuilds the HALOCEVO checkpoint wrapper. Campaign Evolved stores
/// the GVAS/Blam payload as independent 128 KiB Oodle Kraken streams.
/// </summary>
public sealed class HaloCevoCheckpoint
{
    private const int DescriptorTableOffset = 0x30;
    private const int DescriptorSize = 16;

    // The 16 bytes at 0x20 describe the stream as a whole using the same
    // descriptor shape as the per-chunk entries: uint24 total compressed at
    // +1 and uint24 total uncompressed at +9. Offset 0x0C repeats the total
    // uncompressed size as a uint32. All three must be rewritten whenever the
    // payload changes, or the game sees a wrapper that contradicts itself.
    /// <summary>
    /// Distance from a magazine record back to its object's game-state
    /// identifier. Verified on nine records spanning three weapon types.
    /// </summary>
    private const int GameStateIdBackOffset = 240;
    private const int WeaponTagDatumBackOffset = 698;
    private const int NativeRecordSizeBackOffset = 714;
    private const uint SpartanBipedTagDatum = 0xFBB2195C;
    private const int BipedHeaderBackOffset = 16;
    private const int BipedBodyVitalityOffset = 0x80;
    private const int BipedShieldVitalityOffset = 0x84;

    private const int StreamDescriptorOffset = 0x20;
    private const int TotalUncompressedOffset = 0x0C;
    private const int DefaultChunkSize = 0x20000;

    private readonly byte[] _wrapperPrefix;
    private int[] _uncompressedChunkSizes;
    private byte[] _payload;

    private HaloCevoCheckpoint(
        byte[] wrapperPrefix,
        byte[] payload,
        int[] uncompressedChunkSizes,
        int leadingDataBytes)
    {
        _wrapperPrefix = wrapperPrefix;
        _payload = payload;
        _uncompressedChunkSizes = uncompressedChunkSizes;
        LeadingDataBytes = leadingDataBytes;
    }

    public byte[] Payload => _payload;
    public int ChunkCount => _uncompressedChunkSizes.Length;
    public int LeadingDataBytes { get; }

    /// <summary>
    /// Swaps in a payload whose length may differ from the original. The
    /// descriptor table is a fixed-size part of the wrapper prefix, so the
    /// chunk count must not change or the compressed data would no longer
    /// start where the table says it does. Every leading chunk therefore keeps
    /// its original size and only the final chunk absorbs the difference.
    /// </summary>
    public void ReplacePayload(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        long head = 0;
        for (int index = 0; index < ChunkCount - 1; index++) head += _uncompressedChunkSizes[index];

        long last = payload.Length - head;
        if (last < 1 || last > DefaultChunkSize)
            throw new InvalidDataException(
                $"The edited payload is {payload.Length:N0} bytes, which does not fit the checkpoint's " +
                $"{ChunkCount} chunk layout. It must stay between {head + 1:N0} and " +
                $"{head + DefaultChunkSize:N0} bytes.");

        var sizes = (int[])_uncompressedChunkSizes.Clone();
        sizes[^1] = (int)last;
        _payload = payload;
        _uncompressedChunkSizes = sizes;
    }

    public static HaloCevoCheckpoint Decode(ReadOnlySpan<byte> wrapper, OodleRuntime oodle)
    {
        ArgumentNullException.ThrowIfNull(oodle);
        if (!wrapper.StartsWith("HALOCEVO"u8))
            throw new InvalidDataException("The selected data is not a HALOCEVO checkpoint.");

        CompressionLayout layout = ReadLayout(wrapper);
        byte[] prefix = wrapper[..(layout.DataOffset + layout.LeadingDataBytes)].ToArray();
        var sizes = new int[layout.ChunkCount];
        using var payload = new MemoryStream(checked((int)layout.UncompressedSize));

        int compressedOffset = layout.DataOffset + layout.LeadingDataBytes;
        for (int index = 0; index < layout.ChunkCount; index++)
        {
            int descriptorOffset = DescriptorTableOffset + (index * DescriptorSize);
            int compressedSize = ReadUInt24(wrapper.Slice(descriptorOffset + 1, 3));
            int uncompressedSize = ReadUInt24(wrapper.Slice(descriptorOffset + 9, 3));
            sizes[index] = uncompressedSize;

            byte[] chunk = oodle.Decompress(
                wrapper.Slice(compressedOffset, compressedSize),
                uncompressedSize);
            payload.Write(chunk);
            compressedOffset += compressedSize;
        }

        if (compressedOffset != wrapper.Length)
            throw new InvalidDataException("HALOCEVO compressed data did not end at the wrapper boundary.");

        byte[] decoded = payload.ToArray();
        if (!decoded.AsSpan().StartsWith("GVAS"u8))
            throw new InvalidDataException("The decompressed checkpoint payload does not begin with GVAS.");

        return new HaloCevoCheckpoint(prefix, decoded, sizes, layout.LeadingDataBytes);
    }

    public IReadOnlyList<HaloCevoAmmoState> FindAmmoStates(int reserveAmmo, int loadedAmmo)
    {
        if (reserveAmmo < 0 || loadedAmmo < 0) return [];
        return EnumerateAmmoRecords()
            .Where(record => record.ReserveAmmo == reserveAmmo && record.LoadedAmmo == loadedAmmo)
            .ToArray();
    }

    /// <summary>
    /// Resolves the player biped's native vitality fields. The framing was
    /// correlated with the live runtime tag table and checked across every
    /// saved Marine, Grunt, Elite, and Spartan biped in controlled A30 saves.
    /// </summary>
    public HaloCevoVitalityState? FindPlayerVitality()
    {
        ReadOnlySpan<byte> payload = Payload;
        var matches = new List<HaloCevoVitalityState>();
        for (int tagOffset = BipedHeaderBackOffset;
             tagOffset <= payload.Length - BipedShieldVitalityOffset - sizeof(float);
             tagOffset++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(payload[tagOffset..]) != SpartanBipedTagDatum)
                continue;

            int headerOffset = tagOffset - BipedHeaderBackOffset;
            int recordSize = BinaryPrimitives.ReadInt32LittleEndian(payload[headerOffset..]);
            ushort gameStateId = BinaryPrimitives.ReadUInt16LittleEndian(payload[(headerOffset + 4)..]);
            if (recordSize is < 4096 or > 64 * 1024 || gameStateId != 1)
                continue;

            float body = BinaryPrimitives.ReadSingleLittleEndian(
                payload[(tagOffset + BipedBodyVitalityOffset)..]);
            float shield = BinaryPrimitives.ReadSingleLittleEndian(
                payload[(tagOffset + BipedShieldVitalityOffset)..]);
            if (!float.IsFinite(body) || !float.IsFinite(shield) ||
                body is < 0 or > 4 || shield is < 0 or > 4)
                continue;

            matches.Add(new HaloCevoVitalityState(
                tagOffset,
                gameStateId,
                SpartanBipedTagDatum,
                recordSize,
                body,
                shield));
        }

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidDataException(
                $"Found {matches.Count} guarded player-biped vitality records; expected one."),
        };
    }

    public void SetPlayerVitality(
        HaloCevoVitalityState state,
        float bodyVitality,
        float shieldVitality)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!float.IsFinite(bodyVitality) || bodyVitality is < 0 or > 1)
            throw new ArgumentOutOfRangeException(
                nameof(bodyVitality), "Body vitality must be between 0 and 1.");
        if (!float.IsFinite(shieldVitality) || shieldVitality is < 0 or > 1)
            throw new ArgumentOutOfRangeException(
                nameof(shieldVitality), "Shield vitality must be between 0 and 1.");

        int bodyOffset = state.TagDatumOffset + BipedBodyVitalityOffset;
        int shieldOffset = state.TagDatumOffset + BipedShieldVitalityOffset;
        if (state.TagDatumOffset < BipedHeaderBackOffset ||
            shieldOffset > Payload.Length - sizeof(float))
            throw new ArgumentOutOfRangeException(nameof(state));

        ReadOnlySpan<byte> current = Payload;
        uint datum = BinaryPrimitives.ReadUInt32LittleEndian(current[state.TagDatumOffset..]);
        ushort gameStateId = BinaryPrimitives.ReadUInt16LittleEndian(
            current[(state.TagDatumOffset - 12)..]);
        float currentBody = BinaryPrimitives.ReadSingleLittleEndian(current[bodyOffset..]);
        float currentShield = BinaryPrimitives.ReadSingleLittleEndian(current[shieldOffset..]);
        if (datum != state.BipedTagDatum || gameStateId != state.GameStateId ||
            BitConverter.SingleToInt32Bits(currentBody) != BitConverter.SingleToInt32Bits(state.BodyVitality) ||
            BitConverter.SingleToInt32Bits(currentShield) != BitConverter.SingleToInt32Bits(state.ShieldVitality))
            throw new InvalidOperationException(
                "The player vitality record changed after it was resolved; nothing was written.");

        BinaryPrimitives.WriteSingleLittleEndian(Payload.AsSpan(bodyOffset, sizeof(float)), bodyVitality);
        BinaryPrimitives.WriteSingleLittleEndian(Payload.AsSpan(shieldOffset, sizeof(float)), shieldVitality);
    }

    /// <summary>
    /// Every guarded native weapon datum in the checkpoint, whatever its
    /// values. This is what makes editing possible without the game running:
    /// records no longer have to be matched against a live capture first.
    /// </summary>
    public IReadOnlyList<HaloCevoAmmoState> EnumerateAmmoRecords()
    {
        // Both controlled player-weapon records share this native weapon datum
        // framing. Requiring it avoids treating unrelated integer pairs
        // elsewhere in the multi-megabyte simulation as ammunition.
        ReadOnlySpan<byte> prefix = [0xFF, 0xFF, 0x00, 0x00];
        ReadOnlySpan<byte> suffix = [0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

        var matches = new List<HaloCevoAmmoState>();
        ReadOnlySpan<byte> payload = Payload;
        for (int offset = 4; offset <= payload.Length - 16; offset++)
        {
            if (payload[offset + 10] != 0xFF || payload[offset + 8] != 0x00) continue;
            if (!payload.Slice(offset - 4, 4).SequenceEqual(prefix)) continue;
            if (!payload.Slice(offset + 8, 8).SequenceEqual(suffix)) continue;

            int reserve = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
            int loaded = BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 4)..]);
            if (reserve is < 0 or > 100_000 || loaded is < 0 or > 10_000) continue;

            // The owning object's game-state identifier sits a fixed distance
            // ahead of the magazine. It is the same value the saved actor table
            // stores as BlamObjectGameStateIdentifier and the live
            // BlamObjectSynchronizationComponent holds at +0xF8, so it is what
            // lets an offline record be named without the game running.
            int? gameStateId = null;
            if (offset >= GameStateIdBackOffset)
            {
                int candidate = BinaryPrimitives.ReadInt32LittleEndian(
                    payload[(offset - GameStateIdBackOffset)..]);
                if (candidate is >= 0 and <= short.MaxValue) gameStateId = candidate;
            }

            uint? weaponTagDatum = null;
            if (offset >= WeaponTagDatumBackOffset)
            {
                uint candidate = BinaryPrimitives.ReadUInt32LittleEndian(
                    payload[(offset - WeaponTagDatumBackOffset)..]);
                if (candidate != 0 && candidate != uint.MaxValue)
                    weaponTagDatum = candidate;
            }

            int? nativeRecordSize = null;
            if (offset >= NativeRecordSizeBackOffset)
            {
                int candidate = BinaryPrimitives.ReadInt32LittleEndian(
                    payload[(offset - NativeRecordSizeBackOffset)..]);
                if (candidate is >= 256 and <= 64 * 1024)
                    nativeRecordSize = candidate;
            }

            matches.Add(new HaloCevoAmmoState(
                offset,
                reserve,
                loaded,
                gameStateId,
                weaponTagDatum,
                nativeRecordSize));
        }
        return matches;
    }

    public void SetAmmo(HaloCevoAmmoState state, int reserveAmmo, int loadedAmmo)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (reserveAmmo < 0 || loadedAmmo < 0)
            throw new ArgumentOutOfRangeException(nameof(reserveAmmo), "Ammunition cannot be negative.");
        if (state.PayloadOffset < 4 || state.PayloadOffset > Payload.Length - 16)
            throw new ArgumentOutOfRangeException(nameof(state), "The ammunition record is outside the payload.");

        ReadOnlySpan<byte> current = Payload;
        if (BinaryPrimitives.ReadInt32LittleEndian(current[state.PayloadOffset..]) != state.ReserveAmmo ||
            BinaryPrimitives.ReadInt32LittleEndian(current[(state.PayloadOffset + 4)..]) != state.LoadedAmmo)
            throw new InvalidOperationException(
                "The checkpoint changed after ammunition records were resolved. Capture the live loadout again.");

        BinaryPrimitives.WriteInt32LittleEndian(Payload.AsSpan(state.PayloadOffset, 4), reserveAmmo);
        BinaryPrimitives.WriteInt32LittleEndian(Payload.AsSpan(state.PayloadOffset + 4, 4), loadedAmmo);
    }

    /// <summary>
    /// Replaces the weapon-definition datum embedded in one native weapon
    /// record. This does not resize or reinterpret the rest of the
    /// type-specific record and is therefore a guarded research primitive,
    /// not sufficient by itself for a cross-type weapon replacement.
    /// </summary>
    public void SetWeaponTagDatum(HaloCevoAmmoState state, uint weaponTagDatum)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.WeaponTagDatum is not { } original)
            throw new InvalidOperationException(
                "The selected weapon record has no mapped definition datum.");
        int offset = state.PayloadOffset - WeaponTagDatumBackOffset;
        if (offset < 0 || offset > Payload.Length - sizeof(uint))
            throw new ArgumentOutOfRangeException(nameof(state));

        uint current = BinaryPrimitives.ReadUInt32LittleEndian(Payload.AsSpan(offset, sizeof(uint)));
        if (current != original)
            throw new InvalidOperationException(
                $"The weapon datum at 0x{offset:X} changed from 0x{original:X8} " +
                $"to 0x{current:X8}; nothing was written.");
        BinaryPrimitives.WriteUInt32LittleEndian(
            Payload.AsSpan(offset, sizeof(uint)),
            weaponTagDatum);
    }

    public byte[] Encode(OodleRuntime oodle)
    {
        ArgumentNullException.ThrowIfNull(oodle);
        byte[] prefix = (byte[])_wrapperPrefix.Clone();
        var compressedChunks = new byte[ChunkCount][];

        int payloadOffset = 0;
        long compressedTotal = 0;
        for (int index = 0; index < ChunkCount; index++)
        {
            int size = _uncompressedChunkSizes[index];
            ReadOnlySpan<byte> raw = Payload.AsSpan(payloadOffset, size);
            byte[] compressed = oodle.CompressKraken(raw);
            byte[] verified = oodle.Decompress(compressed, size);
            if (!raw.SequenceEqual(verified))
                throw new InvalidDataException($"Oodle verification failed for checkpoint chunk {index}.");

            compressedChunks[index] = compressed;
            compressedTotal += compressed.Length;
            int descriptorOffset = DescriptorTableOffset + (index * DescriptorSize);
            WriteUInt24(prefix.AsSpan(descriptorOffset + 1, 3), compressed.Length);
            WriteUInt24(prefix.AsSpan(descriptorOffset + 9, 3), size);
            payloadOffset += size;
        }

        if (payloadOffset != Payload.Length)
            throw new InvalidDataException("HALOCEVO chunk sizes do not cover the decompressed payload.");

        // Keep the whole-stream descriptor consistent with what was actually
        // written. Leaving these stale produces a wrapper that decodes but
        // contradicts its own header.
        WriteUInt24(prefix.AsSpan(StreamDescriptorOffset + 1, 3), checked((int)compressedTotal));
        WriteUInt24(prefix.AsSpan(StreamDescriptorOffset + 9, 3), Payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(
            prefix.AsSpan(TotalUncompressedOffset, 4),
            checked((uint)Payload.Length));

        using var result = new MemoryStream(checked((int)(prefix.Length + compressedTotal)));
        result.Write(prefix);
        foreach (byte[] chunk in compressedChunks) result.Write(chunk);
        return result.ToArray();
    }

    private static CompressionLayout ReadLayout(ReadOnlySpan<byte> bytes)
    {
        long compressedTotal = 0;
        long uncompressedTotal = 0;

        for (int count = 1; count <= 4096; count++)
        {
            int descriptorOffset = DescriptorTableOffset + ((count - 1) * DescriptorSize);
            int dataOffset = DescriptorTableOffset + (count * DescriptorSize);
            if (descriptorOffset + DescriptorSize > bytes.Length)
                break;

            ReadOnlySpan<byte> descriptor = bytes.Slice(descriptorOffset, DescriptorSize);
            int compressed = ReadUInt24(descriptor[1..4]);
            int uncompressed = ReadUInt24(descriptor[9..12]);
            if (compressed <= 0 || uncompressed <= 0 || uncompressed > 0x20000)
                break;

            compressedTotal += compressed;
            uncompressedTotal += uncompressed;
            long remaining = bytes.Length - dataOffset;
            if (compressedTotal == remaining)
                return new CompressionLayout(count, dataOffset, 0, uncompressedTotal);
            if (compressedTotal == remaining - 1)
                return new CompressionLayout(count, dataOffset, 1, uncompressedTotal);
            if (compressedTotal > remaining)
                break;
        }

        throw new InvalidDataException("The HALOCEVO compression table is invalid or unsupported.");
    }

    private static int ReadUInt24(ReadOnlySpan<byte> value)
        => value[0] | (value[1] << 8) | (value[2] << 16);

    private static void WriteUInt24(Span<byte> value, int number)
    {
        if (number is < 0 or > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(number));
        value[0] = (byte)number;
        value[1] = (byte)(number >> 8);
        value[2] = (byte)(number >> 16);
    }

    private sealed record CompressionLayout(
        int ChunkCount,
        int DataOffset,
        int LeadingDataBytes,
        long UncompressedSize);
}

public sealed record HaloCevoAmmoState(
    int PayloadOffset,
    int ReserveAmmo,
    int LoadedAmmo,
    int? GameStateId = null,
    uint? WeaponTagDatum = null,
    int? NativeRecordSize = null)
{
    /// <summary>
    /// The guard framing also matches unrelated float pairs in the simulation:
    /// 16,256 is the high half of 1.0f and shows up with a zero partner. A
    /// magazine that is actually carrying rounds has a small non-zero loaded
    /// count, which separates real weapons from that noise.
    /// </summary>
    public bool LooksLikeMagazine
        => LoadedAmmo is > 0 and <= 200 && ReserveAmmo <= 5000;
}

public sealed record HaloCevoVitalityState(
    int TagDatumOffset,
    ushort GameStateId,
    uint BipedTagDatum,
    int NativeRecordSize,
    float BodyVitality,
    float ShieldVitality);

/// <summary>
/// Uses a user-provided licensed Oodle 2.8 runtime. Halo Meister does not
/// redistribute Epic's proprietary codec.
/// </summary>
public sealed class OodleRuntime : IDisposable
{
    private const int OodleLzCompressorKraken = 8;
    private const int OodleLzCompressionLevelFast = 3;
    private readonly nint _library;
    private readonly OodleLzDecompress _decompress;
    private readonly OodleLzCompress _compress;
    private bool _disposed;

    public OodleRuntime(string libraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        LibraryPath = Path.GetFullPath(libraryPath);
        if (!File.Exists(LibraryPath))
            throw new FileNotFoundException("The selected Oodle runtime does not exist.", LibraryPath);
        if (!Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("HALOCEVO editing requires the 64-bit Halo Meister build.");

        _library = NativeLibrary.Load(LibraryPath);
        try
        {
            _decompress = Marshal.GetDelegateForFunctionPointer<OodleLzDecompress>(
                NativeLibrary.GetExport(_library, "OodleLZ_Decompress"));
            _compress = Marshal.GetDelegateForFunctionPointer<OodleLzCompress>(
                NativeLibrary.GetExport(_library, "OodleLZ_Compress"));
        }
        catch
        {
            NativeLibrary.Free(_library);
            throw;
        }
    }

    public string LibraryPath { get; }

    public byte[] Decompress(ReadOnlySpan<byte> compressed, int expectedSize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (compressed.IsEmpty || expectedSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedSize));

        nint source = Marshal.AllocHGlobal(compressed.Length);
        nint destination = Marshal.AllocHGlobal(expectedSize);
        try
        {
            Marshal.Copy(compressed.ToArray(), 0, source, compressed.Length);
            long actual = _decompress(
                source, compressed.Length, destination, expectedSize,
                fuzzSafe: 1, checkCrc: 0, verbosity: 0,
                destinationBase: 0, destinationSizeForBounds: 0,
                callback: 0, callbackUserData: 0,
                decoderMemory: 0, decoderMemorySize: 0,
                threadPhase: 3);
            if (actual != expectedSize)
                throw new InvalidDataException(
                    $"Oodle decompressed {actual:N0} bytes; {expectedSize:N0} were expected.");

            var result = new byte[expectedSize];
            Marshal.Copy(destination, result, 0, result.Length);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(destination);
            Marshal.FreeHGlobal(source);
        }
    }

    public byte[] CompressKraken(ReadOnlySpan<byte> raw)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (raw.IsEmpty) throw new ArgumentException("Cannot compress an empty chunk.", nameof(raw));

        int capacity = checked(raw.Length + 64 * 1024);
        nint source = Marshal.AllocHGlobal(raw.Length);
        nint destination = Marshal.AllocHGlobal(capacity);
        try
        {
            Marshal.Copy(raw.ToArray(), 0, source, raw.Length);
            long actual = _compress(
                OodleLzCompressorKraken,
                source,
                raw.Length,
                destination,
                OodleLzCompressionLevelFast,
                options: 0,
                dictionaryBase: 0,
                longRangeMatcher: 0,
                scratchMemory: 0,
                scratchMemorySize: 0);
            if (actual <= 0 || actual > capacity)
                throw new InvalidDataException($"Oodle compression returned invalid size {actual}.");

            var result = new byte[checked((int)actual)];
            Marshal.Copy(destination, result, 0, result.Length);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(destination);
            Marshal.FreeHGlobal(source);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        NativeLibrary.Free(_library);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long OodleLzDecompress(
        nint source,
        long sourceSize,
        nint destination,
        long destinationSize,
        int fuzzSafe,
        int checkCrc,
        int verbosity,
        nint destinationBase,
        long destinationSizeForBounds,
        nint callback,
        nint callbackUserData,
        nint decoderMemory,
        long decoderMemorySize,
        int threadPhase);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long OodleLzCompress(
        int compressor,
        nint source,
        long sourceSize,
        nint destination,
        int level,
        nint options,
        nint dictionaryBase,
        nint longRangeMatcher,
        nint scratchMemory,
        long scratchMemorySize);
}
