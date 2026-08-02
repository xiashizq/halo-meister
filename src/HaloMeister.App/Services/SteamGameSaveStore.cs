using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using HaloMeister.App.Localization;
using HaloMeister.Core;
using Windows.System;

namespace HaloMeister.App.Services;

public sealed class SteamGameSaveStore : IGameSaveStore
{
    public const string Platform = "steam";
    public const int SteamAppId = 2806050;

    public SteamGameSaveStore()
    {
        LiveRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Meteorite",
            "Saved",
            "SaveGames");
        BackupRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HaloMeister",
            "GameSaveBackupsSteam");
    }

    public string PlatformId => Platform;
    public string LiveRoot { get; }
    public string BackupRoot { get; }
    public bool LiveRootExists => Directory.Exists(LiveRoot);
    public bool IsGameRunning => Process.GetProcessesByName("HaloCampaignEvolved").Length > 0;

    private string ExportRoot => Path.Combine(BackupRoot, "Exports");
    private string ImportRoot => Path.Combine(BackupRoot, "Imports");

    public IReadOnlyList<WgsSaveSlot> Discover()
        => DiscoverSlotsFromDirectory(LiveRoot);

    public IReadOnlyList<WgsBackupEntry> DiscoverBackups()
    {
        if (!Directory.Exists(BackupRoot))
            return [];

        var results = new List<WgsBackupEntry>();

        foreach (string directory in Directory.EnumerateDirectories(BackupRoot))
        {
            string name = Path.GetFileName(directory);
            if (name.Equals("Exports", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Imports", StringComparison.OrdinalIgnoreCase))
                continue;

            BackupManifest manifest = ReadBackupManifest(directory);
            foreach (WgsSaveSlot slot in DiscoverSlotsFromDirectory(directory)
                         .Where(slot => slot.Save.Kind == WgsGameSaveKind.Checkpoint))
            {
                results.Add(new WgsBackupEntry(
                    $"snapshot:{directory}:{slot.ContainerId}",
                    slot.DataPath,
                    slot.ContainerId,
                    slot.Save,
                    manifest.CreatedUtc.ToLocalTime(),
                    manifest.Reason,
                    L.Get("game_saves.origin_steam_snapshot"),
                    IsArchive: false));
            }
        }

        foreach (string library in new[] { ExportRoot, ImportRoot })
        {
            if (!Directory.Exists(library)) continue;
            bool imported = library.Equals(ImportRoot, StringComparison.OrdinalIgnoreCase);
            string origin = imported
                ? L.Get("game_saves.origin_imported_archive")
                : L.Get("game_saves.origin_exported_archive");

            foreach (string archive in Directory.EnumerateFiles(library, "*", SearchOption.TopDirectoryOnly)
                         .Where(IsArchivePath))
            {
                try
                {
                    results.Add(InspectArchive(archive, origin, imported));
                }
                catch
                {
                    // A damaged or partially copied archive should not break the page.
                }
            }
        }

        return results
            .OrderByDescending(backup => backup.CreatedLocal)
            .ThenBy(backup => backup.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public WgsBackupResult BackupAll(string reason)
    {
        if (!Directory.Exists(LiveRoot))
            throw new DirectoryNotFoundException(L.Format("game_saves.live_folder_missing", LiveRoot));

        Directory.CreateDirectory(BackupRoot);
        string destination = Path.Combine(
            BackupRoot,
            $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{SafeName(reason)}");
        Directory.CreateDirectory(destination);

        int count = 0;
        foreach (string source in Directory.EnumerateFiles(LiveRoot, "*", SearchOption.TopDirectoryOnly))
        {
            string target = Path.Combine(destination, Path.GetFileName(source));
            File.Copy(source, target, overwrite: false);
            count++;
        }

        var manifest = new
        {
            format = 1,
            platform = Platform,
            createdUtc = DateTime.UtcNow,
            reason,
            source = LiveRoot,
            steamAppId = SteamAppId,
            fileCount = count,
        };
        File.WriteAllText(
            Path.Combine(destination, "halomeister-backup.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        return new WgsBackupResult(destination, count);
    }

    public WgsBackupEntry ExportToLibrary(WgsSaveSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        Directory.CreateDirectory(ExportRoot);
        string mission = SafeName(slot.Save.ScenarioCode ?? slot.Save.Kind.ToString());
        string destination = UniquePath(
            ExportRoot,
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{mission}.halo-wgs");
        ExportSlot(slot, destination);
        return InspectArchive(destination, L.Get("game_saves.origin_exported_archive"), imported: false);
    }

    public WgsBackupEntry ImportToLibrary(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException(L.Get("game_saves.archive_missing"), sourcePath);
        if (!IsArchivePath(sourcePath))
            throw new InvalidDataException(L.Get("game_saves.select_halo_wgs_or_zip"));

        byte[] data = ReadImportData(sourcePath);
        WgsGameSaveInfo info = WgsGameSave.Inspect(sourcePath, data);
        if (info.Kind != WgsGameSaveKind.Checkpoint)
            throw new InvalidDataException(L.Get("game_saves.archive_not_checkpoint"));

        Directory.CreateDirectory(ImportRoot);
        string baseName = SafeName(Path.GetFileNameWithoutExtension(sourcePath));
        string destination = UniquePath(
            ImportRoot,
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{baseName}.halo-wgs");
        File.Copy(sourcePath, destination, overwrite: false);
        return InspectArchive(destination, L.Get("game_saves.origin_imported_archive"), imported: true);
    }

    public WgsReplaceResult RestoreBackup(WgsSaveSlot target, WgsBackupEntry backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        byte[] replacement = backup.IsArchive
            ? ReadImportData(backup.SourcePath)
            : File.ReadAllBytes(backup.SourcePath);
        return ReplaceSlotData(target, replacement, "before-backup-restore");
    }

    public async Task<bool> LaunchGameAsync()
        => await Launcher.LaunchUriAsync(new Uri($"steam://rungameid/{SteamAppId}"));

    private void ExportSlot(WgsSaveSlot slot, string destination)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        string? parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        using FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        AddFile(archive, slot.DataPath, "Data");

        var manifest = new
        {
            format = 1,
            platform = Platform,
            steamAppId = SteamAppId,
            slot.ContainerId,
            kind = slot.Save.Kind.ToString(),
            slot.Save.ScenarioCode,
            slot.Save.ScenarioTitle,
            slot.Save.Difficulty,
            slot.Save.InternalCheckpoint,
            slot.Save.Size,
            slot.Save.Sha256,
            exportedUtc = DateTime.UtcNow,
        };
        ZipArchiveEntry entry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using StreamWriter writer = new(entry.Open());
        writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private WgsReplaceResult ReplaceSlotData(
        WgsSaveSlot target,
        ReadOnlySpan<byte> replacement,
        string backupReason)
    {
        if (IsGameRunning)
            throw new InvalidOperationException(L.Get("game_saves.close_game_before_restore"));
        if (target.Save.Kind != WgsGameSaveKind.Checkpoint)
            throw new InvalidOperationException(L.Get("game_saves.only_checkpoint_restore"));

        byte[] replacementBytes = replacement.ToArray();
        WgsGameSaveInfo imported = WgsGameSave.Inspect("backup restore", replacementBytes);
        if (imported.Kind != WgsGameSaveKind.Checkpoint)
            throw new InvalidDataException(L.Get("game_saves.backup_not_checkpoint"));

        WgsBackupResult safetyBackup = BackupAll(backupReason);
        string targetDirectory = Path.GetDirectoryName(target.DataPath)
            ?? throw new InvalidOperationException(L.Get("game_saves.target_slot_missing_directory"));
        string tempPath = Path.Combine(targetDirectory, $".halomeister-{Guid.NewGuid():N}.tmp");

        try
        {
            using (FileStream stream = new(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(replacementBytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, target.DataPath, overwrite: true);
            File.SetLastWriteTimeUtc(target.DataPath, DateTime.UtcNow);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        WgsSaveSlot updated = Discover().First(slot =>
            slot.ContainerId.Equals(target.ContainerId, StringComparison.OrdinalIgnoreCase));
        return new WgsReplaceResult(safetyBackup.DirectoryPath, updated);
    }

    private static IReadOnlyList<WgsSaveSlot> DiscoverSlotsFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return [];

        var slots = new List<WgsSaveSlot>();
        foreach (string path in Directory.EnumerateFiles(directory, "*.sav", SearchOption.TopDirectoryOnly)
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            string fileName = Path.GetFileName(path);
            if (!fileName.StartsWith("CoreSave_", StringComparison.OrdinalIgnoreCase) &&
                !fileName.Equals("Progress.sav", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                WgsGameSaveInfo info = WgsGameSave.Inspect(path);
                if (info.Kind == WgsGameSaveKind.Unknown)
                    continue;

                slots.Add(new WgsSaveSlot(
                    Path.GetFileNameWithoutExtension(path),
                    path,
                    path,
                    MetadataRevision: 0,
                    info));
            }
            catch
            {
                // Ignore unrelated or transient save files.
            }
        }

        return slots
            .OrderBy(slot => slot.Save.Kind == WgsGameSaveKind.Checkpoint ? 0 : 1)
            .ThenByDescending(slot => slot.Save.LastWriteTimeUtc)
            .ToList();
    }

    private static WgsBackupEntry InspectArchive(string path, string origin, bool imported)
    {
        byte[] data = ReadImportData(path);
        DateTime created = File.GetCreationTime(path);
        string reason = imported
            ? L.Get("game_saves.reason_imported_by_user")
            : L.Get("game_saves.reason_portable_slot_copy");
        string? containerId = null;

        using ZipArchive archive = ZipFile.OpenRead(path);
        if (archive.GetEntry("manifest.json") is { } entry)
        {
            using Stream stream = entry.Open();
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("exportedUtc", out JsonElement exported) &&
                exported.TryGetDateTime(out DateTime exportedUtc))
                created = exportedUtc.ToLocalTime();
            if (root.TryGetProperty("ContainerId", out JsonElement container))
                containerId = container.GetString();
        }

        WgsGameSaveInfo info = WgsGameSave.Inspect(path, data, created.ToUniversalTime());
        return new WgsBackupEntry(
            $"archive:{Path.GetFullPath(path)}",
            path,
            containerId,
            info,
            created,
            reason,
            origin,
            IsArchive: true);
    }

    private static BackupManifest ReadBackupManifest(string directory)
    {
        string path = Path.Combine(directory, "halomeister-backup.json");
        DateTime created = Directory.GetCreationTimeUtc(directory);
        string reason = Path.GetFileName(directory);

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("createdUtc", out JsonElement createdElement) &&
                createdElement.TryGetDateTime(out DateTime parsed))
                created = parsed;
            if (root.TryGetProperty("reason", out JsonElement reasonElement))
                reason = reasonElement.GetString() ?? reason;
        }
        catch
        {
            // Older snapshots without a readable manifest still remain restorable.
        }

        return new BackupManifest(created, reason);
    }

    private static byte[] ReadImportData(string path)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        ZipArchiveEntry entry = archive.GetEntry("Data")
            ?? throw new InvalidDataException(L.Get("game_saves.archive_missing_data"));
        using Stream stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void AddFile(ZipArchive archive, string sourcePath, string entryName)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
        using Stream input = File.OpenRead(sourcePath);
        using Stream output = entry.Open();
        input.CopyTo(output);
    }

    private static bool IsArchivePath(string path)
        => path.EndsWith(".halo-wgs", StringComparison.OrdinalIgnoreCase) ||
           path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static string UniquePath(string directory, string fileName)
    {
        string candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate)) return candidate;
        return Path.Combine(
            directory,
            $"{Path.GetFileNameWithoutExtension(fileName)}-{Guid.NewGuid():N}" +
            Path.GetExtension(fileName));
    }

    private static string SafeName(string value)
    {
        string result = string.Concat(value.Select(c =>
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')).Trim('-');
        return result.Length == 0 ? "save" : result;
    }

    private sealed record BackupManifest(DateTime CreatedUtc, string Reason);
}
