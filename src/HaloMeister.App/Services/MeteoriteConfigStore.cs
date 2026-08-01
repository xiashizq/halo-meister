using System.Text;

namespace HaloMeister.App.Services;

public sealed class MeteoriteConfigStore
{
    private static readonly HashSet<string> EditableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ini",
        ".cfg",
    };

    public MeteoriteConfigStore(string? savedRoot = null, string? backupRoot = null)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        SavedRoot = savedRoot ?? Path.Combine(localAppData, "Meteorite", "Saved");
        BackupRoot = backupRoot ?? Path.Combine(localAppData, "HaloMeister", "ConfigBackups");
    }

    public string SavedRoot { get; }
    public string ConfigRoot => Path.Combine(SavedRoot, "Config");
    public string ImGuiRoot => Path.Combine(SavedRoot, "ImGui");
    public string BackupRoot { get; }

    public IReadOnlyList<ConfigDocument> LoadDocuments()
    {
        return EnumerateSourceFiles()
            .Select(LoadDocument)
            .OrderBy(document => document.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ConfigBackup> SaveAsync(IEnumerable<ConfigDocument> documents)
    {
        List<ConfigDocument> changed = documents.Where(document => document.IsDirty).ToList();
        if (changed.Count == 0)
            throw new InvalidOperationException("There are no config changes to save.");

        foreach (ConfigDocument document in changed)
        {
            if (!File.Exists(document.FullPath))
                throw new IOException($"{document.RelativePath} no longer exists. Reload the config files and try again.");

            DateTime currentWriteTime = File.GetLastWriteTimeUtc(document.FullPath);
            if (currentWriteTime != document.LoadedWriteTimeUtc)
                throw new IOException($"{document.RelativePath} changed on disk after it was loaded. Reload it before saving so the game's changes are not overwritten.");
        }

        ConfigBackup backup = CreateBackup("before-save");

        foreach (ConfigDocument document in changed)
        {
            string temporaryPath = document.FullPath + $".halomeister-{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, document.Text, document.Encoding);
                File.Move(temporaryPath, document.FullPath, overwrite: true);
                document.MarkSaved(File.GetLastWriteTimeUtc(document.FullPath));
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        return backup;
    }

    public ConfigBackup CreateBackup(string reason = "manual")
    {
        Directory.CreateDirectory(BackupRoot);

        string directoryName = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{SanitizeReason(reason)}";
        string destinationRoot = Path.Combine(BackupRoot, directoryName);
        Directory.CreateDirectory(destinationRoot);

        int fileCount = 0;
        foreach (string sourcePath in EnumerateSourceFiles())
        {
            string relativePath = Path.GetRelativePath(SavedRoot, sourcePath);
            string destinationPath = Path.Combine(destinationRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: false);
            fileCount++;
        }

        File.WriteAllText(
            Path.Combine(destinationRoot, "_backup.txt"),
            $"Halo Meister Meteorite config backup{Environment.NewLine}" +
            $"Created: {DateTimeOffset.Now:O}{Environment.NewLine}" +
            $"Reason: {reason}{Environment.NewLine}" +
            $"Source: {SavedRoot}{Environment.NewLine}" +
            $"Files: {fileCount}{Environment.NewLine}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return new ConfigBackup(destinationRoot, Directory.GetCreationTime(destinationRoot), fileCount);
    }

    public IReadOnlyList<ConfigBackup> GetBackups()
    {
        if (!Directory.Exists(BackupRoot))
            return [];

        return Directory.EnumerateDirectories(BackupRoot)
            .Select(path => new ConfigBackup(
                path,
                Directory.GetCreationTime(path),
                EnumerateEditableFiles(path, excludeCrashReports: false).Count()))
            .Where(backup => backup.FileCount > 0)
            .OrderByDescending(backup => backup.Created)
            .ToList();
    }

    public int Restore(ConfigBackup backup)
    {
        string resolvedBackupRoot = Path.GetFullPath(backup.Path);
        string resolvedStoreRoot = Path.GetFullPath(BackupRoot) + Path.DirectorySeparatorChar;
        if (!resolvedBackupRoot.StartsWith(resolvedStoreRoot, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(resolvedBackupRoot))
            throw new InvalidOperationException("The selected backup is not available.");

        // Preserve the current on-disk state so a restore can itself be undone.
        CreateBackup("before-restore");

        int restored = 0;
        foreach (string sourcePath in EnumerateEditableFiles(resolvedBackupRoot, excludeCrashReports: false))
        {
            string relativePath = Path.GetRelativePath(resolvedBackupRoot, sourcePath);
            string destinationPath = Path.GetFullPath(Path.Combine(SavedRoot, relativePath));
            string resolvedSavedRoot = Path.GetFullPath(SavedRoot) + Path.DirectorySeparatorChar;
            if (!destinationPath.StartsWith(resolvedSavedRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Backup entry escapes the Meteorite Saved directory: {relativePath}");

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
            restored++;
        }

        return restored;
    }

    private ConfigDocument LoadDocument(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Encoding encoding = DetectEncoding(bytes, out int preambleLength);
        string text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        string relativePath = Path.GetRelativePath(SavedRoot, path);
        return new ConfigDocument(relativePath, path, text, encoding, File.GetLastWriteTimeUtc(path));
    }

    private IEnumerable<string> EnumerateSourceFiles()
        => EnumerateEditableFiles(ConfigRoot)
            .Concat(EnumerateEditableFiles(ImGuiRoot, excludeCrashReports: false));

    private static IEnumerable<string> EnumerateEditableFiles(string root, bool excludeCrashReports = true)
    {
        if (!Directory.Exists(root))
            return [];

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => EditableExtensions.Contains(Path.GetExtension(path)))
            .Where(path => !path.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            .Where(path => !excludeCrashReports || !IsCrashReportFile(root, path));
    }

    private static bool IsCrashReportFile(string root, string path)
    {
        string relativePath = Path.GetRelativePath(root, path);
        string? firstSegment = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.Equals(firstSegment, "CrashReportClient", StringComparison.OrdinalIgnoreCase);
    }

    private static Encoding DetectEncoding(byte[] bytes, out int preambleLength)
    {
        foreach (Encoding encoding in new Encoding[]
                 {
                     new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                     Encoding.Unicode,
                     Encoding.BigEndianUnicode,
                     Encoding.UTF32,
                 })
        {
            byte[] preamble = encoding.GetPreamble();
            if (preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble))
            {
                preambleLength = preamble.Length;
                return encoding;
            }
        }

        preambleLength = 0;
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    private static string SanitizeReason(string reason)
    {
        string value = string.Concat(reason.Select(character =>
            char.IsLetterOrDigit(character) || character == '-' ? character : '-'));
        return string.IsNullOrWhiteSpace(value) ? "backup" : value;
    }
}

public sealed class ConfigDocument
{
    private string _text;
    private string _savedText;

    public ConfigDocument(
        string relativePath,
        string fullPath,
        string text,
        Encoding encoding,
        DateTime loadedWriteTimeUtc)
    {
        RelativePath = relativePath;
        FullPath = fullPath;
        _text = text;
        _savedText = text;
        Encoding = encoding;
        LoadedWriteTimeUtc = loadedWriteTimeUtc;
    }

    public string RelativePath { get; }
    public string FullPath { get; }
    public string TabTitle => Path.GetFileName(RelativePath);
    public Encoding Encoding { get; }
    public DateTime LoadedWriteTimeUtc { get; private set; }
    public bool IsDirty => !string.Equals(_text, _savedText, StringComparison.Ordinal);

    public string Text
    {
        get => _text;
        set => _text = value;
    }

    public void MarkSaved(DateTime writeTimeUtc)
    {
        _savedText = _text;
        LoadedWriteTimeUtc = writeTimeUtc;
    }
}

public sealed record ConfigBackup(string Path, DateTime Created, int FileCount)
{
    public string DisplayName => $"{Created:g}  •  {FileCount} file{(FileCount == 1 ? string.Empty : "s")}  •  {System.IO.Path.GetFileName(Path)}";
}
