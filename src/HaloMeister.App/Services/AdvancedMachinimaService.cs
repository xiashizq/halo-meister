using System.Globalization;
using System.Text;
using System.Text.Json;
using HaloMeister.App.Localization;

namespace HaloMeister.App.Services;

public sealed record MachinimaTransform(
    float X,
    float Y,
    float Z,
    float Pitch,
    float Yaw,
    float Roll)
{
    public PlayerCoordinates Position => new(X, Y, Z);
}

public sealed record MachinimaState(
    bool IsEnabled,
    string WorldName,
    MachinimaTransform Transform);

public sealed record MachinimaNode(
    string Id,
    string Name,
    string Detail,
    string WorldName,
    MachinimaTransform Transform);

public sealed record SavedMachinimaLocation(
    Guid Id,
    string Name,
    string WorldName,
    MachinimaTransform Transform,
    DateTimeOffset SavedAt)
{
    public string Detail =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Transform.X:F2}, {Transform.Y:F2}, {Transform.Z:F2} · {ShortWorldName(WorldName)}");

    private static string ShortWorldName(string value)
    {
        int slash = value.LastIndexOf('/');
        string leaf = slash >= 0 ? value[(slash + 1)..] : value;
        int dot = leaf.IndexOf('.');
        return dot > 0 ? leaf[..dot] : leaf;
    }
}

public sealed class AdvancedMachinimaService
{
    private const float UnrealCentimetersPerBlamUnit = 304.8f;

    private sealed record RawMachinimaState(
        bool IsEnabled,
        string WorldName,
        float CameraUnrealX,
        float CameraUnrealY,
        float CameraUnrealZ,
        float PlayerUnrealX,
        float PlayerUnrealY,
        float PlayerUnrealZ,
        float Pitch,
        float Yaw,
        float Roll);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private readonly RuntimeBoundaryService _boundaries = new();
    private readonly SoftCeilingService _softCeilings = new();
    private readonly PlayerToolsService _player = new();
    private readonly string _locationsPath;
    private bool _restoreBoundariesOnExit;
    private bool _restorePhysicalWallsOnExit;

    private AdvancedMachinimaService()
    {
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        _locationsPath = Path.Combine(
            localAppData,
            "HaloMeister",
            "AdvancedMachinima",
            "locations.json");
    }

    public static AdvancedMachinimaService Current { get; } = new();

    public ScriptingBridgeStatus BridgeStatus => _bridge.GetStatus();

    public string InstallOrRepairBridge() => _bridge.InstallOrUpdateBridge();

    public IReadOnlyList<SavedMachinimaLocation> LoadSavedLocations()
    {
        if (!File.Exists(_locationsPath))
            return [];

        try
        {
            SavedMachinimaLocation[] locations =
                JsonSerializer.Deserialize<SavedMachinimaLocation[]>(
                    File.ReadAllText(_locationsPath, Encoding.UTF8),
                    JsonOptions) ?? [];
            if (locations.Any(location =>
                    location.Id == Guid.Empty ||
                    string.IsNullOrWhiteSpace(location.Name) ||
                    string.IsNullOrWhiteSpace(location.WorldName) ||
                    !IsFinite(location.Transform)))
            {
                throw new InvalidDataException(
                    "Saved machinima locations contain invalid data.");
            }
            return locations
                .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "Saved machinima locations could not be read.",
                ex);
        }
    }

    public SavedMachinimaLocation SaveLocation(
        string name,
        MachinimaState state)
    {
        name = name.Trim();
        if (!state.IsEnabled)
            throw new InvalidOperationException(
                "Enter Advanced Machinima before saving the camera location.");
        if (name.Length is < 1 or > 80 ||
            name.IndexOfAny(['\r', '\n', '\t']) >= 0)
        {
            throw new ArgumentException(
                "Location names must contain 1–80 printable characters.",
                nameof(name));
        }

        List<SavedMachinimaLocation> locations =
            [.. LoadSavedLocations()];
        SavedMachinimaLocation? existing = locations.FirstOrDefault(location =>
            string.Equals(location.Name, name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                location.WorldName,
                state.WorldName,
                StringComparison.Ordinal));
        var saved = new SavedMachinimaLocation(
            existing?.Id ?? Guid.NewGuid(),
            name,
            state.WorldName,
            state.Transform,
            DateTimeOffset.UtcNow);
        if (existing is not null)
            locations.Remove(existing);
        locations.Add(saved);
        WriteLocations(locations);
        return saved;
    }

    public void DeleteLocation(Guid id)
    {
        List<SavedMachinimaLocation> locations =
            [.. LoadSavedLocations()];
        int removed = locations.RemoveAll(location => location.Id == id);
        if (removed == 0)
            throw new InvalidOperationException(
                "That saved machinima location no longer exists.");
        WriteLocations(locations);
    }

    public async Task<MachinimaState> ReadStateAsync(
        CancellationToken cancellationToken = default)
    {
        RawMachinimaState raw =
            await ReadRawStateAsync(cancellationToken);
        PlayerCoordinates player =
            await _player.ReadPositionAsync(cancellationToken);
        return ToMachinimaState(raw, player);
    }

    public async Task<IReadOnlyList<MachinimaNode>> ReadLiveNodesAsync(
        CancellationToken cancellationToken = default)
    {
        RawMachinimaState raw =
            await ReadRawStateAsync(cancellationToken);
        PlayerCoordinates player =
            await _player.ReadPositionAsync(cancellationToken);
        EnsureBridgeReady();
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.MachinimaNodes,
            "read",
            cancellationToken: cancellationToken);
        EnsureConfirmed(result);

        var nodes = new List<MachinimaNode>();
        foreach (string line in result.Message.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split('\t', 7);
            if (fields.Length != 7 ||
                !TryParseFinite(fields[0], out float x) ||
                !TryParseFinite(fields[1], out float y) ||
                !TryParseFinite(fields[2], out float z) ||
                !TryParseFinite(fields[3], out float pitch) ||
                !TryParseFinite(fields[4], out float yaw) ||
                !TryParseFinite(fields[5], out float roll) ||
                string.IsNullOrWhiteSpace(fields[6]))
            {
                throw new InvalidDataException(
                    "The game returned an invalid camera-location node.");
            }

            string fullName = fields[6];
            PlayerCoordinates position = ToBlamPosition(
                x,
                y,
                z,
                raw,
                player);
            nodes.Add(new MachinimaNode(
                fullName,
                FriendlyActorName(fullName),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{position.X:F2}, {position.Y:F2}, {position.Z:F2} · authored camera actor"),
                raw.WorldName,
                new MachinimaTransform(
                    position.X,
                    position.Y,
                    position.Z,
                    pitch,
                    yaw,
                    roll)));
        }
        return nodes;
    }

    public async Task<MachinimaState> EnterAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureBridgeReady();
        bool changedBoundaries = false;
        bool changedPhysicalWalls = false;
        bool presentationPrepared = false;
        bool nativeMachinimaPrepared = false;
        try
        {
            RuntimeBoundaryState? boundaryState = null;
            try
            {
                boundaryState =
                    await _boundaries.ReadAsync(cancellationToken);
            }
            catch (InvalidOperationException ex)
                when (ex.Message.Contains(
                    "No runtime kill/out-of-bounds triggers",
                    StringComparison.OrdinalIgnoreCase))
            {
                // Some authored spaces have no runtime kill/OOB bitset.
            }
            if (boundaryState is not null && !boundaryState.IsDisabled)
            {
                await _boundaries.DisableAsync(cancellationToken);
                changedBoundaries = true;
                _restoreBoundariesOnExit = !boundaryState.CanRestore;
            }

            bool physicalWallsDisabled =
                await _softCeilings.ReadDisabledAsync(cancellationToken);
            if (!physicalWallsDisabled)
            {
                await _softCeilings.SetDisabledAsync(true, cancellationToken);
                changedPhysicalWalls = true;
                _restorePhysicalWallsOnExit = true;
            }

            // Hide the Unreal HUD and capture the camera/player anchor before
            // native machinima changes the view state. Accessing MyHUD during
            // the native transition can leave UE4SS holding a stale object.
            ScriptExecutionResult presentation = await _bridge.ExecuteAsync(
                ScriptLanguage.MachinimaEnable,
                "enable",
                cancellationToken: cancellationToken);
            RawMachinimaState raw = ParseRawState(presentation);
            presentationPrepared = true;
            PlayerCoordinates player =
                await _player.ReadPositionAsync(cancellationToken);

            ScriptExecutionResult nativeState = await _bridge.ExecuteAsync(
                ScriptLanguage.BlamMachinima,
                "read",
                cancellationToken: cancellationToken);
            bool wasNativeEnabled = ParseNativeEnabled(nativeState);
            if (wasNativeEnabled)
            {
                // Capture the original enabled state, then create a real off/on
                // transition. Some mission loads initialize the raw flag to one
                // before the native free-camera system has observed an edge.
                await ExecuteNativeMachinimaAsync("enable", cancellationToken);
                nativeMachinimaPrepared = true;
                await ExecuteNativeMachinimaAsync("disable", cancellationToken);
                await Task.Delay(50, cancellationToken);
            }
            await ExecuteNativeMachinimaAsync("enable", cancellationToken);
            nativeMachinimaPrepared = true;

            return ToMachinimaState(raw, player);
        }
        catch
        {
            if (nativeMachinimaPrepared)
            {
                try
                {
                    await ExecuteNativeMachinimaAsync(
                        "restore",
                        CancellationToken.None);
                }
                catch
                {
                    // Preserve the original failure; mission reload is the fallback.
                }
            }
            if (presentationPrepared)
            {
                try
                {
                    await _bridge.ExecuteAsync(
                        ScriptLanguage.MachinimaDisable,
                        "disable",
                        cancellationToken: CancellationToken.None);
                }
                catch
                {
                    // Preserve the original failure; mission reload is the fallback.
                }
            }
            if (changedPhysicalWalls)
            {
                try
                {
                    await _softCeilings.SetDisabledAsync(
                        false,
                        CancellationToken.None);
                }
                catch
                {
                    // Preserve the original failure; the UI will still report it.
                }
                _restorePhysicalWallsOnExit = false;
            }
            if (changedBoundaries && _restoreBoundariesOnExit)
            {
                try
                {
                    await _boundaries.RestoreAsync(CancellationToken.None);
                }
                catch
                {
                    // Preserve the original failure; mission reload is the fallback.
                }
                _restoreBoundariesOnExit = false;
            }
            throw;
        }
    }

    public async Task<MachinimaState> ExitAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureBridgeReady();
        var restoreErrors = new List<string>();
        RawMachinimaState? raw = null;
        try
        {
            ScriptExecutionResult result = await _bridge.ExecuteAsync(
                ScriptLanguage.MachinimaDisable,
                "disable",
                cancellationToken: cancellationToken);
            raw = ParseRawState(result);
        }
        catch (Exception ex)
        {
            restoreErrors.Add("HUD: " + ex.Message);
        }
        try
        {
            await ExecuteNativeMachinimaAsync("restore", cancellationToken);
        }
        catch (Exception ex)
        {
            restoreErrors.Add("native camera: " + ex.Message);
        }
        if (_restorePhysicalWallsOnExit)
        {
            try
            {
                await _softCeilings.SetDisabledAsync(false, cancellationToken);
                _restorePhysicalWallsOnExit = false;
            }
            catch (Exception ex)
            {
                restoreErrors.Add("physical walls: " + ex.Message);
            }
        }
        if (_restoreBoundariesOnExit)
        {
            try
            {
                await _boundaries.RestoreAsync(cancellationToken);
                _restoreBoundariesOnExit = false;
            }
            catch (Exception ex)
            {
                restoreErrors.Add("kill/out-of-bounds triggers: " + ex.Message);
            }
        }
        if (restoreErrors.Count > 0 || raw is null)
        {
            throw new InvalidOperationException(
                "Advanced Machinima cleanup could not fully restore: " +
                string.Join(" · ", restoreErrors));
        }
        PlayerCoordinates player =
            await _player.ReadPositionAsync(cancellationToken);
        return ToMachinimaState(raw, player);
    }

    public Task<MachinimaState> MoveCameraAsync(
        MachinimaTransform transform,
        CancellationToken cancellationToken = default)
    {
        // Camera teleport stays blocked until Blam free-camera position fields
        // are verified. Never emulate it by moving the controlled player.
        // Spartan teleport remains available via TeleportSpartanAsync.
        _ = transform;
        _ = cancellationToken;
        throw new NotSupportedException(
            L.Get("advanced_machinima.camera_move_not_ready"));
    }

    public Task TeleportSpartanAsync(
        MachinimaTransform transform,
        CancellationToken cancellationToken = default)
        => _player.TeleportAsync(transform.Position, cancellationToken);

    public async Task<MachinimaState> MoveBothAsync(
        MachinimaTransform transform,
        CancellationToken cancellationToken = default)
    {
        MachinimaState state =
            await MoveCameraAsync(transform, cancellationToken);
        await TeleportSpartanAsync(transform, cancellationToken);
        return state;
    }

    public static void EnsureSameWorld(
        string currentWorld,
        SavedMachinimaLocation location)
    {
        if (!string.Equals(
                currentWorld,
                location.WorldName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"“{location.Name}” belongs to a different mission/world. " +
                "Load that mission before teleporting to it.");
        }
    }

    private void WriteLocations(
        IReadOnlyCollection<SavedMachinimaLocation> locations)
    {
        string? directory = Path.GetDirectoryName(_locationsPath);
        Directory.CreateDirectory(directory!);
        string temporary = _locationsPath + ".tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(
                locations
                    .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase),
                JsonOptions),
            new UTF8Encoding(false));
        File.Move(temporary, _locationsPath, true);
    }

    private async Task<RawMachinimaState> ReadRawStateAsync(
        CancellationToken cancellationToken)
    {
        EnsureBridgeReady();
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.MachinimaState,
            "read",
            cancellationToken: cancellationToken);
        return ParseRawState(result);
    }

    private async Task ExecuteNativeMachinimaAsync(
        string action,
        CancellationToken cancellationToken)
    {
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.BlamMachinima,
            action,
            cancellationToken: cancellationToken);
        EnsureConfirmed(result);
    }

    private static bool ParseNativeEnabled(ScriptExecutionResult result)
    {
        EnsureConfirmed(result);
        foreach (string line in result.Message.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (line == "enabled=0") return false;
            if (line == "enabled=1") return true;
        }
        throw new InvalidDataException(
            "The game returned invalid native machinima-camera state.");
    }

    private static RawMachinimaState ParseRawState(
        ScriptExecutionResult result)
    {
        EnsureConfirmed(result);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in result.Message.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf('=');
            if (separator <= 0)
                throw new InvalidDataException(
                    "The game returned invalid Advanced Machinima state.");
            values[line[..separator]] = line[(separator + 1)..];
        }

        if (!values.TryGetValue("enabled", out string? enabledText) ||
            enabledText is not ("0" or "1") ||
            !values.TryGetValue("world", out string? world) ||
            string.IsNullOrWhiteSpace(world) ||
            !TryGetFinite(values, "camera_ue_x", out float cameraUnrealX) ||
            !TryGetFinite(values, "camera_ue_y", out float cameraUnrealY) ||
            !TryGetFinite(values, "camera_ue_z", out float cameraUnrealZ) ||
            !TryGetFinite(values, "player_ue_x", out float playerUnrealX) ||
            !TryGetFinite(values, "player_ue_y", out float playerUnrealY) ||
            !TryGetFinite(values, "player_ue_z", out float playerUnrealZ) ||
            !TryGetFinite(values, "pitch", out float pitch) ||
            !TryGetFinite(values, "yaw", out float yaw) ||
            !TryGetFinite(values, "roll", out float roll))
        {
            throw new InvalidDataException(
                "The game returned incomplete Advanced Machinima state.");
        }
        return new RawMachinimaState(
            enabledText == "1",
            world,
            cameraUnrealX,
            cameraUnrealY,
            cameraUnrealZ,
            playerUnrealX,
            playerUnrealY,
            playerUnrealZ,
            pitch,
            yaw,
            roll);
    }

    private static MachinimaState ToMachinimaState(
        RawMachinimaState raw,
        PlayerCoordinates player)
    {
        PlayerCoordinates camera = ToBlamPosition(
            raw.CameraUnrealX,
            raw.CameraUnrealY,
            raw.CameraUnrealZ,
            raw,
            player);
        return new MachinimaState(
            raw.IsEnabled,
            raw.WorldName,
            new MachinimaTransform(
                camera.X,
                camera.Y,
                camera.Z,
                raw.Pitch,
                raw.Yaw,
                raw.Roll));
    }

    private static PlayerCoordinates ToBlamPosition(
        float unrealX,
        float unrealY,
        float unrealZ,
        RawMachinimaState anchor,
        PlayerCoordinates player)
        => new(
            player.X +
                (unrealX - anchor.PlayerUnrealX) /
                UnrealCentimetersPerBlamUnit,
            player.Y -
                (unrealY - anchor.PlayerUnrealY) /
                UnrealCentimetersPerBlamUnit,
            player.Z +
                (unrealZ - anchor.PlayerUnrealZ) /
                UnrealCentimetersPerBlamUnit);

    private static (float X, float Y, float Z) ToUnrealPosition(
        MachinimaTransform target,
        RawMachinimaState anchor,
        PlayerCoordinates player)
        => (
            anchor.PlayerUnrealX +
                (target.X - player.X) * UnrealCentimetersPerBlamUnit,
            anchor.PlayerUnrealY -
                (target.Y - player.Y) * UnrealCentimetersPerBlamUnit,
            anchor.PlayerUnrealZ +
                (target.Z - player.Z) * UnrealCentimetersPerBlamUnit);

    private static void EnsureConfirmed(ScriptExecutionResult result)
    {
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);
    }

    private static bool TryGetFinite(
        IReadOnlyDictionary<string, string> values,
        string key,
        out float value)
    {
        value = default;
        return values.TryGetValue(key, out string? text) &&
               TryParseFinite(text, out value);
    }

    private static bool TryParseFinite(string text, out float value)
        => float.TryParse(
               text,
               NumberStyles.Float,
               CultureInfo.InvariantCulture,
               out value) &&
           float.IsFinite(value);

    private static bool IsFinite(MachinimaTransform transform) =>
        float.IsFinite(transform.X) &&
        float.IsFinite(transform.Y) &&
        float.IsFinite(transform.Z) &&
        float.IsFinite(transform.Pitch) &&
        float.IsFinite(transform.Yaw) &&
        float.IsFinite(transform.Roll);

    private static string FriendlyActorName(string fullName)
    {
        int dot = fullName.LastIndexOf('.');
        string value = dot >= 0 ? fullName[(dot + 1)..] : fullName;
        value = value.Replace('_', ' ').Trim();
        return string.IsNullOrWhiteSpace(value)
            ? "Camera location"
            : value;
    }

    private void EnsureBridgeReady()
    {
        ScriptingBridgeStatus status = BridgeStatus;
        if (!status.IsRuntimeReady)
            throw new InvalidOperationException(
                L.Get("bridge.error_not_responding"));
        if (status.IsStale)
            throw new InvalidOperationException(status.Summary);
    }
}
