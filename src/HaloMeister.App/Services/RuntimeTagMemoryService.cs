using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HaloMeister.App.Models;
using Microsoft.Win32.SafeHandles;

namespace HaloMeister.App.Services;

public sealed class RuntimeTagMemoryService : IDisposable
{
    private const string ProcessName = "HaloCampaignEvolved";
    private const string SimulationModule = "HaloSimulation_tag_release.dll";
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryInformation = 0x0400;

    // Layout constants from Baboon's Campaign Evolved runtime poker.
    private const int StringIdMaxEntries = 523_264;
    private const int StringIdStorageCapacity = 26_163_200;
    private const int StringIdMaxNameBytes = 127;
    private const int StringIdBuiltinCount = 2_678;
    private const uint StringIdSetZeroBuiltinCount = 1_068;

    private SafeProcessHandle? _handle;
    private Process? _process;
    private long _moduleBase;
    private string? _modulePath;
    private GameBuildProfile? _buildProfile;

    public static RuntimeTagMemoryService Current { get; } = new();

    public event EventHandler? ConnectionChanged;

    public bool IsConnected
    {
        get
        {
            try
            {
                return _handle is { IsInvalid: false, IsClosed: false } &&
                       _process is { HasExited: false };
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }
    public int ProcessId => _process?.Id ?? 0;
    public long ModuleBase => _moduleBase;
    public string? ModulePath => _modulePath;
    public string? BuildProfileId => _buildProfile?.Id;

    public void Connect()
    {
        Disconnect();
        Process process = Process.GetProcessesByName(ProcessName).SingleOrDefault()
            ?? throw new InvalidOperationException("Halo: Campaign Evolved is not running.");
        ProcessModule module = process.Modules.Cast<ProcessModule>()
            .SingleOrDefault(candidate =>
                candidate.ModuleName.Equals(SimulationModule, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"{SimulationModule} is not loaded yet. Load into the game and try again.");
        GameBuildProfile buildProfile = GameBuildProfileCatalog.Resolve(module.FileName);

        SafeProcessHandle handle = OpenProcess(
            ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation,
            false,
            process.Id);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw Win32("OpenProcess");
        }

        _process = process;
        process.EnableRaisingEvents = true;
        process.Exited += OnProcessExited;
        _handle = handle;
        _moduleBase = module.BaseAddress.ToInt64();
        _modulePath = module.FileName;
        _buildProfile = buildProfile;

        long table = checked((long)ReadUInt64(
            _moduleBase + _buildProfile.TagTablePointerOffset));
        ValidateTagTable(table);
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<RuntimeTagEntry> ReadTags()
    {
        EnsureConnected();
        long table = checked((long)ReadUInt64(
            _moduleBase + BuildProfile.TagTablePointerOffset));
        (int elementSize, long first, int capacity) = ValidateTagTable(table);

        var result = new List<RuntimeTagEntry>();
        const int chunkEntries = 4096;
        for (int chunkStart = 0; chunkStart < capacity; chunkStart += chunkEntries)
        {
            int count = Math.Min(chunkEntries, capacity - chunkStart);
            byte[] chunk = ReadBytes(first + (long)chunkStart * elementSize, count * elementSize);
            for (int relative = 0; relative < count; relative++)
            {
                int offset = relative * elementSize;
                long namePointer = BinaryPrimitives.ReadInt64LittleEndian(chunk.AsSpan(offset + 0x10, 8));
                if (namePointer == 0) continue;

                string name;
                try { name = ReadCString(namePointer, 1024); }
                catch { continue; }
                if (string.IsNullOrWhiteSpace(name)) continue;

                string group = Encoding.ASCII.GetString(chunk, offset + 4, 4);
                group = new string(group.Reverse().ToArray());
                uint datum = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(offset, 4));
                int rootCount = BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(offset + 0x18, 4));
                uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(offset + 0x1C, 4));
                uint definitionOffset =
                    BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(offset + 0x20, 4));

                long dataAddress = TryResolveOffset(dataOffset, out long data) ? data : 0;
                long definitionAddress =
                    TryResolveOffset(definitionOffset, out long definition) ? definition : 0;
                result.Add(new RuntimeTagEntry(
                    chunkStart + relative, datum, group, name,
                    namePointer, rootCount,
                    dataOffset, definitionOffset, dataAddress, definitionAddress));
            }
        }
        return result;
    }

    public long ResolveOffset(uint encodedOffset)
    {
        if (!TryResolveOffset(encodedOffset, out long address))
            throw new InvalidDataException(
                $"Segmented tag offset 0x{encodedOffset:X8} could not be resolved.");
        return address;
    }

    public bool TryResolveOffset(uint encodedOffset, out long address)
    {
        EnsureConnected();
        address = 0;
        if (encodedOffset == 0 || encodedOffset == uint.MaxValue) return false;
        int arena = (int)(encodedOffset >> 28);
        uint wordOffset = encodedOffset & 0x0FFF_FFFF;
        ulong arenaBase;
        try { arenaBase = ReadUInt64(
            _moduleBase + BuildProfile.ArenaTableOffset + arena * 8L); }
        catch { return false; }
        if (arenaBase == 0 || arenaBase > long.MaxValue) return false;
        try { address = checked((long)arenaBase + wordOffset * 4L); }
        catch (OverflowException) { return false; }
        return address > 0;
    }

    public bool TryEncodeOffset(long address, out uint encodedOffset)
    {
        EnsureConnected();
        encodedOffset = 0;
        for (int arena = 0; arena < 16; arena++)
        {
            ulong rawBase;
            try { rawBase = ReadUInt64(
                _moduleBase + BuildProfile.ArenaTableOffset + arena * 8L); }
            catch { continue; }
            if (rawBase == 0 || rawBase > long.MaxValue) continue;
            long arenaBase = (long)rawBase;
            long delta = address - arenaBase;
            if (delta < 0 || (delta & 3) != 0) continue;
            long wordOffset = delta / 4;
            if (wordOffset > 0x0FFF_FFFF) continue;
            encodedOffset = (uint)(arena << 28) | (uint)wordOffset;
            return true;
        }
        return false;
    }

    public byte[] BuildTagReference(RuntimeTagEntry target)
    {
        if (!TryEncodeOffset(target.NameAddress, out uint nameOffset))
            throw new InvalidDataException(
                $"The name for {target.Name} is not inside a known tag arena.");

        byte[] reference = new byte[16];
        byte[] group = Encoding.ASCII.GetBytes(target.Group.PadRight(4)[..4]);
        Array.Reverse(group);
        group.CopyTo(reference, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(reference.AsSpan(4), nameOffset);
        BinaryPrimitives.WriteInt32LittleEndian(
            reference.AsSpan(8), Encoding.UTF8.GetByteCount(target.Name));
        BinaryPrimitives.WriteUInt32LittleEndian(
            reference.AsSpan(12), BuildRuntimeDatum(target));
        return reference;
    }

    public static uint BuildRuntimeDatum(RuntimeTagEntry target)
    {
        if ((uint)target.Index > ushort.MaxValue)
            throw new InvalidDataException(
                "The target tag index does not fit the runtime datum format.");

        return ((target.Datum & 0xFFFF) << 16) | (uint)target.Index;
    }

    /// <summary>
    /// Resolves a named string-id from the running game's string registry.
    /// Names such as <c>warthog_d</c> are fixed; the numeric value comes from
    /// the engine table (built-ins are stable, dynamics depend on load order).
    /// </summary>
    public uint ResolveStringId(string name)
    {
        EnsureConnected();
        GameBuildProfile profile = BuildProfile;
        byte[]? target = NormalizeStringIdName(name);
        if (target is null)
            return uint.MaxValue;

        long storageAddress = checked((long)ReadUInt64(
            _moduleBase + profile.StringIdStorageRva));
        uint storageUsed = ReadUInt32(
            _moduleBase + profile.StringIdStorageUsedRva);
        long stringsAddress = checked((long)ReadUInt64(
            _moduleBase + profile.StringIdStringsRva));
        uint count = ReadUInt32(_moduleBase + profile.StringIdCountRva);
        if (storageAddress <= 0 || stringsAddress <= 0 || count == 0)
            throw new InvalidDataException(
                "The runtime string-id registry is not initialized.");
        if (storageUsed == 0 || storageUsed > StringIdStorageCapacity)
            throw new InvalidDataException(
                "The runtime string-id name storage has an invalid size.");
        if (count < StringIdBuiltinCount || count > StringIdMaxEntries)
            throw new InvalidDataException(
                "The runtime string-id registry count is outside the supported range.");

        byte[] storage = ReadBytes(storageAddress, (int)storageUsed);
        byte[] strings = ReadBytes(stringsAddress, checked((int)count * 8));
        byte[] builtins = ReadBytes(
            _moduleBase + profile.StringIdBuiltinTableRva,
            StringIdBuiltinCount * 16);

        for (int index = 0; index < (int)count; index++)
        {
            ulong namePointer = BinaryPrimitives.ReadUInt64LittleEndian(
                strings.AsSpan(index * 8, 8));
            if (namePointer < (ulong)storageAddress)
                continue;
            ulong relative = namePointer - (ulong)storageAddress;
            if (relative > uint.MaxValue || relative >= storageUsed)
                continue;
            if (!TryReadStorageName(storage, (uint)relative, out ReadOnlySpan<byte> candidate))
                continue;
            if (!candidate.SequenceEqual(target))
                continue;

            if (index < StringIdBuiltinCount)
            {
                return BinaryPrimitives.ReadUInt32LittleEndian(
                    builtins.AsSpan(index * 16, 4));
            }

            return checked(
                StringIdSetZeroBuiltinCount + (uint)(index - StringIdBuiltinCount));
        }

        throw new InvalidDataException(
            $"'{name}' is not registered in the running game's string-id table.");
    }

    public bool TryResolveStringId(string name, out uint stringId)
    {
        stringId = 0;
        try
        {
            stringId = ResolveStringId(name);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Looks up the display name for a runtime string-id value.
    /// </summary>
    public bool TryGetStringIdName(uint stringId, out string? name)
    {
        name = null;
        if (stringId == 0 || stringId == uint.MaxValue)
            return false;

        EnsureConnected();
        GameBuildProfile profile = BuildProfile;
        long storageAddress = checked((long)ReadUInt64(
            _moduleBase + profile.StringIdStorageRva));
        uint storageUsed = ReadUInt32(
            _moduleBase + profile.StringIdStorageUsedRva);
        long stringsAddress = checked((long)ReadUInt64(
            _moduleBase + profile.StringIdStringsRva));
        uint count = ReadUInt32(_moduleBase + profile.StringIdCountRva);
        if (storageAddress <= 0 || stringsAddress <= 0 || count == 0)
            return false;
        if (storageUsed == 0 || storageUsed > StringIdStorageCapacity)
            return false;
        if (count < StringIdBuiltinCount || count > StringIdMaxEntries)
            return false;

        byte[] storage = ReadBytes(storageAddress, (int)storageUsed);
        byte[] strings = ReadBytes(stringsAddress, checked((int)count * 8));
        byte[] builtins = ReadBytes(
            _moduleBase + profile.StringIdBuiltinTableRva,
            StringIdBuiltinCount * 16);

        for (int index = 0; index < (int)count; index++)
        {
            uint id = index < StringIdBuiltinCount
                ? BinaryPrimitives.ReadUInt32LittleEndian(
                    builtins.AsSpan(index * 16, 4))
                : checked(
                    StringIdSetZeroBuiltinCount +
                    (uint)(index - StringIdBuiltinCount));
            if (id != stringId)
                continue;

            ulong namePointer = BinaryPrimitives.ReadUInt64LittleEndian(
                strings.AsSpan(index * 8, 8));
            if (namePointer < (ulong)storageAddress)
                return false;
            ulong relative = namePointer - (ulong)storageAddress;
            if (relative > uint.MaxValue || relative >= storageUsed)
                return false;
            if (!TryReadStorageName(storage, (uint)relative, out ReadOnlySpan<byte> bytes))
                return false;
            name = Encoding.UTF8.GetString(bytes);
            return true;
        }

        return false;
    }

    public byte[] ReadBytes(long address, int count)
    {
        EnsureConnected();
        if (address <= 0) throw new ArgumentOutOfRangeException(nameof(address));
        if (count is < 0 or > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(count));
        byte[] buffer = new byte[count];
        if (!ReadProcessMemory(_handle!, new IntPtr(address), buffer, count, out nuint read) ||
            read != (nuint)count)
            throw Win32($"ReadProcessMemory at 0x{address:X}");
        return buffer;
    }

    public void WriteVerified(long address, ReadOnlySpan<byte> bytes)
    {
        EnsureConnected();
        if (address <= 0) throw new ArgumentOutOfRangeException(nameof(address));
        if (bytes.Length == 0) throw new ArgumentException("No bytes supplied.", nameof(bytes));
        byte[] buffer = bytes.ToArray();
        if (!WriteProcessMemory(_handle!, new IntPtr(address), buffer, buffer.Length, out nuint written) ||
            written != (nuint)buffer.Length)
            throw Win32($"WriteProcessMemory at 0x{address:X}");
        byte[] verification = ReadBytes(address, buffer.Length);
        if (!verification.AsSpan().SequenceEqual(buffer))
            throw new IOException($"The game did not retain the write at 0x{address:X}.");
    }

    public void Disconnect()
    {
        bool wasConnected = _handle is not null || _process is not null;
        if (_process is not null)
            _process.Exited -= OnProcessExited;
        _handle?.Dispose();
        _handle = null;
        _process?.Dispose();
        _process = null;
        _moduleBase = 0;
        _modulePath = null;
        _buildProfile = null;
        if (wasConnected)
            ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Disconnect();

    private void OnProcessExited(object? sender, EventArgs e) => Disconnect();

    private (int ElementSize, long First, int Capacity) ValidateTagTable(long table)
    {
        if (table <= 0) throw new InvalidDataException("The runtime tag table pointer is null.");
        int elementSize = checked((int)ReadUInt64(table + 0x20));
        long first = checked((long)ReadUInt64(table + 0x50));
        long last = checked((long)ReadUInt64(table + 0x58));
        if (elementSize is < 0x24 or > 0x1000)
            throw new InvalidDataException(
                $"Unexpected tag entry size 0x{elementSize:X}; the game layout may have changed.");
        if (first <= 0 || last < first || (last - first) % elementSize != 0)
            throw new InvalidDataException(
                "The runtime tag table range is invalid; the game layout may have changed.");
        long capacity = (last - first) / elementSize;
        if (capacity is <= 0 or > 1_000_000)
            throw new InvalidDataException($"Implausible runtime tag capacity {capacity:N0}.");
        return (elementSize, first, (int)capacity);
    }

    private ulong ReadUInt64(long address)
        => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(address, 8));

    private uint ReadUInt32(long address)
        => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(address, 4));

    private static byte[]? NormalizeStringIdName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (name.Length > StringIdMaxNameBytes)
            throw new ArgumentException(
                $"String-id name is longer than {StringIdMaxNameBytes} bytes.",
                nameof(name));

        byte[] bytes = Encoding.UTF8.GetBytes(name);
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = bytes[i] switch
            {
                >= (byte)'A' and <= (byte)'Z' => (byte)(bytes[i] + ('a' - 'A')),
                (byte)' ' or (byte)'-' => (byte)'_',
                _ => bytes[i],
            };
        }
        return bytes;
    }

    private static bool TryReadStorageName(
        byte[] storage,
        uint offset,
        out ReadOnlySpan<byte> name)
    {
        name = default;
        if (offset >= storage.Length) return false;
        int max = Math.Min(StringIdMaxNameBytes + 1, storage.Length - (int)offset);
        int zero = storage.AsSpan((int)offset, max).IndexOf((byte)0);
        if (zero < 0) return false;
        name = storage.AsSpan((int)offset, zero);
        return true;
    }

    private string ReadCString(long address, int maxBytes)
    {
        var bytes = new List<byte>(Math.Min(maxBytes, 128));
        const int page = 128;
        for (int offset = 0; offset < maxBytes; offset += page)
        {
            byte[] part = ReadBytes(address + offset, Math.Min(page, maxBytes - offset));
            int zero = Array.IndexOf(part, (byte)0);
            if (zero >= 0)
            {
                bytes.AddRange(part.AsSpan(0, zero).ToArray());
                return Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(bytes));
            }
            bytes.AddRange(part);
        }
        return Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(bytes));
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
            throw new InvalidOperationException("Not connected to Halo: Campaign Evolved.");
    }

    private GameBuildProfile BuildProfile => _buildProfile
        ?? throw new InvalidOperationException(
            "No supported game build profile is active.");

    private static Win32Exception Win32(string operation)
        => new(Marshal.GetLastWin32Error(), $"{operation} failed");

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        SafeProcessHandle process,
        IntPtr baseAddress,
        [Out] byte[] buffer,
        int size,
        out nuint numberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(
        SafeProcessHandle process,
        IntPtr baseAddress,
        byte[] buffer,
        int size,
        out nuint numberOfBytesWritten);
}
