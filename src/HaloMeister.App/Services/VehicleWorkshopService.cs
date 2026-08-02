using System.Buffers.Binary;
using HaloMeister.App.Models;
using HaloMeister.App.Localization;

namespace HaloMeister.App.Services;

public sealed record LoadableVehicle(string Name, RuntimeTagEntry Tag)
{
    public string DisplayName => Name;
    public string TagPath => Tag.Name;
    public string Category => Categorize(Tag.Name);
    public string VariantSummary => "Loaded vehicle";
    public string SearchText => $"{DisplayName} {TagPath} {Category}";
    public string Detail => $"[vehi] 0x{RuntimeTagMemoryService.BuildRuntimeDatum(Tag):X8}";

    private static string Categorize(string path)
    {
        string value = path.Replace('\\', '/').ToLowerInvariant();
        if (ContainsAny(value, "banshee", "pelican", "spirit", "phantom",
                "hornet", "seraph"))
            return "Aircraft";
        if (ContainsAny(value, "warthog", "ghost", "scorpion", "wraith",
                "mongoose", "chopper"))
            return "Ground";
        if (ContainsAny(value, "turret", "shade", "gun_tower"))
            return "Turrets";
        return "Other";
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.Ordinal));
}

public sealed record VehiclePlayerControlResult(
    int ChangedSeatCount,
    bool WasAlreadyEnabled,
    bool RemappedSeatLabel)
{
    public string Message
    {
        get
        {
            if (WasAlreadyEnabled)
                return L.Get("vehicle_workshop.pelican_already_enabled");
            return RemappedSeatLabel
                ? L.Format(
                    "vehicle_workshop.pelican_enabled_with_label",
                    ChangedSeatCount)
                : L.Format(
                    "vehicle_workshop.pelican_enabled",
                    ChangedSeatCount);
        }
    }
}

public sealed class VehicleWorkshopService : IDisposable
{
    private const uint InvisibleSeat = 1u << 0;
    private const uint DriverSeat = 1u << 2;
    private const uint InvalidForPlayer = 1u << 13;
    private const uint PlayerBlockingFlags = InvisibleSeat | InvalidForPlayer;
    private const short AiSeatTypeDriver = 5;

    private readonly RuntimeTagMemoryService _memory = RuntimeTagMemoryService.Current;
    private readonly RuntimeTagDefinitionService _definitions = new();
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private IReadOnlyList<RuntimeTagEntry> _tags = [];
    private int _warmedProcessId;

    public int ProcessId => _memory.ProcessId;
    public ScriptingBridgeStatus BridgeStatus => _bridge.GetStatus();

    public IReadOnlyList<LoadableVehicle> Connect()
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException(
                L.Get("vehicle_workshop.error_connect_header_first"));
        return Refresh();
    }

    public IReadOnlyList<LoadableVehicle> Refresh()
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException(
                L.Get("vehicle_workshop.error_connect_game_first"));

        _tags = _memory.ReadTags();
        // Match the weapon loader: list every loaded [vehi] path. Prefer a
        // resolved root when the same path appears more than once.
        LoadableVehicle[] vehicles = _tags
            .Where(tag =>
                string.Equals(tag.Group, "vehi", StringComparison.OrdinalIgnoreCase))
            .GroupBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(tag => tag.DataAddress > 0 ? 1 : 0)
                .ThenByDescending(tag => tag.RootCount > 0 ? 1 : 0)
                .ThenBy(tag => tag.Index)
                .First())
            .Select(tag => new LoadableVehicle(FriendlyName(tag), tag))
            .OrderBy(vehicle => vehicle.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(vehicle => vehicle.TagPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (vehicles.Length == 0)
            throw new InvalidDataException(
                L.Get("vehicle_workshop.error_no_vehicles"));
        return vehicles;
    }

    public async Task<ScriptExecutionResult> SpawnAsync(
        LoadableVehicle selected,
        CancellationToken cancellationToken = default)
    {
        ScriptingBridgeStatus status = _bridge.GetStatus();
        if (!status.IsRuntimeReady)
            throw new InvalidOperationException(
                L.Get("bridge.error_not_responding"));
        if (status.IsStale)
            throw new InvalidOperationException(status.Summary);

        RuntimeTagEntry? live = _tags.FirstOrDefault(tag =>
                tag.Index == selected.Tag.Index &&
                string.Equals(tag.Group, "vehi", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(tag.Name, selected.Tag.Name, StringComparison.OrdinalIgnoreCase));
        if (live is null)
        {
            _tags = _memory.ReadTags();
            live = _tags.FirstOrDefault(tag =>
                tag.Index == selected.Tag.Index &&
                string.Equals(tag.Group, "vehi", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(tag.Name, selected.Tag.Name, StringComparison.OrdinalIgnoreCase));
        }
        if (live is null)
            throw new InvalidOperationException(L.Get("vehicle_workshop.error_tag_unloaded"));
        if (live.DataAddress <= 0 || live.RootCount <= 0)
            throw new InvalidOperationException(
                L.Get("vehicle_workshop.error_tag_not_ready"));

        uint datum = RuntimeTagMemoryService.BuildRuntimeDatum(live);
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.BlamSpawn,
            datum.ToString("X8"),
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);
        return result;
    }

    public void WarmUpDefinitions() => EnsureDefinitions();

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (_warmedProcessId == _memory.ProcessId && _warmedProcessId != 0)
            return;

        ScriptingBridgeStatus status = _bridge.GetStatus();
        if (!status.IsRuntimeReady || status.IsStale)
            return;

        try
        {
            ScriptExecutionResult result = await _bridge.ExecuteAsync(
                ScriptLanguage.PlayerPosition,
                "read",
                TimeSpan.FromSeconds(5),
                cancellationToken);
            if (result.Outcome == ScriptOutcome.Confirmed)
                _warmedProcessId = _memory.ProcessId;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Prewarming is optional; spawning remains available on builds
            // without the player-position capability.
        }
    }

    public VehiclePlayerControlResult EnablePelicanPlayerControl(LoadableVehicle selected)
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException(
                L.Get("vehicle_workshop.error_connect_game_first"));
        if (!IsPelican(selected))
            throw new InvalidOperationException(
                L.Get("vehicle_workshop.error_pelican_only"));

        EnsureDefinitions();
        _tags = _memory.ReadTags();
        RuntimeTagEntry live = FindLive(selected)
            ?? throw new InvalidOperationException(
                L.Get("vehicle_workshop.error_pelican_unloaded"));

        uint warthogDriverLabel;
        try
        {
            warthogDriverLabel = _memory.ResolveStringId("warthog_d");
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                L.Get("vehicle_workshop.error_no_warthog_driver_label"),
                ex);
        }

        uint? pelicanDriverLabel = _memory.TryResolveStringId("pelican_d", out uint pelicanId)
            ? pelicanId
            : null;

        IReadOnlyList<SeatPatchField> seats = ReadSeats(live);
        IReadOnlyList<SeatPatchField> targets = SelectPelicanDriverSeats(
            seats,
            pelicanDriverLabel,
            warthogDriverLabel);
        if (targets.Count == 0)
            throw new InvalidDataException(
                L.Get("vehicle_workshop.error_pelican_no_driver_seat"));

        SeatPatchField[] needingWork = targets
            .Where(seat =>
                seat.Label != warthogDriverLabel ||
                (seat.Flags & PlayerBlockingFlags) != 0)
            .ToArray();
        if (needingWork.Length == 0)
            return new VehiclePlayerControlResult(0, true, false);

        var completed = new List<(long Address, byte[] Original)>();
        bool remappedLabel = false;
        try
        {
            foreach (SeatPatchField seat in needingWork)
            {
                byte[] currentFlags = _memory.ReadBytes(seat.FlagsAddress, sizeof(uint));
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(currentFlags);
                if (flags != seat.Flags)
                    throw new InvalidOperationException(
                        L.Format(
                            "vehicle_workshop.error_pelican_flags_changed",
                            seat.Index));

                byte[] currentLabel = _memory.ReadBytes(seat.LabelAddress, sizeof(uint));
                uint label = BinaryPrimitives.ReadUInt32LittleEndian(currentLabel);
                if (label != seat.Label)
                    throw new InvalidOperationException(
                        L.Format(
                            "vehicle_workshop.error_pelican_label_changed",
                            seat.Index));

                uint nextFlags = flags & ~PlayerBlockingFlags;
                if (nextFlags != flags)
                {
                    byte[] replacement = new byte[sizeof(uint)];
                    BinaryPrimitives.WriteUInt32LittleEndian(replacement, nextFlags);
                    _memory.WriteVerified(seat.FlagsAddress, replacement);
                    completed.Add((seat.FlagsAddress, currentFlags));
                }

                if (label != warthogDriverLabel)
                {
                    byte[] replacement = new byte[sizeof(uint)];
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        replacement, warthogDriverLabel);
                    _memory.WriteVerified(seat.LabelAddress, replacement);
                    completed.Add((seat.LabelAddress, currentLabel));
                    remappedLabel = true;
                }
            }

            IReadOnlyList<SeatPatchField> verified = SelectPelicanDriverSeats(
                ReadSeats(live),
                pelicanDriverLabel,
                warthogDriverLabel);
            if (verified.Count == 0 ||
                verified.Any(seat =>
                    seat.Label != warthogDriverLabel ||
                    (seat.Flags & PlayerBlockingFlags) != 0))
            {
                throw new InvalidDataException(
                    L.Get("vehicle_workshop.error_pelican_verify_failed"));
            }
        }
        catch
        {
            foreach ((long address, byte[] original) in completed.AsEnumerable().Reverse())
            {
                try { _memory.WriteVerified(address, original); }
                catch { }
            }
            throw;
        }

        return new VehiclePlayerControlResult(needingWork.Length, false, remappedLabel);
    }

    public static bool IsPelican(LoadableVehicle? vehicle) =>
        vehicle is not null && IsPelicanPath(vehicle.Tag.Name);

    public static string FriendlyName(RuntimeTagEntry tag)
    {
        string text = tag.LeafName.Replace('_', ' ').Replace('-', ' ').Trim();
        return text.Length == 0
            ? "Unnamed vehicle"
            : string.Join(
                ' ',
                text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(word =>
                        char.ToUpperInvariant(word[0]) + word[1..]));
    }

    public void Dispose() { }

    private RuntimeTagEntry? FindLive(LoadableVehicle selected) =>
        _tags.FirstOrDefault(tag =>
            tag.Index == selected.Tag.Index &&
            string.Equals(tag.Group, "vehi", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(tag.Name, selected.Tag.Name, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<SeatPatchField> SelectPelicanDriverSeats(
        IReadOnlyList<SeatPatchField> seats,
        uint? pelicanDriverLabel,
        uint warthogDriverLabel)
    {
        if (pelicanDriverLabel is uint pelicanLabel)
        {
            SeatPatchField[] byPelicanLabel = seats
                .Where(seat => seat.Label == pelicanLabel)
                .ToArray();
            if (byPelicanLabel.Length > 0) return byPelicanLabel;
        }

        SeatPatchField[] alreadyWarthog = seats
            .Where(seat => seat.Label == warthogDriverLabel)
            .ToArray();
        if (alreadyWarthog.Length > 0) return alreadyWarthog;

        SeatPatchField[] byFlag = seats
            .Where(seat => (seat.Flags & DriverSeat) != 0)
            .ToArray();
        if (byFlag.Length > 0) return byFlag;

        SeatPatchField[] byAi = seats
            .Where(seat => seat.AiSeatType == AiSeatTypeDriver)
            .ToArray();
        if (byAi.Length > 0) return byAi;

        // Campaign Pelican seats often omit the driver flag; seat 0 is the cockpit.
        return seats.Count > 0 ? [seats[0]] : [];
    }

    private IReadOnlyList<SeatPatchField> ReadSeats(RuntimeTagEntry vehicle)
    {
        IReadOnlyList<RuntimeTagFieldValue> root = _definitions.ReadRootFields(
            vehicle.Group, vehicle.DataAddress, _memory.ReadBytes, ResolveOrNull);
        // Vehicle roots nest unit fields as "unit / seats". Prefer the concrete
        // unit_seat_block definition so "powered seats" cannot win.
        RuntimeTagFieldValue seats = root.FirstOrDefault(field =>
                field.Type == "block" &&
                string.Equals(
                    field.ChildBlockDefinition,
                    "unit_seat_block",
                    StringComparison.OrdinalIgnoreCase))
            ?? root.FirstOrDefault(field =>
                field.Type == "block" &&
                string.Equals(
                    LeafFieldName(field.Name),
                    "seats",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                L.Get("vehicle_workshop.error_no_seats_block"));
        if (seats.ChildBlockDefinition is null ||
            seats.ChildCount < 0 ||
            seats.ChildCount > 128 ||
            (seats.ChildCount > 0 && seats.ChildAddress <= 0))
            throw new InvalidDataException(
                L.Get("vehicle_workshop.error_invalid_seats_block"));

        var result = new List<SeatPatchField>();
        for (int index = 0; index < seats.ChildCount; index++)
        {
            IReadOnlyList<RuntimeTagFieldValue> fields = _definitions.ReadBlockFields(
                vehicle.Group,
                seats.ChildBlockDefinition,
                seats.ChildAddress,
                index,
                _memory.ReadBytes,
                ResolveOrNull);
            RuntimeTagFieldValue? flagsField = fields.FirstOrDefault(field =>
                field.Type == "long_flags" &&
                string.Equals(
                    LeafFieldName(field.Name),
                    "flags",
                    StringComparison.OrdinalIgnoreCase));
            RuntimeTagFieldValue? labelField = fields.FirstOrDefault(field =>
                field.Type == "string_id" &&
                string.Equals(
                    LeafFieldName(field.Name),
                    "label",
                    StringComparison.OrdinalIgnoreCase));
            if (flagsField is null || labelField is null) continue;

            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(
                _memory.ReadBytes(flagsField.Address, sizeof(uint)));
            uint label = BinaryPrimitives.ReadUInt32LittleEndian(
                _memory.ReadBytes(labelField.Address, sizeof(uint)));
            short aiSeatType = 0;
            RuntimeTagFieldValue? aiSeatTypeField = fields.FirstOrDefault(field =>
                field.Type == "short_enum" &&
                string.Equals(
                    LeafFieldName(field.Name),
                    "ai seat type",
                    StringComparison.OrdinalIgnoreCase));
            if (aiSeatTypeField is not null)
            {
                aiSeatType = BinaryPrimitives.ReadInt16LittleEndian(
                    _memory.ReadBytes(aiSeatTypeField.Address, sizeof(short)));
            }

            result.Add(new SeatPatchField(
                index,
                flagsField.Address,
                flags,
                labelField.Address,
                label,
                aiSeatType));
        }
        return result;
    }

    private static string LeafFieldName(string name)
    {
        int description = name.IndexOfAny(['#', '{', ':', '^', '*', '!', '~']);
        string value = description >= 0 ? name[..description] : name;
        int path = value.LastIndexOf('/');
        return (path >= 0 ? value[(path + 1)..] : value).Trim();
    }

    private void EnsureDefinitions()
    {
        if (_definitions.SchemaCount == 0)
            _definitions.LoadDirectory(
                RuntimeTagDefinitionLocator.ResolveCampaignEvolved());
        if (!_definitions.HasSchema("vehi"))
            throw new InvalidDataException(
                L.Get("vehicle_workshop.error_no_vehi_schema"));
    }

    private long? ResolveOrNull(uint encoded) =>
        _memory.TryResolveOffset(encoded, out long address) ? address : null;

    private static bool IsPelicanPath(string path) =>
        path.Contains("pelican", StringComparison.OrdinalIgnoreCase);

    private sealed record SeatPatchField(
        int Index,
        long FlagsAddress,
        uint Flags,
        long LabelAddress,
        uint Label,
        short AiSeatType);
}
