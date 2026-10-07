using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HaloMeister.App.Localization;
using HaloMeister.App.Models;
using Microsoft.Win32.SafeHandles;

namespace HaloMeister.App.Services;

public enum GameAttachResult
{
    Connected,
    NotRunning,
    NotReady,
    Failed,
}

public sealed class RuntimeTagMemoryService : IDisposable
{
    private const string ProcessName = "HaloCampaignEvolved";
    private const string SimulationModule = "HaloSimulation_tag_release.dll";
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint PageReadWrite = 0x04;
    private const uint PageGuard = 0x100;
    private const uint PageNoAccess = 0x01;

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
    private RuntimeIdentity? _identity;
    private IReadOnlyList<RuntimeTagEntry>? _tagCache;
    private long _tagCacheTable;
    private long _tagCacheFirst;
    private long _tagCacheLast;
    private long _tagCacheScenarioEntry;
    private long _tagCacheScenarioName;
    private uint _tagCacheScenarioDataOffset;
    private Dictionary<uint, string>? _stringIdNameCache;
    private DateTimeOffset _stringIdNameCacheExpires;
    private readonly object _sessionGate = new();

    public static RuntimeTagMemoryService Current { get; } = new();

    public event EventHandler? ConnectionChanged;

    public bool IsConnected
    {
        get
        {
            lock (_sessionGate)
            {
                try
                {
                    return _handle is { IsInvalid: false, IsClosed: false } &&
                           _process is { HasExited: false };
                }
                catch (Exception)
                {
                    return false;
                }
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
        switch (TryAttach(out string? error))
        {
            case GameAttachResult.Connected:
                return;
            case GameAttachResult.NotRunning:
                throw new InvalidOperationException(L.Get("shell.game_not_running"));
            case GameAttachResult.NotReady:
                throw new InvalidOperationException(
                    error ?? $"{SimulationModule} is not loaded yet. Load into the game and try again.");
            default:
                throw new InvalidOperationException(error ?? "Could not connect to the game.");
        }
    }

    /// <summary>
    /// Attaches when the game process and simulation module are ready.
    /// Returns without throwing when the game is closed or still starting,
    /// so a background watcher can retry without tearing down the UI.
    /// </summary>
    public GameAttachResult TryAttach(out string? error)
    {
        error = null;
        ReleaseIfExited();
        if (IsConnected)
            return GameAttachResult.Connected;

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(ProcessName);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return GameAttachResult.Failed;
        }

        Process? process = null;
        bool keepProcess = false;
        try
        {
            process = processes.FirstOrDefault(IsAlive);
            if (process is null)
                return GameAttachResult.NotRunning;

            ProcessModule? module;
            try
            {
                module = FindSimulationModule(process);
            }
            catch (InvalidOperationException)
            {
                return GameAttachResult.NotRunning;
            }
            catch (Win32Exception ex)
            {
                error = ex.Message;
                return GameAttachResult.NotReady;
            }

            if (module is null)
                return GameAttachResult.NotReady;

            GameBuildProfile buildProfile;
            long moduleBase;
            string modulePath;
            try
            {
                modulePath = module.FileName;
                moduleBase = module.BaseAddress.ToInt64();
                buildProfile = GameBuildProfileCatalog.Resolve(modulePath);
            }
            catch (InvalidOperationException)
            {
                return GameAttachResult.NotRunning;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return GameAttachResult.Failed;
            }

            SafeProcessHandle handle = OpenProcess(
                ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation,
                false,
                process.Id);
            if (handle.IsInvalid)
            {
                int code = Marshal.GetLastWin32Error();
                handle.Dispose();
                error = $"OpenProcess failed ({code})";
                return GameAttachResult.NotReady;
            }

            try
            {
                if (!CommitSession(
                        process,
                        handle,
                        moduleBase,
                        modulePath,
                        buildProfile,
                        out bool tookOwnership,
                        out error))
                    return error is null ? GameAttachResult.NotRunning : GameAttachResult.NotReady;
                keepProcess = tookOwnership;
            }
            catch (InvalidDataException ex)
            {
                error = ex.Message;
                return GameAttachResult.NotReady;
            }
            catch (Win32Exception ex)
            {
                error = ex.Message;
                return GameAttachResult.NotReady;
            }
            catch (InvalidOperationException)
            {
                return GameAttachResult.NotRunning;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return GameAttachResult.Failed;
            }

            return GameAttachResult.Connected;
        }
        finally
        {
            foreach (Process candidate in processes)
            {
                if (keepProcess && ReferenceEquals(candidate, process))
                    continue;
                try
                {
                    candidate.Dispose();
                }
                catch (Exception)
                {
                    // Already released.
                }
            }
        }
    }

    /// <summary>
    /// Drops a session whose process has already exited. This is a single
    /// wait on the process we already hold, not a system-wide process scan.
    /// </summary>
    public bool ReleaseIfExited()
    {
        bool exited;
        lock (_sessionGate)
        {
            if (_process is null && _handle is null)
                return false;
            try
            {
                exited = _process is null || _process.HasExited;
            }
            catch (Exception)
            {
                exited = true;
            }
        }

        if (!exited)
            return false;
        Disconnect();
        return true;
    }

    private bool CommitSession(
        Process process,
        SafeProcessHandle handle,
        long moduleBase,
        string modulePath,
        GameBuildProfile buildProfile,
        out bool tookOwnership,
        out string? error)
    {
        error = null;
        tookOwnership = false;
        bool notify = false;
        lock (_sessionGate)
        {
            if (IsSessionAlive())
            {
                handle.Dispose();
                return true;
            }

            _process = process;
            _handle = handle;
            _moduleBase = moduleBase;
            _modulePath = modulePath;
            _buildProfile = buildProfile;
            try
            {
                long table = checked((long)ReadUInt64(
                    _moduleBase + buildProfile.TagTablePointerOffset));
                ValidateTagTable(table);
                if (process.HasExited)
                    throw new InvalidOperationException("The game process exited.");
                process.EnableRaisingEvents = true;
                process.Exited += OnProcessExited;
                if (!ReferenceEquals(_process, process) || process.HasExited)
                    throw new InvalidOperationException("The game process exited.");
                _identity = new RuntimeIdentity(
                    process.Id,
                    process.StartTime.ToUniversalTime().Ticks,
                    moduleBase,
                    table);
                tookOwnership = true;
                notify = true;
            }
            catch
            {
                if (_process is not null)
                    _process.Exited -= OnProcessExited;
                _handle = null;
                _process = null;
                _moduleBase = 0;
                _modulePath = null;
                _buildProfile = null;
                _identity = null;
                handle.Dispose();
                throw;
            }
        }

        if (notify)
            ConnectionChanged?.Invoke(this, EventArgs.Empty);
        return tookOwnership || IsConnected;
    }

    private static ProcessModule? FindSimulationModule(Process process)
    {
        foreach (ProcessModule candidate in process.Modules)
        {
            if (candidate.ModuleName.Equals(SimulationModule, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return null;
    }

    private static bool IsAlive(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool IsSessionAlive()
    {
        try
        {
            return _handle is { IsInvalid: false, IsClosed: false } &&
                   _process is { HasExited: false };
        }
        catch (Exception)
        {
            return false;
        }
    }

    public IReadOnlyList<RuntimeTagEntry> ReadTags()
    {
        EnsureConnected();
        EnsureRuntimeIdentity();
        if (TagCacheStillValid())
            return _tagCache!;

        long table = checked((long)ReadUInt64(
            _moduleBase + BuildProfile.TagTablePointerOffset));
        (int elementSize, long first, int capacity) = ValidateTagTable(table);
        long last = first + (long)capacity * elementSize;

        var pending = new List<PendingTag>();
        var namePointers = new List<long>();
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
                string group = Encoding.ASCII.GetString(chunk, offset + 4, 4);
                group = new string(group.Reverse().ToArray());
                pending.Add(new PendingTag(
                    chunkStart + relative,
                    namePointer,
                    group,
                    BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(offset, 4)),
                    BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(offset + 0x18, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(offset + 0x1C, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(offset + 0x20, 4))));
                namePointers.Add(namePointer);
            }
        }

        IReadOnlyDictionary<long, string> names = ReadCStringBatch(namePointers, 1024);
        var result = new List<RuntimeTagEntry>(pending.Count);
        foreach (PendingTag tag in pending)
        {
            if (!names.TryGetValue(tag.NamePointer, out string? name) ||
                string.IsNullOrWhiteSpace(name))
                continue;

            long dataAddress = TryResolveOffset(tag.DataOffset, out long data) ? data : 0;
            long definitionAddress =
                TryResolveOffset(tag.DefinitionOffset, out long definition) ? definition : 0;
            result.Add(new RuntimeTagEntry(
                tag.Index, tag.Datum, tag.Group, name,
                tag.NamePointer, tag.RootCount,
                tag.DataOffset, tag.DefinitionOffset, dataAddress, definitionAddress));
        }

        RuntimeTagEntry? scenario = result.FirstOrDefault(tag =>
            string.Equals(tag.Group, "scnr", StringComparison.OrdinalIgnoreCase) &&
            tag.DataAddress > 0);
        _tagCache = result;
        _tagCacheTable = table;
        _tagCacheFirst = first;
        _tagCacheLast = last;
        _tagCacheScenarioEntry = scenario is null
            ? 0
            : first + (long)scenario.Index * elementSize;
        _tagCacheScenarioName = scenario?.NameAddress ?? 0;
        _tagCacheScenarioDataOffset = scenario?.DataOffset ?? 0;
        return _tagCache;
    }

    private bool TagCacheStillValid()
    {
        if (_tagCache is null || _tagCacheTable == 0)
            return false;
        try
        {
            long first = checked((long)ReadUInt64(_tagCacheTable + 0x50));
            long last = checked((long)ReadUInt64(_tagCacheTable + 0x58));
            if (first != _tagCacheFirst || last != _tagCacheLast)
                return false;
            if (_tagCacheScenarioEntry == 0)
                return false;
            byte[] header = ReadBytes(_tagCacheScenarioEntry, 0x24);
            long namePointer = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0x10, 8));
            uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x1C, 4));
            return namePointer == _tagCacheScenarioName &&
                   dataOffset == _tagCacheScenarioDataOffset;
        }
        catch
        {
            return false;
        }
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

    /// <summary>
    /// Returns whether <paramref name="address"/>..<paramref name="address"/>+count
    /// lies in a single committed writable region.
    /// </summary>
    public bool IsWritable(long address, int count)
    {
        EnsureConnected();
        if (address <= 0 || count <= 0) return false;
        try
        {
            EnsureWritable(address, count);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns how many writable bytes remain in the committed region that
    /// contains <paramref name="address"/>.
    /// </summary>
    public bool TryGetWritableExtent(long address, out int writableBytes)
    {
        EnsureConnected();
        writableBytes = 0;
        if (address <= 0) return false;
        if (VirtualQueryEx(
                _handle!,
                new IntPtr(address),
                out MemoryBasicInformation memory,
                (nuint)Marshal.SizeOf<MemoryBasicInformation>()) == 0 ||
            memory.State != MemCommit ||
            (memory.Protect & (PageGuard | PageNoAccess)) != 0 ||
            !IsWritableProtection(memory.Protect))
        {
            return false;
        }

        long regionEnd = checked(memory.BaseAddress.ToInt64() + (long)memory.RegionSize);
        long remaining = regionEnd - address;
        if (remaining <= 0 || remaining > int.MaxValue) return false;
        writableBytes = (int)remaining;
        return true;
    }

    /// <summary>
    /// Allocates a committed, page-aligned writable buffer in the game process.
    /// Used only for session-scoped tag-block relocation that still needs a
    /// segmented arena encoding.
    /// </summary>
    public long AllocateRemote(int size)
    {
        EnsureConnected();
        EnsureRuntimeIdentity();
        if (size is <= 0 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(size));

        IntPtr allocated = VirtualAllocEx(
            _handle!,
            IntPtr.Zero,
            (nuint)size,
            MemCommit | MemReserve,
            PageReadWrite);
        if (allocated == IntPtr.Zero)
            throw Win32("VirtualAllocEx");
        return allocated.ToInt64();
    }

    /// <summary>
    /// Claims an unused tag-arena slot and points it at <paramref name="baseAddress"/>
    /// so newly allocated buffers can be addressed with segmented offsets.
    /// Arena 0 is skipped because a zero encoded offset is treated as null.
    /// </summary>
    public int ClaimUnusedArena(long baseAddress)
    {
        EnsureConnected();
        EnsureRuntimeIdentity();
        if (baseAddress <= 0 || (baseAddress & 3) != 0)
            throw new ArgumentOutOfRangeException(nameof(baseAddress));

        // Slot 0 is reserved: encoded offset 0 is null in the runtime resolver.
        for (int arena = 1; arena < 16; arena++)
        {
            long slot = _moduleBase + BuildProfile.ArenaTableOffset + arena * 8L;
            ulong current = ReadUInt64(slot);
            if (current != 0) continue;

            byte[] expected = new byte[8];
            byte[] replacement = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(replacement, (ulong)baseAddress);
            WriteVerified(slot, expected, replacement);
            return arena;
        }

        throw new InvalidOperationException(
            "No unused tag-arena slot is available for a temporary palette buffer.");
    }

    /// <summary>
    /// Allocates <paramref name="data"/> in the game process, claims an unused
    /// arena for it, and returns the segmented offset of the buffer start.
    /// </summary>
    public uint PublishArenaBuffer(ReadOnlySpan<byte> data)
        => PublishArenaBuffer(data, out _);

    /// <summary>
    /// Allocates <paramref name="data"/> in the game process, claims an unused
    /// arena for it, and returns both the segmented base offset and arena index.
    /// </summary>
    public uint PublishArenaBuffer(ReadOnlySpan<byte> data, out int arena)
    {
        if (data.Length == 0)
            throw new ArgumentException("Buffer data is empty.", nameof(data));

        long address = AllocateRemote(data.Length);
        byte[] expected = new byte[data.Length];
        WriteVerified(address, expected, data);
        arena = ClaimUnusedArena(address);
        uint encoded = (uint)(arena << 28);
        if (encoded == 0 || !TryResolveOffset(encoded, out long resolved) ||
            resolved != address)
        {
            throw new InvalidDataException(
                "The temporary palette buffer could not be published into a tag arena.");
        }
        return encoded;
    }

    /// <summary>
    /// Encodes a 4-byte-aligned byte offset inside a claimed tag arena.
    /// </summary>
    public static uint EncodeArenaByteOffset(int arena, int byteOffset)
    {
        if (arena is < 1 or > 15)
            throw new ArgumentOutOfRangeException(nameof(arena));
        if (byteOffset < 0 || (byteOffset & 3) != 0)
            throw new ArgumentOutOfRangeException(nameof(byteOffset));
        return (uint)(arena << 28) | (uint)(byteOffset >> 2);
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

        return GetStringIdNameMap().TryGetValue(stringId, out name);
    }

    /// <summary>
    /// Builds (and briefly caches) the reverse string-id map. Variant pickers
    /// resolve many ids in a row; reading the full registry per lookup is too
    /// expensive because name storage alone is tens of megabytes.
    /// </summary>
    private Dictionary<uint, string> GetStringIdNameMap()
    {
        if (_stringIdNameCache is not null &&
            DateTimeOffset.UtcNow < _stringIdNameCacheExpires)
            return _stringIdNameCache;

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
            return [];
        if (storageUsed == 0 || storageUsed > StringIdStorageCapacity)
            return [];
        if (count < StringIdBuiltinCount || count > StringIdMaxEntries)
            return [];

        byte[] storage = ReadBytes(storageAddress, (int)storageUsed);
        byte[] strings = ReadBytes(stringsAddress, checked((int)count * 8));
        byte[] builtins = ReadBytes(
            _moduleBase + profile.StringIdBuiltinTableRva,
            StringIdBuiltinCount * 16);

        var map = new Dictionary<uint, string>((int)count);
        for (int index = 0; index < (int)count; index++)
        {
            uint id = index < StringIdBuiltinCount
                ? BinaryPrimitives.ReadUInt32LittleEndian(
                    builtins.AsSpan(index * 16, 4))
                : checked(
                    StringIdSetZeroBuiltinCount +
                    (uint)(index - StringIdBuiltinCount));

            ulong namePointer = BinaryPrimitives.ReadUInt64LittleEndian(
                strings.AsSpan(index * 8, 8));
            if (namePointer < (ulong)storageAddress)
                continue;
            ulong relative = namePointer - (ulong)storageAddress;
            if (relative > uint.MaxValue || relative >= storageUsed)
                continue;
            if (!TryReadStorageName(storage, (uint)relative, out ReadOnlySpan<byte> bytes))
                continue;
            map[id] = Encoding.UTF8.GetString(bytes);
        }

        _stringIdNameCache = map;
        _stringIdNameCacheExpires = DateTimeOffset.UtcNow.AddSeconds(30);
        return map;
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
        EnsureRuntimeIdentity();
        byte[] expected = ReadBytes(address, bytes.Length);
        WriteVerified(address, expected, bytes);
    }

    /// <summary>
    /// Applies a single compare-and-swap style memory patch. The target must
    /// still contain <paramref name="expected"/> immediately before the write.
    /// </summary>
    public void WriteVerified(
        long address,
        ReadOnlySpan<byte> expected,
        ReadOnlySpan<byte> bytes)
    {
        ApplyTransaction([new RuntimeMemoryWrite(address, expected.ToArray(), bytes.ToArray())]);
    }

    /// <summary>
    /// Applies non-overlapping patches with preflight checks and conservative
    /// rollback. A rollback never overwrites bytes another game system changed
    /// after this transaction's write.
    /// </summary>
    public void ApplyTransaction(IEnumerable<RuntimeMemoryWrite> requestedWrites)
    {
        EnsureConnected();
        EnsureRuntimeIdentity();
        RuntimeMemoryWrite[] writes = requestedWrites
            .OrderBy(write => write.Address)
            .ToArray();
        if (writes.Length == 0)
            throw new ArgumentException("No memory writes supplied.", nameof(requestedWrites));

        long previousEnd = 0;
        foreach (RuntimeMemoryWrite write in writes)
        {
            if (write.Address <= 0)
                throw new ArgumentOutOfRangeException(nameof(requestedWrites));
            if (write.Expected.Length == 0 || write.Expected.Length != write.Value.Length)
                throw new ArgumentException(
                    "Each memory write needs equally sized expected and replacement bytes.",
                    nameof(requestedWrites));
            long end = checked(write.Address + write.Value.Length);
            if (previousEnd > write.Address)
                throw new InvalidOperationException(
                    $"Memory patches overlap at 0x{write.Address:X}.");
            previousEnd = end;
            EnsureWritable(write.Address, write.Value.Length);
            byte[] live = ReadBytes(write.Address, write.Expected.Length);
            if (!live.AsSpan().SequenceEqual(write.Expected))
            {
                throw new IOException(
                    $"The game changed memory at 0x{write.Address:X}; refusing a stale write.");
            }
        }

        int completed = 0;
        try
        {
            foreach (RuntimeMemoryWrite write in writes)
            {
                WriteUnchecked(write.Address, write.Value);
                byte[] verification = ReadBytes(write.Address, write.Value.Length);
                if (!verification.AsSpan().SequenceEqual(write.Value))
                    throw new IOException(
                        $"The game did not retain the write at 0x{write.Address:X}.");
                completed++;
            }
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<string>();
            for (int index = completed - 1; index >= 0; index--)
            {
                RuntimeMemoryWrite write = writes[index];
                try
                {
                    if (ReadBytes(write.Address, write.Value.Length).AsSpan()
                        .SequenceEqual(write.Value))
                    {
                        WriteUnchecked(write.Address, write.Expected);
                    }
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add($"0x{write.Address:X}: {rollbackError.Message}");
                }
            }
            string rollback = rollbackErrors.Count == 0
                ? "Earlier writes were rolled back where still owned by this transaction."
                : $"Rollback errors: {string.Join("; ", rollbackErrors)}";
            throw new IOException($"{error.Message} {rollback}", error);
        }

        ClearTagCache();
    }

    public void Disconnect()
    {
        SafeProcessHandle? handle;
        Process? process;
        bool notify;
        lock (_sessionGate)
        {
            notify = _handle is not null || _process is not null;
            if (_process is not null)
                _process.Exited -= OnProcessExited;
            handle = _handle;
            process = _process;
            _handle = null;
            _process = null;
            _moduleBase = 0;
            _modulePath = null;
            _buildProfile = null;
            _identity = null;
            ClearTagCache();
            _stringIdNameCache = null;
            _stringIdNameCacheExpires = default;
        }

        try
        {
            handle?.Dispose();
        }
        catch (Exception)
        {
            // The handle may already be closed.
        }

        try
        {
            process?.Dispose();
        }
        catch (Exception)
        {
            // The process object may already be released.
        }

        if (notify)
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

    private readonly record struct PendingTag(
        int Index,
        long NamePointer,
        string Group,
        uint Datum,
        int RootCount,
        uint DataOffset,
        uint DefinitionOffset);

    private void ClearTagCache()
    {
        _tagCache = null;
        _tagCacheTable = 0;
        _tagCacheFirst = 0;
        _tagCacheLast = 0;
        _tagCacheScenarioEntry = 0;
        _tagCacheScenarioName = 0;
        _tagCacheScenarioDataOffset = 0;
    }

    /// <summary>
    /// Reads many C strings with one <c>ReadProcessMemory</c> per cluster of
    /// nearby pointers instead of one call per name.
    /// </summary>
    private IReadOnlyDictionary<long, string> ReadCStringBatch(
        IReadOnlyList<long> addresses,
        int maxBytes)
    {
        var map = new Dictionary<long, string>();
        if (addresses.Count == 0 || maxBytes <= 0)
            return map;

        long[] sorted = addresses.Where(address => address > 0).Distinct().ToArray();
        Array.Sort(sorted);
        const int clusterLimit = 64 * 1024;
        int index = 0;
        while (index < sorted.Length)
        {
            long start = sorted[index];
            long end = start + maxBytes;
            int next = index + 1;
            while (next < sorted.Length &&
                   sorted[next] >= start &&
                   sorted[next] + maxBytes - start <= clusterLimit &&
                   sorted[next] <= end)
            {
                end = sorted[next] + maxBytes;
                next++;
            }

            int length = (int)Math.Min(end - start, clusterLimit);
            byte[]? blob = null;
            try
            {
                blob = ReadBytes(start, length);
            }
            catch
            {
                blob = null;
            }

            for (int cursor = index; cursor < next; cursor++)
            {
                long address = sorted[cursor];
                if (blob is not null)
                {
                    int offset = (int)(address - start);
                    if (offset >= 0 && offset < blob.Length)
                    {
                        int zero = blob.AsSpan(offset).IndexOf((byte)0);
                        if (zero >= 0)
                        {
                            map[address] = Encoding.UTF8.GetString(blob, offset, zero);
                            continue;
                        }
                    }
                }

                try
                {
                    map[address] = ReadCString(address, maxBytes);
                }
                catch
                {
                    // Unreadable names are skipped by the caller.
                }
            }

            index = next;
        }

        return map;
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

    private void EnsureRuntimeIdentity()
    {
        RuntimeIdentity expected = _identity
            ?? throw new InvalidOperationException("No runtime identity is active.");
        if (_process is null || _process.Id != expected.ProcessId ||
            _process.StartTime.ToUniversalTime().Ticks != expected.ProcessStartTimeTicks ||
            _moduleBase != expected.ModuleBase)
        {
            throw new InvalidOperationException(
                "The game process identity changed; reconnect before writing live tags.");
        }

        long currentTable = checked((long)ReadUInt64(
            _moduleBase + BuildProfile.TagTablePointerOffset));
        if (currentTable != expected.TagTable)
        {
            ClearTagCache();
            throw new InvalidOperationException(
                "The runtime tag table changed; reconnect before using cached tag addresses.");
        }
    }

    private void EnsureWritable(long address, int count)
    {
        if (VirtualQueryEx(
                _handle!,
                new IntPtr(address),
                out MemoryBasicInformation memory,
                (nuint)Marshal.SizeOf<MemoryBasicInformation>()) == 0 ||
            memory.State != MemCommit ||
            (memory.Protect & (PageGuard | PageNoAccess)) != 0 ||
            !IsWritableProtection(memory.Protect))
        {
            throw new UnauthorizedAccessException(
                $"The target memory page at 0x{address:X} is not writable.");
        }

        long pageEnd = checked(memory.BaseAddress.ToInt64() + (long)memory.RegionSize);
        if (checked(address + count) > pageEnd)
        {
            throw new UnauthorizedAccessException(
                $"The write at 0x{address:X} crosses a memory page boundary.");
        }
    }

    private void WriteUnchecked(long address, byte[] bytes)
    {
        if (!WriteProcessMemory(_handle!, new IntPtr(address), bytes, bytes.Length, out nuint written) ||
            written != (nuint)bytes.Length)
        {
            throw Win32($"WriteProcessMemory at 0x{address:X}");
        }
    }

    private static bool IsWritableProtection(uint protection)
        => protection is 0x04 or 0x08 or 0x40 or 0x80;

    private GameBuildProfile BuildProfile => _buildProfile
        ?? throw new InvalidOperationException(
            "No supported game build profile is active.");

    private static Win32Exception Win32(string operation)
        => new(Marshal.GetLastWin32Error(), $"{operation} failed");

    private sealed record RuntimeIdentity(
        int ProcessId,
        long ProcessStartTimeTicks,
        long ModuleBase,
        long TagTable);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public ushort _padding;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint _padding2;
    }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(
        SafeProcessHandle process,
        IntPtr address,
        out MemoryBasicInformation buffer,
        nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(
        SafeProcessHandle process,
        IntPtr address,
        nuint size,
        uint allocationType,
        uint protect);
}

public sealed record RuntimeMemoryWrite(long Address, byte[] Expected, byte[] Value);
