using System.Text;
using System.Text.Json;
using System.Diagnostics;
using HaloMeister.App.Localization;

namespace HaloMeister.App.Services;

public enum ScriptLanguage
{
    Lua,
    HaloScript,
    BlamSpawn,
    BlamBipedSpawn,
    BlamBipedVariantSpawn,
    BlamAiSpawn,
    BlamAiTeamSpawn,
    BlamWeaponLoad,
    BlamObjectVariant,
    BlamObjectColors,
    BlamWeaponVariant,
    BlamBipedPossess,
    BlamBumpPossessionOff,
    BlamCheatGlobalsRead,
    BlamCheatGlobalWrite,
    BlamSkullsRead,
    BlamSkullWrite,
    BlamSoftCeilingRead,
    BlamSoftCeilingWrite,
    BlamBoundariesRead,
    BlamBoundariesDisable,
    BlamBoundariesRestore,
    BlamTagAssetLoad,
    PlayerTeleport,
    PlayerNoClip,
    PlayerTeam,
    PlayerPosition,
    PlayerUnitTagRead,
    PlayerInput,
    BlamMachinima,
    MachinimaState,
    MachinimaNodes,
    MachinimaEnable,
    MachinimaDisable,
    MachinimaCameraTeleport,
}

/// <summary>
/// What the game actually told us about a request. The distinction matters: Unreal's
/// Lua and native bridge operations report whether the requested work completed.
/// </summary>
public enum ScriptOutcome
{
    /// <summary>The game reported a compile or runtime failure.</summary>
    Failed,

    /// <summary>The game accepted the request but cannot say whether it did anything.</summary>
    Submitted,

    /// <summary>The game ran the code and confirmed it completed.</summary>
    Confirmed,
}

public sealed record ScriptExecutionResult(
    string RequestId,
    ScriptLanguage Language,
    ScriptOutcome Outcome,
    string Message,
    TimeSpan Elapsed);

public sealed record ScriptingBridgeStatus(
    string? InstalledMainPath,
    bool IsInstalled,
    int? InstalledVersion,
    bool IsGameProcessRunning,
    bool IsRuntimeReady,
    DateTimeOffset? LastHeartbeat,
    int? RunningVersion,
    bool IsStale,
    string Summary);

public sealed class ScriptingBridgeService
{
    private const string RequestMagic = "HMREQ1";
    private const string ResultMagic = "HMRES1";
    private const string StatusMagic = "HMSTATUS1";
    private const string MarkerStart = "-- HALOMEISTER SCRIPTING BRIDGE:BEGIN";
    private const string MarkerEnd = "-- HALOMEISTER SCRIPTING BRIDGE:END";
    private const string MarkerVersion = "-- HALOMEISTER SCRIPTING BRIDGE:VERSION";
    private const int MaximumCodeBytes = 64 * 1024;
    private static readonly TimeSpan HeartbeatLifetime = TimeSpan.FromSeconds(8);
    // Fast probes for the common case where status.hm appears within a few seconds,
    // then settle into a steady poll. Callers cancel when they no longer need to wait.
    private static readonly TimeSpan[] HeartbeatInitialDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(750),
        TimeSpan.FromSeconds(1),
    ];
    private static readonly TimeSpan HeartbeatPollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly object CapabilityProfileGate = new();
    private static CapabilityProfileCache? _capabilityProfileCache;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private (DateTimeOffset? Heartbeat, int? Version) _lastStatus;
    private int? _packagedVersion;
    private bool _packagedVersionRead;

    private ScriptingBridgeService()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        BridgeRoot = Path.Combine(localAppData, "Meteorite", "Saved", "HaloMeister", "Scripting");
        RequestPath = Path.Combine(BridgeRoot, "request.hm");
        ProcessingPath = Path.Combine(BridgeRoot, "processing.hm");
        ResultPath = Path.Combine(BridgeRoot, "result.hm");
        StatusPath = Path.Combine(BridgeRoot, "status.hm");
        InstallLocationPath = Path.Combine(localAppData, "HaloMeister", "ue4ss-main-path.txt");
        BackupRoot = Path.Combine(localAppData, "HaloMeister", "UE4SSBackups");
        BridgeAssetPath = Path.Combine(AppContext.BaseDirectory, "Assets", "UE4SS", "bridge.lua");
        NativeBridgeAssetPath =
            Path.Combine(AppContext.BaseDirectory, "Assets", "UE4SS", "halomeister_blam_v45.dll");
        Directory.CreateDirectory(BridgeRoot);
    }

    public static ScriptingBridgeService Current { get; } = new();

    public string BridgeRoot { get; }
    public string RequestPath { get; }
    public string ProcessingPath { get; }
    public string ResultPath { get; }
    public string StatusPath { get; }
    public string InstallLocationPath { get; }
    public string BackupRoot { get; }
    public string BridgeAssetPath { get; }
    public string NativeBridgeAssetPath { get; }

    /// <summary>
    /// The version of the bridge this build of Halo Meister ships, if readable. The asset
    /// cannot change while the app runs, so read it once instead of on every status poll.
    /// </summary>
    public int? PackagedVersion
    {
        get
        {
            if (!_packagedVersionRead)
            {
                _packagedVersion = ReadBridgeVersion(BridgeAssetPath);
                _packagedVersionRead = true;
            }
            return _packagedVersion;
        }
    }

    public ScriptingBridgeStatus GetStatus()
    {
        string? mainPath = FindInstalledMainPath();
        bool installed = mainPath is not null && ContainsBridge(mainPath);
        int? installedVersion = installed && mainPath is not null
            ? ReadBridgeVersion(mainPath)
            : null;
        bool gameRunning = IsGameProcessRunning();
        (DateTimeOffset? heartbeat, int? runningVersion) = ReadStatusFile();
        bool ready = heartbeat is { } time && DateTimeOffset.UtcNow - time <= HeartbeatLifetime;

        int? packaged = PackagedVersion;
        bool installedStale = installed && packaged is { } expected &&
                             (installedVersion is null || installedVersion < expected);
        bool runningStale = ready && packaged is { } runtimeExpected &&
                     (runningVersion is null || runningVersion < runtimeExpected);
        // A bridge older than the one we ship reports outcomes we no longer trust, so it
        // has to be called out rather than shown as plain "Ready".
        bool stale = installedStale || runningStale;

        string summary = (installed, gameRunning, ready, runningStale, installedStale) switch
        {
            (_, _, true, true, _) => L.Format(
                "bridge.summary_stale",
                runningVersion?.ToString() ?? "1",
                packaged),
            (_, _, _, _, true) => L.Format(
                "bridge.summary_installed_stale",
                installedVersion?.ToString() ?? "unknown",
                packaged),
            (true, _, true, false, false) => L.Format(
                "bridge.summary_ready",
                runningVersion,
                heartbeat?.ToLocalTime().ToString("HH:mm:ss")),
            (true, true, false, false, false) => L.Get("bridge.summary_game_running_no_heartbeat"),
            (true, false, false, false, false) => L.Get("bridge.summary_installed_not_running"),
            (false, _, true, false, false) => L.Format(
                "bridge.summary_running_install_missing",
                heartbeat?.ToLocalTime().ToString("HH:mm:ss")),
            _ => L.Get("bridge.summary_not_installed"),
        };

        return new ScriptingBridgeStatus(
            mainPath, installed, installedVersion, gameRunning, ready, heartbeat, runningVersion, stale, summary);
    }

    /// <summary>
    /// Keeps reading the bridge heartbeat until one is live or
    /// <paramref name="cancellationToken"/> is cancelled. Works for both
    /// app-first and game-first launches: it does not stop just because the
    /// game process is not visible yet. Status-only; never runs a game operation.
    /// </summary>
    public async Task<ScriptingBridgeStatus> WaitForHeartbeatAsync(
        CancellationToken cancellationToken = default)
    {
        ScriptingBridgeStatus status = GetStatus();
        if (status.IsRuntimeReady)
            return status;

        foreach (TimeSpan delay in HeartbeatInitialDelays)
        {
            await Task.Delay(delay, cancellationToken);
            status = GetStatus();
            if (status.IsRuntimeReady)
                return status;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(HeartbeatPollInterval, cancellationToken);
            status = GetStatus();
            if (status.IsRuntimeReady)
                return status;
        }

        return status;
    }

    public async Task<ScriptExecutionResult> ExecuteAsync(
        ScriptLanguage language,
        string code,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Enter a script before running it.", nameof(code));
        byte[] codeBytes = Utf8.GetBytes(code);
        if (codeBytes.Length > MaximumCodeBytes)
            throw new ArgumentException($"Scripts are limited to {MaximumCodeBytes / 1024} KiB.", nameof(code));

        await _executionGate.WaitAsync(cancellationToken);
        try
        {
            ScriptingBridgeStatus status = GetStatus();
            if (!status.IsRuntimeReady)
                throw new InvalidOperationException(L.Get("bridge.error_scripting_not_responding"));
            EnsureCapabilityReady(language);

            string requestId = Guid.NewGuid().ToString("N");
            string kind = language switch
            {
                ScriptLanguage.Lua => "lua",
                ScriptLanguage.HaloScript => "haloscript",
                ScriptLanguage.BlamSpawn => "blam_spawn",
                ScriptLanguage.BlamBipedSpawn => "blam_biped_spawn",
                ScriptLanguage.BlamBipedVariantSpawn => "blam_biped_variant_spawn",
                ScriptLanguage.BlamAiSpawn => "blam_ai_spawn",
                ScriptLanguage.BlamAiTeamSpawn => "blam_ai_team_spawn",
                ScriptLanguage.BlamWeaponLoad => "blam_weapon_load",
                ScriptLanguage.BlamObjectVariant => "blam_object_variant",
                ScriptLanguage.BlamObjectColors => "blam_object_colors",
                ScriptLanguage.BlamWeaponVariant => "blam_weapon_variant",
                ScriptLanguage.BlamBipedPossess => "blam_biped_possess",
                ScriptLanguage.BlamBumpPossessionOff => "blam_bump_possession_off",
                ScriptLanguage.BlamCheatGlobalsRead => "blam_cheat_globals_read",
                ScriptLanguage.BlamCheatGlobalWrite => "blam_cheat_global_write",
                ScriptLanguage.BlamSkullsRead => "blam_skulls_read",
                ScriptLanguage.BlamSkullWrite => "blam_skull_write",
                ScriptLanguage.BlamSoftCeilingRead => "blam_soft_ceiling_read",
                ScriptLanguage.BlamSoftCeilingWrite => "blam_soft_ceiling_write",
                ScriptLanguage.BlamBoundariesRead => "blam_boundaries_read",
                ScriptLanguage.BlamBoundariesDisable => "blam_boundaries_disable",
                ScriptLanguage.BlamBoundariesRestore => "blam_boundaries_restore",
                ScriptLanguage.BlamTagAssetLoad => "blam_tag_asset_load",
                ScriptLanguage.PlayerTeleport => "player_teleport",
                ScriptLanguage.PlayerNoClip => "player_noclip",
                ScriptLanguage.PlayerTeam => "player_team",
                ScriptLanguage.PlayerPosition => "player_position",
                ScriptLanguage.PlayerUnitTagRead => "player_unit_tag_read",
                ScriptLanguage.PlayerInput => "player_input",
                ScriptLanguage.BlamMachinima => "blam_machinima",
                ScriptLanguage.MachinimaState => "machinima_state",
                ScriptLanguage.MachinimaNodes => "machinima_nodes",
                ScriptLanguage.MachinimaEnable => "machinima_enable",
                ScriptLanguage.MachinimaDisable => "machinima_disable",
                ScriptLanguage.MachinimaCameraTeleport =>
                    "machinima_camera_teleport",
                _ => throw new ArgumentOutOfRangeException(nameof(language)),
            };

            if (File.Exists(RequestPath) || File.Exists(ProcessingPath))
                throw new InvalidOperationException(
                    "Another scripting request is already pending in the game mailbox.");

            DeleteIfExists(ResultPath);
            WriteAtomic(RequestPath, $"{RequestMagic}\n{requestId}\n{kind}\n{code}");

            DateTimeOffset started = DateTimeOffset.UtcNow;
            TimeSpan waitFor = timeout ?? DefaultTimeout;
            try
            {
                while (DateTimeOffset.UtcNow - started < waitFor)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ScriptExecutionResult? result = TryReadResult(requestId, language, started);
                    if (result is not null)
                    {
                        DeleteIfExists(ResultPath);
                        return result;
                    }
                    await Task.Delay(125, cancellationToken);
                }
            }
            catch
            {
                DeleteRequestIfOwned(requestId);
                throw;
            }

            DeleteRequestIfOwned(requestId);
            throw new TimeoutException(
                $"The game did not finish the {Display(language)} request within {waitFor.TotalSeconds:0} seconds. " +
                "Any unclaimed mailbox request was removed; code already running inside the game cannot be cancelled.");
        }
        finally
        {
            _executionGate.Release();
        }
    }

    private static void EnsureCapabilityReady(ScriptLanguage language)
    {
        LiveToolCapability? capability = LiveToolCapabilityCatalog.For(language);
        if (capability is null)
            return;

        Process process = Process.GetProcessesByName("HaloCampaignEvolved").SingleOrDefault()
            ?? throw new InvalidOperationException(L.Get("shell.game_not_running"));
        try
        {
            long startTicks = process.StartTime.ToUniversalTime().Ticks;
            GameBuildProfile profile;
            lock (CapabilityProfileGate)
            {
                if (_capabilityProfileCache is { } cached &&
                    cached.ProcessId == process.Id &&
                    cached.StartTicks == startTicks)
                {
                    profile = cached.Profile;
                }
                else
                {
                    ProcessModule module = process.Modules.Cast<ProcessModule>().SingleOrDefault(candidate =>
                        candidate.ModuleName.Equals(
                            "HaloSimulation_tag_release.dll",
                            StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException(
                            "Load an offline campaign mission before using this live tool.");
                    profile = GameBuildProfileCatalog.Resolve(module.FileName);
                    _capabilityProfileCache = new CapabilityProfileCache(
                        process.Id, startTicks, profile);
                }
            }
            CapabilityValidationLevel level =
                GameBuildProfileCatalog.GetCapability(profile, capability.Value);
            if (level < CapabilityValidationLevel.Integrated)
            {
                throw new NotSupportedException(
                    $"{capability} is {level} for build '{profile.Id}' and is not enabled. " +
                    "Update the build profile after static and live validation.");
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private sealed record CapabilityProfileCache(
        int ProcessId,
        long StartTicks,
        GameBuildProfile Profile);

    public string InstallOrUpdateBridge(string? gameRoot = null)
    {
        string bridgeAssetPath = BridgeAssetPath;
        if (!File.Exists(bridgeAssetPath))
            throw new FileNotFoundException("The packaged UE4SS bridge asset is missing.", bridgeAssetPath);
        if (!File.Exists(NativeBridgeAssetPath))
            throw new FileNotFoundException(
                "The packaged native Blam bridge is missing.",
                NativeBridgeAssetPath);

        string? mainPath = gameRoot is null ? FindInstalledMainPath() : ResolveMainPath(gameRoot);

        // A first-time install has no HaloMeister mod yet. Creating it is the whole point of
        // the button, so scaffold the mod and enable it rather than demanding it already exist.
        mainPath ??= CreateModScaffold(gameRoot);
        if (mainPath is null)
            throw new DirectoryNotFoundException(
                "Could not find a UE4SS installation (ue4ss\\Mods) under the selected folder. " +
                "Select the Halo: Campaign Evolved installation folder, or install UE4SS first.");

        string bridge = File.ReadAllText(bridgeAssetPath, Utf8);
        if (!bridge.Contains(MarkerStart, StringComparison.Ordinal) ||
            !bridge.Contains(MarkerEnd, StringComparison.Ordinal))
            throw new InvalidDataException("The packaged bridge asset has invalid installation markers.");
        int? bridgeVersion = ReadBridgeVersion(bridgeAssetPath);
        if (bridgeVersion is null ||
            !bridge.Contains(
                $"local bridge_version = {bridgeVersion.Value}",
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "The packaged bridge marker and reported heartbeat version do not match.");

        // Stage the versioned native module first. If this fails, leave main.lua
        // untouched so the installed Lua never points at a missing DLL. Old
        // versioned modules may remain mapped until the game exits and are inert
        // once the updated Lua selects the new filename on the next launch.
        InstallNativeBridge(mainPath);

        string original = ReadLuaText(mainPath);
        string updated = ReplaceMarkedBlock(original, bridge);
        if (!string.Equals(original, updated, StringComparison.Ordinal))
        {
            Directory.CreateDirectory(BackupRoot);
            string backupPath = Path.Combine(BackupRoot, $"main-{DateTime.Now:yyyyMMdd-HHmmss-fff}.lua");
            File.Copy(mainPath, backupPath, overwrite: false);
            WriteAtomic(mainPath, updated);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(InstallLocationPath)!);
        File.WriteAllText(InstallLocationPath, mainPath, Utf8);
        Directory.CreateDirectory(BridgeRoot);
        return mainPath;
    }

    public string? FindInstalledMainPath()
    {
        if (File.Exists(InstallLocationPath))
        {
            try
            {
                string remembered = File.ReadAllText(InstallLocationPath).Trim();
                if (File.Exists(remembered))
                    return Path.GetFullPath(remembered);
            }
            catch
            {
                // Continue with known installation layouts.
            }
        }

        foreach (string root in CandidateGameRoots())
        {
            string? mainPath = ResolveMainPath(root);
            if (mainPath is not null)
                return mainPath;
        }
        return null;
    }

    /// <summary>
    /// Reads the heartbeat and, from bridge v2 onward, the version the game is running.
    /// <para>
    /// The bridge rewrites this file once a second by deleting and renaming, so a read can
    /// legitimately land on a missing or locked file. Falling back to the last good read
    /// keeps that from showing up as "the bridge is not running" and disabling the Run
    /// buttons. This cannot mask a bridge that really stopped: the cached heartbeat still
    /// ages out against <see cref="HeartbeatLifetime"/>.
    /// </para>
    /// </summary>
    private (DateTimeOffset? Heartbeat, int? Version) ReadStatusFile()
    {
        // The Lua bridge rewrites status.hm via delete+rename. Share-compatible
        // retries avoid treating that momentary gap as "no heartbeat" when the
        // game was already running before Halo Meister started.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    StatusPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true);
                var lines = new List<string>(4);
                while (lines.Count < 4 && reader.ReadLine() is { } line)
                    lines.Add(line);
                if (lines.Count < 2 || lines[0] != StatusMagic ||
                    !long.TryParse(lines[1], out long unixTime))
                {
                    return _lastStatus;
                }

                int? version = lines.Count >= 4 && int.TryParse(lines[3].Trim(), out int parsed)
                    ? parsed
                    : null;
                _lastStatus = (DateTimeOffset.FromUnixTimeSeconds(unixTime), version);
                return _lastStatus;
            }
            catch (FileNotFoundException)
            {
                if (attempt < 3)
                {
                    Thread.Sleep(25);
                    continue;
                }
                return _lastStatus;
            }
            catch (DirectoryNotFoundException)
            {
                Directory.CreateDirectory(BridgeRoot);
                return _lastStatus;
            }
            catch (IOException)
            {
                if (attempt < 3)
                {
                    Thread.Sleep(25);
                    continue;
                }
                return _lastStatus;
            }
            catch (UnauthorizedAccessException)
            {
                return _lastStatus;
            }
            catch
            {
                return _lastStatus;
            }
        }

        return _lastStatus;
    }

    private static bool IsGameProcessRunning()
    {
        Process[] processes = Process.GetProcessesByName("HaloCampaignEvolved");
        foreach (Process process in processes)
            process.Dispose();
        return processes.Length > 0;
    }

    private static int? ReadBridgeVersion(string path)
    {
        try
        {
            foreach (string line in File.ReadLines(path))
            {
                int marker = line.IndexOf(MarkerVersion, StringComparison.Ordinal);
                if (marker < 0)
                    continue;
                string value = line[(marker + MarkerVersion.Length)..].Trim();
                return int.TryParse(value, out int version) ? version : null;
            }
        }
        catch
        {
            // An unreadable or marker-less file just means "version unknown".
        }
        return null;
    }

    private ScriptExecutionResult? TryReadResult(
        string requestId,
        ScriptLanguage language,
        DateTimeOffset started)
    {
        try
        {
            if (!File.Exists(ResultPath))
                return null;
            string result = File.ReadAllText(ResultPath, Utf8);
            string[] parts = result.Split('\n', 4);
            if (parts.Length < 4 || parts[0].TrimEnd('\r') != ResultMagic ||
                !string.Equals(parts[1].Trim(), requestId, StringComparison.Ordinal))
                return null;

            ScriptOutcome outcome = parts[2].Trim() switch
            {
                "ok" when language is
                    ScriptLanguage.Lua or
                    ScriptLanguage.BlamSpawn or
                    ScriptLanguage.BlamBipedSpawn or
                    ScriptLanguage.BlamBipedVariantSpawn or
                    ScriptLanguage.BlamAiSpawn or
                    ScriptLanguage.BlamAiTeamSpawn or
                    ScriptLanguage.BlamWeaponLoad or
                    ScriptLanguage.BlamObjectVariant or
                    ScriptLanguage.BlamObjectColors or
                    ScriptLanguage.BlamWeaponVariant or
                    ScriptLanguage.BlamBipedPossess or
                    ScriptLanguage.BlamBumpPossessionOff or
                    ScriptLanguage.BlamCheatGlobalsRead or
                    ScriptLanguage.BlamCheatGlobalWrite or
                    ScriptLanguage.BlamSkullsRead or
                    ScriptLanguage.BlamSkullWrite or
                    ScriptLanguage.BlamSoftCeilingRead or
                    ScriptLanguage.BlamSoftCeilingWrite or
                    ScriptLanguage.BlamBoundariesRead or
                    ScriptLanguage.BlamBoundariesDisable or
                    ScriptLanguage.BlamBoundariesRestore or
                    ScriptLanguage.BlamTagAssetLoad or
                    ScriptLanguage.PlayerTeleport or
                    ScriptLanguage.PlayerNoClip or
                    ScriptLanguage.PlayerTeam or
                    ScriptLanguage.PlayerPosition or
                    ScriptLanguage.PlayerUnitTagRead or
                    ScriptLanguage.PlayerInput or
                    ScriptLanguage.BlamMachinima or
                    ScriptLanguage.MachinimaState or
                    ScriptLanguage.MachinimaNodes or
                    ScriptLanguage.MachinimaEnable or
                    ScriptLanguage.MachinimaDisable or
                    ScriptLanguage.MachinimaCameraTeleport =>
                    ScriptOutcome.Confirmed,
                "ok" => ScriptOutcome.Submitted,
                "submitted" => ScriptOutcome.Submitted,
                _ => ScriptOutcome.Failed,
            };

            return new ScriptExecutionResult(
                requestId,
                language,
                outcome,
                parts[3].TrimEnd(),
                DateTimeOffset.UtcNow - started);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void DeleteRequestIfOwned(string requestId)
    {
        foreach (string path in new[] { RequestPath, ProcessingPath })
        {
            try
            {
                if (!File.Exists(path))
                    continue;
                using var reader = new StreamReader(path, Utf8, false);
                _ = reader.ReadLine();
                if (string.Equals(reader.ReadLine(), requestId, StringComparison.Ordinal))
                    File.Delete(path);
            }
            catch
            {
                // The bridge may own the file now; it will finish or report an error.
            }
        }
    }

    private static string ReplaceMarkedBlock(string original, string bridge)
    {
        int start = original.IndexOf(MarkerStart, StringComparison.Ordinal);
        int end = original.IndexOf(MarkerEnd, StringComparison.Ordinal);
        if (start < 0 && end < 0)
            return original.TrimEnd() + Environment.NewLine + Environment.NewLine +
                   bridge.Trim() + Environment.NewLine;
        if (start < 0 || end < start)
            throw new InvalidDataException("The installed main.lua contains an incomplete Halo Meister bridge block.");

        end += MarkerEnd.Length;
        return original[..start].TrimEnd() + Environment.NewLine + Environment.NewLine +
               bridge.Trim() + Environment.NewLine +
               original[end..].TrimStart('\r', '\n');
    }

    private void InstallNativeBridge(string mainPath)
    {
        string target = Path.Combine(
            Path.GetDirectoryName(mainPath)!,
            Path.GetFileName(NativeBridgeAssetPath));
        if (File.Exists(target) &&
            File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(NativeBridgeAssetPath)))
            return;

        Directory.CreateDirectory(BackupRoot);
        if (File.Exists(target))
        {
            string backup = Path.Combine(
                BackupRoot,
                $"halomeister-blam-{DateTime.Now:yyyyMMdd-HHmmss-fff}.dll");
            File.Copy(target, backup, overwrite: false);
        }
        File.Copy(NativeBridgeAssetPath, target, overwrite: true);
    }

    private static bool ContainsBridge(string mainPath)
    {
        try
        {
            string text = ReadLuaText(mainPath);
            return text.Contains(MarkerStart, StringComparison.Ordinal) &&
                   text.Contains(MarkerEnd, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads a hand-edited Lua file. A single stray non-UTF-8 byte anywhere in main.lua must
    /// not make the bridge look uninstalled, so fall back to a lossy read.
    /// </summary>
    private static string ReadLuaText(string path)
    {
        try
        {
            return File.ReadAllText(path, Utf8);
        }
        catch (DecoderFallbackException)
        {
            return File.ReadAllText(path, new UTF8Encoding(false, false));
        }
    }

    /// <summary>
    /// Creates ue4ss\Mods\HaloMeister\Scripts\main.lua and enables the mod, returning the
    /// new main.lua path. Returns null when no UE4SS installation can be located.
    /// </summary>
    private static string? CreateModScaffold(string? gameRoot)
    {
        IEnumerable<string> roots = gameRoot is null
            ? CandidateGameRoots()
            : [gameRoot];

        foreach (string root in roots)
        {
            string? modsDirectory = ResolveModsDirectory(root);
            if (modsDirectory is null)
                continue;

            string scripts = Path.Combine(modsDirectory, "HaloMeister", "Scripts");
            Directory.CreateDirectory(scripts);
            string mainPath = Path.Combine(scripts, "main.lua");
            if (!File.Exists(mainPath))
                File.WriteAllText(mainPath, string.Empty, Utf8);

            EnableModInTextList(Path.Combine(modsDirectory, "mods.txt"));
            EnableModInJsonList(Path.Combine(modsDirectory, "mods.json"));
            return mainPath;
        }
        return null;
    }

    private static string? ResolveModsDirectory(string gameRoot)
    {
        try
        {
            string fullRoot = Path.GetFullPath(gameRoot);
            string[] candidates =
            [
                Path.Combine(fullRoot, "Content", "Meteorite", "Binaries", "WinGDK", "ue4ss", "Mods"),
                Path.Combine(fullRoot, "Content", "Meteorite", "Binaries", "Win64", "ue4ss", "Mods"),
                Path.Combine(fullRoot, "Meteorite", "Binaries", "WinGDK", "ue4ss", "Mods"),
                Path.Combine(fullRoot, "Meteorite", "Binaries", "Win64", "ue4ss", "Mods"),
                Path.Combine(fullRoot, "ue4ss", "Mods"),
                fullRoot,
            ];

            foreach (string candidate in candidates)
            {
                // Only treat it as a UE4SS Mods folder if UE4SS itself is next to it.
                string? parent = Path.GetDirectoryName(candidate);
                if (Directory.Exists(candidate) && parent is not null &&
                    File.Exists(Path.Combine(parent, "UE4SS.dll")))
                    return candidate;
            }
        }
        catch
        {
            // Fall through to the next candidate root.
        }
        return null;
    }

    private static void EnableModInTextList(string modsTextPath)
    {
        try
        {
            if (!File.Exists(modsTextPath))
                return;

            List<string> lines = [.. File.ReadAllLines(modsTextPath, Utf8)];
            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith(';') ||
                    !trimmed.StartsWith("HaloMeister", StringComparison.Ordinal))
                    continue;
                lines[i] = "HaloMeister : 1";
                File.WriteAllLines(modsTextPath, lines, Utf8);
                return;
            }

            lines.Add("HaloMeister : 1");
            File.WriteAllLines(modsTextPath, lines, Utf8);
        }
        catch
        {
            // The install still works if the user enables the mod by hand.
        }
    }

    private static void EnableModInJsonList(string modsJsonPath)
    {
        try
        {
            if (!File.Exists(modsJsonPath))
                return;

            var entries = JsonSerializer.Deserialize<List<Ue4ssModEntry>>(
                File.ReadAllText(modsJsonPath, Utf8)) ?? [];

            int index = entries.FindIndex(entry =>
                string.Equals(entry.mod_name, "HaloMeister", StringComparison.Ordinal));
            if (index >= 0)
                entries[index] = entries[index] with { mod_enabled = true };
            else
                entries.Add(new Ue4ssModEntry("HaloMeister", true));

            File.WriteAllText(
                modsJsonPath,
                JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }),
                Utf8);
        }
        catch
        {
            // Same as mods.txt: a failure here is recoverable by hand.
        }
    }

    private sealed record Ue4ssModEntry(string mod_name, bool mod_enabled);

    private static string? ResolveMainPath(string gameRoot)
    {
        try
        {
            string fullRoot = Path.GetFullPath(gameRoot);
            string[] relatives =
            [
                Path.Combine(
                    "Content", "Meteorite", "Binaries", "WinGDK",
                    "ue4ss", "Mods", "HaloMeister", "Scripts", "main.lua"),
                Path.Combine(
                    "Content", "Meteorite", "Binaries", "Win64",
                    "ue4ss", "Mods", "HaloMeister", "Scripts", "main.lua"),
                Path.Combine(
                    "Meteorite", "Binaries", "WinGDK",
                    "ue4ss", "Mods", "HaloMeister", "Scripts", "main.lua"),
                Path.Combine(
                    "Meteorite", "Binaries", "Win64",
                    "ue4ss", "Mods", "HaloMeister", "Scripts", "main.lua"),
            ];
            foreach (string relative in relatives)
            {
                string candidate = Path.Combine(fullRoot, relative);
                if (File.Exists(candidate))
                    return candidate;
            }

            if (string.Equals(Path.GetFileName(fullRoot), "Scripts", StringComparison.OrdinalIgnoreCase))
            {
                string scriptsCandidate = Path.Combine(fullRoot, "main.lua");
                if (File.Exists(scriptsCandidate))
                    return scriptsCandidate;
            }

            string directCandidate = Path.Combine(
                fullRoot,
                "ue4ss",
                "Mods",
                "HaloMeister",
                "Scripts",
                "main.lua");
            return File.Exists(directCandidate) ? directCandidate : null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> CandidateGameRoots()
    {
        string? configured = Environment.GetEnvironmentVariable("HALO_CAMPAIGN_EVOLVED_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
            yield return configured;

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
                continue;
            yield return Path.Combine(drive.RootDirectory.FullName, "Games", "Halo- Campaign Evolved");
            yield return Path.Combine(drive.RootDirectory.FullName, "XboxGames", "Halo Campaign Evolved");
            yield return Path.Combine(drive.RootDirectory.FullName, "XboxGames", "Halo- Campaign Evolved");
        }
    }

    private static void WriteAtomic(string destinationPath, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        string temporaryPath = destinationPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, content, Utf8);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            DeleteIfExists(temporaryPath);
        }
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static string Display(ScriptLanguage language)
        => language switch
        {
            ScriptLanguage.HaloScript => "HaloScript",
            ScriptLanguage.BlamSpawn => "Blam object spawn",
            ScriptLanguage.BlamBipedSpawn => "Blam biped spawn",
            ScriptLanguage.BlamBipedVariantSpawn => "Blam variant biped spawn",
            ScriptLanguage.BlamAiSpawn => "Blam AI spawn",
            ScriptLanguage.BlamAiTeamSpawn => "Blam AI team spawn",
            ScriptLanguage.BlamWeaponLoad => "Blam weapon load",
            ScriptLanguage.BlamObjectVariant => "live player model variant",
            ScriptLanguage.BlamWeaponVariant => "live weapon model variant",
            ScriptLanguage.BlamBipedPossess => "Blam biped bump possession",
            ScriptLanguage.BlamBumpPossessionOff => "disable Blam bump possession",
            ScriptLanguage.BlamCheatGlobalsRead => "read Blam cheat globals",
            ScriptLanguage.BlamCheatGlobalWrite => "write Blam cheat global",
            ScriptLanguage.BlamSkullsRead => "read live campaign skulls",
            ScriptLanguage.BlamSkullWrite => "write live campaign skull",
            ScriptLanguage.BlamSoftCeilingRead => "read physical-wall override",
            ScriptLanguage.BlamSoftCeilingWrite => "write physical-wall override",
            ScriptLanguage.BlamBoundariesRead => "read runtime boundaries",
            ScriptLanguage.BlamBoundariesDisable => "disable runtime boundaries",
            ScriptLanguage.BlamBoundariesRestore => "restore runtime boundaries",
            ScriptLanguage.BlamTagAssetLoad => "Blam tag asset load",
            ScriptLanguage.PlayerTeleport => "player teleport",
            ScriptLanguage.PlayerNoClip => "player no-clip",
            ScriptLanguage.PlayerTeam => "player allegiance",
            ScriptLanguage.PlayerPosition => "player position",
            ScriptLanguage.PlayerUnitTagRead => "read controlled player unit tag",
            ScriptLanguage.PlayerInput => "suppress or restore player input",
            ScriptLanguage.BlamMachinima => "native Blam machinima camera",
            ScriptLanguage.MachinimaState => "read Advanced Machinima state",
            ScriptLanguage.MachinimaNodes => "read live camera-location nodes",
            ScriptLanguage.MachinimaEnable => "enter Advanced Machinima",
            ScriptLanguage.MachinimaDisable => "leave Advanced Machinima",
            ScriptLanguage.MachinimaCameraTeleport => "move machinima camera",
            _ => language.ToString(),
        };
}
