using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using HaloMeister.Core;

namespace HaloMeister.App.Services;

public sealed record WgsSaveSlot(
    string ContainerId,
    string DataPath,
    string MetadataPath,
    int MetadataRevision,
    WgsGameSaveInfo Save)
{
    public string DisplayName => Save.Kind == WgsGameSaveKind.Checkpoint
        ? Save.ScenarioTitle ?? Save.ScenarioCode ?? "Unknown mission"
        : Save.KindLabel;

    public string MissionCodeDisplay => Save.ScenarioCode?.ToUpperInvariant() ?? "SAVE";

    public string ThumbnailSource
    {
        get
        {
            string difficulty = Save.Difficulty?.ToLowerInvariant() switch
            {
                "easy" => "easy",
                "normal" => "normal",
                "heroic" => "heroic",
                "legendary" or "laso" => "legendary",
                _ => "unknown",
            };
            return $"ms-appx:///Assets/DifficultyIcons/{difficulty}.jpg";
        }
    }

    public string MetadataSummary
    {
        get
        {
            var parts = new List<string>
            {
                Save.KindLabel,
                Save.Difficulty ?? "Unknown difficulty",
            };
            if (!string.IsNullOrWhiteSpace(Save.InternalCheckpoint))
                parts.Add(Catalog.Humanize(Save.InternalCheckpoint));
            if (Save.ActiveSkulls.Count > 0)
                parts.Add($"{Save.ActiveSkulls.Count} skull{(Save.ActiveSkulls.Count == 1 ? "" : "s")}");
            return string.Join(" · ", parts);
        }
    }

    public string UpdatedDisplay => File.GetLastWriteTime(DataPath).ToString("yyyy-MM-dd HH:mm:ss");
    public string SizeDisplay => $"{Save.Size / 1024d:N1} KiB";
    public string HashDisplay => Save.Sha256[..16] + "…";
}

public sealed record WgsBackupEntry(
    string Id,
    string SourcePath,
    string? ContainerId,
    WgsGameSaveInfo Save,
    DateTime CreatedLocal,
    string Reason,
    string OriginLabel,
    bool IsArchive)
{
    public string DisplayName =>
        $"{Save.ScenarioTitle ?? Save.ScenarioCode ?? Save.KindLabel} · " +
        $"{Save.Difficulty ?? "Unknown difficulty"} · {CreatedLocal:yyyy-MM-dd HH:mm}";

    public string Detail =>
        $"{OriginLabel} · {Reason} · " +
        $"{Save.Size / 1024d:N1} KiB · {Save.Sha256[..12]}…";
}

public sealed record WgsBackupResult(string DirectoryPath, int FileCount);
public sealed record WgsReplaceResult(string BackupPath, WgsSaveSlot UpdatedSlot);

public sealed class WgsGameSaveStore
{
    public const string PackageFamilyName = "Microsoft.198377053870B_8wekyb3d8bbwe";
    public const string TitleId = "7C27BAE7";

    public WgsGameSaveStore()
    {
        PackageRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            PackageFamilyName);
        WgsRoot = Path.Combine(PackageRoot, "SystemAppData", "wgs");
        BackupRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HaloMeister",
            "GameSaveBackups");
    }

    public string PackageRoot { get; }
    public string WgsRoot { get; }
    public string BackupRoot { get; }

    private string ExportRoot => Path.Combine(BackupRoot, "Exports");
    private string ImportRoot => Path.Combine(BackupRoot, "Imports");

    public bool IsGameRunning => Process.GetProcessesByName("HaloCampaignEvolved").Length > 0;

    public IReadOnlyList<WgsSaveSlot> Discover()
        => DiscoverSlotsFromRoot(WgsRoot);

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
            foreach (WgsSaveSlot slot in DiscoverSlotsFromRoot(directory)
                         .Where(slot => slot.Save.Kind == WgsGameSaveKind.Checkpoint))
            {
                results.Add(new WgsBackupEntry(
                    $"snapshot:{directory}:{slot.ContainerId}",
                    slot.DataPath,
                    slot.ContainerId,
                    slot.Save,
                    manifest.CreatedUtc.ToLocalTime(),
                    manifest.Reason,
                    "Full WGS snapshot",
                    IsArchive: false));
            }
        }

        foreach (string library in new[] { ExportRoot, ImportRoot })
        {
            if (!Directory.Exists(library)) continue;
            string origin = library.Equals(ExportRoot, StringComparison.OrdinalIgnoreCase)
                ? "Exported archive"
                : "Imported archive";

            foreach (string archive in Directory.EnumerateFiles(library, "*", SearchOption.TopDirectoryOnly)
                         .Where(IsArchivePath))
            {
                try
                {
                    results.Add(InspectArchive(archive, origin));
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
        if (!Directory.Exists(WgsRoot))
            throw new DirectoryNotFoundException($"The WGS folder does not exist: {WgsRoot}");

        Directory.CreateDirectory(BackupRoot);
        string safeReason = SafeName(reason);
        string destination = Path.Combine(
            BackupRoot,
            $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{safeReason}");
        Directory.CreateDirectory(destination);

        int count = 0;
        foreach (string source in Directory.EnumerateFiles(WgsRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(WgsRoot, source);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: false);
            count++;
        }

        var manifest = new
        {
            format = 1,
            createdUtc = DateTime.UtcNow,
            reason,
            source = WgsRoot,
            packageFamily = PackageFamilyName,
            titleId = TitleId,
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
        return InspectArchive(destination, "Exported archive");
    }

    public WgsBackupEntry ImportToLibrary(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The selected archive no longer exists.", sourcePath);
        if (!IsArchivePath(sourcePath))
            throw new InvalidDataException("Select a .halo-wgs or .zip archive.");

        byte[] data = ReadImportData(sourcePath);
        WgsGameSaveInfo info = WgsGameSave.Inspect(sourcePath, data);
        if (info.Kind != WgsGameSaveKind.Checkpoint)
            throw new InvalidDataException(
                "The archive does not contain a Campaign Evolved HALOCEVO checkpoint.");

        Directory.CreateDirectory(ImportRoot);
        string baseName = SafeName(Path.GetFileNameWithoutExtension(sourcePath));
        string destination = UniquePath(
            ImportRoot,
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{baseName}.halo-wgs");
        File.Copy(sourcePath, destination, overwrite: false);
        return InspectArchive(destination, "Imported archive");
    }

    public WgsReplaceResult RestoreBackup(WgsSaveSlot target, WgsBackupEntry backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        byte[] replacement = backup.IsArchive
            ? ReadImportData(backup.SourcePath)
            : File.ReadAllBytes(backup.SourcePath);
        return ReplaceSlotData(target, replacement, "before-backup-restore");
    }

    public void ExportSlot(WgsSaveSlot slot, string destination)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        string? parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        using FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        AddFile(archive, slot.DataPath, "Data");
        AddFile(archive, slot.MetadataPath, "container");

        var manifest = new
        {
            format = 1,
            packageFamily = PackageFamilyName,
            titleId = TitleId,
            slot.ContainerId,
            slot.MetadataRevision,
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

    public WgsReplaceResult ReplaceSlotData(
        WgsSaveSlot target,
        ReadOnlySpan<byte> replacement,
        string backupReason = "before-save-replace")
    {
        if (IsGameRunning)
            throw new InvalidOperationException(
                "Close Halo: Campaign Evolved before restoring a game save.");
        if (target.Save.Kind != WgsGameSaveKind.Checkpoint)
            throw new InvalidOperationException(
                "Only checkpoint/resume slots can be restored from this page.");

        byte[] replacementBytes = replacement.ToArray();
        WgsGameSaveInfo imported = WgsGameSave.Inspect("backup restore", replacementBytes);
        if (imported.Kind != WgsGameSaveKind.Checkpoint)
            throw new InvalidDataException(
                "The selected backup is not a HALOCEVO checkpoint/resume save.");

        WgsBackupResult safetyBackup = BackupAll(backupReason);
        string targetDirectory = Path.GetDirectoryName(target.DataPath)
            ?? throw new InvalidOperationException("The target slot has no parent directory.");
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

    private static IReadOnlyList<WgsSaveSlot> DiscoverSlotsFromRoot(string root)
    {
        if (!Directory.Exists(root))
            return [];

        var slots = new List<WgsSaveSlot>();
        foreach (FileInfo metadata in new DirectoryInfo(root)
                     .EnumerateFiles("container.*", SearchOption.AllDirectories)
                     .OrderByDescending(file => ParseRevision(file.Name)))
        {
            DirectoryInfo? containerDirectory = metadata.Directory;
            if (containerDirectory is null) continue;
            if (slots.Any(slot =>
                    slot.DataPath.StartsWith(containerDirectory.FullName, StringComparison.OrdinalIgnoreCase)))
                continue;

            FileInfo? data = containerDirectory
                .EnumerateFiles()
                .Where(file =>
                    !file.Name.StartsWith("container.", StringComparison.OrdinalIgnoreCase) &&
                    !file.Name.StartsWith(".halomeister-", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            if (data is null) continue;

            try
            {
                WgsGameSaveInfo info = WgsGameSave.Inspect(data.FullName);
                slots.Add(new WgsSaveSlot(
                    containerDirectory.Name,
                    data.FullName,
                    metadata.FullName,
                    ParseRevision(metadata.Name),
                    info));
            }
            catch
            {
                // Ignore unrelated or transient WGS files.
            }
        }

        return slots
            .OrderBy(slot => slot.Save.Kind == WgsGameSaveKind.Checkpoint ? 0 : 1)
            .ThenByDescending(slot => slot.Save.LastWriteTimeUtc)
            .ToList();
    }

    private static WgsBackupEntry InspectArchive(string path, string origin)
    {
        byte[] data = ReadImportData(path);
        DateTime created = File.GetCreationTime(path);
        string reason = origin == "Imported archive" ? "Imported by user" : "Portable slot copy";
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
            ?? throw new InvalidDataException("The archive does not contain a Data entry.");
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

    private static int ParseRevision(string name)
        => int.TryParse(Path.GetExtension(name).TrimStart('.'), out int revision) ? revision : 0;

    private static string SafeName(string value)
    {
        string result = string.Concat(value.Select(c =>
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')).Trim('-');
        return result.Length == 0 ? "save" : result;
    }

    private sealed record BackupManifest(DateTime CreatedUtc, string Reason);
}
