using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HaloMeister.App.Services;

public sealed record Ue4ssLoaderInstallResult(
    string BinaryDirectory,
    string LoaderDirectory,
    string BackupDirectory,
    string Version);

/// <summary>
/// Installs a pinned, verified upstream UE4SS build and the Campaign Evolved
/// signatures Halo Meister needs. The game must be closed during installation.
/// </summary>
public sealed class Ue4ssLoaderInstaller
{
    public const string Version = "v3.0.1-1012-gc838a8ac";
    public const string DownloadUrl =
        "https://github.com/UE4SS-RE/RE-UE4SS/releases/download/experimental/" +
        "UE4SS_v3.0.1-1012-gc838a8ac.zip";
    public const string ArchiveSha256 =
        "4CBB48E18D5D7D920DF7C742A871749B725116241B7B9E33788E191E891A73FF";

    private const long MaximumDownloadBytes = 32L * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly string _downloadRoot;
    private readonly string _backupRoot;
    private readonly string _signatureAssetRoot;
    private readonly Func<bool> _isGameRunning;

    public Ue4ssLoaderInstaller(
        string? dataRoot = null,
        string? signatureAssetRoot = null,
        Func<bool>? isGameRunning = null)
    {
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        string haloMeisterDataRoot = dataRoot ??
            Path.Combine(localAppData, "HaloMeister");
        _downloadRoot = Path.Combine(haloMeisterDataRoot, "Downloads");
        _backupRoot = Path.Combine(haloMeisterDataRoot, "UE4SSBackups");
        _signatureAssetRoot = signatureAssetRoot ?? Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "UE4SSLoader",
            "Signatures");
        _isGameRunning = isGameRunning ??
            (() => Process.GetProcessesByName("HaloCampaignEvolved").Length > 0);
    }

    public bool IsInstalled(string selectedPath)
    {
        string? binaryDirectory = ResolveBinaryDirectory(selectedPath);
        return binaryDirectory is not null &&
               File.Exists(Path.Combine(binaryDirectory, "dwmapi.dll")) &&
               File.Exists(Path.Combine(binaryDirectory, "ue4ss", "UE4SS.dll")) &&
               Directory.Exists(Path.Combine(binaryDirectory, "ue4ss", "Mods"));
    }

    public string? FindInstalledBinaryDirectory()
    {
        foreach (string root in CandidateGameRoots())
        {
            string? binaryDirectory = ResolveBinaryDirectory(root);
            if (binaryDirectory is not null && IsInstalled(binaryDirectory))
                return binaryDirectory;
        }

        return null;
    }

    public async Task<Ue4ssLoaderInstallResult> InstallAsync(
        string selectedPath,
        CancellationToken cancellationToken = default)
    {
        if (_isGameRunning())
            throw new InvalidOperationException(
                "Close Halo: Campaign Evolved before installing UE4SS.");

        string binaryDirectory = ResolveBinaryDirectory(selectedPath)
            ?? throw new DirectoryNotFoundException(
                "Could not find HaloCampaignEvolved.exe under the selected folder. " +
                "Select the Halo: Campaign Evolved installation folder.");

        if (!Directory.Exists(_signatureAssetRoot))
            throw new DirectoryNotFoundException(
                "The packaged Campaign Evolved UE4SS signatures are missing.");

        string archivePath = await GetVerifiedArchiveAsync(cancellationToken);
        string stagingRoot = Path.Combine(
            Path.GetTempPath(),
            $"HaloMeister-UE4SS-{Guid.NewGuid():N}");
        string backupDirectory = Path.Combine(
            _backupRoot,
            $"loader-{DateTime.Now:yyyyMMdd-HHmmss-fff}");

        try
        {
            Directory.CreateDirectory(stagingRoot);
            ExtractArchiveSafely(archivePath, stagingRoot);
            PrepareCampaignEvolvedPackage(stagingRoot);
            InstallStagedFiles(stagingRoot, binaryDirectory, backupDirectory);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }

        string loaderDirectory = Path.Combine(binaryDirectory, "ue4ss");
        if (!File.Exists(Path.Combine(binaryDirectory, "dwmapi.dll")) ||
            !File.Exists(Path.Combine(loaderDirectory, "UE4SS.dll")) ||
            !Directory.Exists(Path.Combine(loaderDirectory, "Mods")))
        {
            throw new InvalidDataException(
                "UE4SS installation did not produce the expected loader layout.");
        }

        return new Ue4ssLoaderInstallResult(
            binaryDirectory,
            loaderDirectory,
            backupDirectory,
            Version);
    }

    public static string? ResolveBinaryDirectory(string selectedPath)
    {
        try
        {
            string fullPath = Path.GetFullPath(selectedPath);
            var candidates = new List<string>
            {
                fullPath,
                Path.Combine(fullPath, "Content", "Meteorite", "Binaries", "WinGDK"),
                Path.Combine(fullPath, "Content", "Meteorite", "Binaries", "Win64"),
                Path.Combine(fullPath, "Meteorite", "Binaries", "WinGDK"),
                Path.Combine(fullPath, "Meteorite", "Binaries", "Win64"),
            };

            DirectoryInfo? selected = new(fullPath);
            if (selected.Name.Equals("ue4ss", StringComparison.OrdinalIgnoreCase) &&
                selected.Parent is not null)
            {
                candidates.Insert(0, selected.Parent.FullName);
            }
            else if (selected.Name.Equals("Mods", StringComparison.OrdinalIgnoreCase) &&
                     selected.Parent?.Parent is not null)
            {
                candidates.Insert(0, selected.Parent.Parent.FullName);
            }

            return candidates
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(candidate =>
                    File.Exists(Path.Combine(candidate, "HaloCampaignEvolved.exe")) ||
                    File.Exists(Path.Combine(candidate, "HaloSimulation_tag_release.dll")));
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> GetVerifiedArchiveAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_downloadRoot);
        string archivePath = Path.Combine(
            _downloadRoot,
            $"UE4SS_{Version}.zip");
        if (File.Exists(archivePath) &&
            await HasExpectedHashAsync(archivePath, cancellationToken))
        {
            return archivePath;
        }

        string temporaryPath = archivePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(
                DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is > MaximumDownloadBytes)
                throw new InvalidDataException("The UE4SS download is unexpectedly large.");

            await using Stream source =
                await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    int read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                        break;
                    total += read;
                    if (total > MaximumDownloadBytes)
                        throw new InvalidDataException(
                            "The UE4SS download exceeded the allowed size.");
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            if (!await HasExpectedHashAsync(temporaryPath, cancellationToken))
                throw new InvalidDataException(
                    "The downloaded UE4SS archive failed SHA-256 verification. " +
                    "Nothing was installed.");

            File.Move(temporaryPath, archivePath, overwrite: true);
            return archivePath;
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static async Task<bool> HasExpectedHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
            return Convert.ToHexString(hash).Equals(
                ArchiveSha256,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void PrepareCampaignEvolvedPackage(string stagingRoot)
    {
        string loaderRoot = Path.Combine(stagingRoot, "ue4ss");
        string settingsPath = Path.Combine(loaderRoot, "UE4SS-settings.ini");
        string modsTextPath = Path.Combine(loaderRoot, "Mods", "mods.txt");
        string modsJsonPath = Path.Combine(loaderRoot, "Mods", "mods.json");
        if (!File.Exists(Path.Combine(stagingRoot, "dwmapi.dll")) ||
            !File.Exists(Path.Combine(loaderRoot, "UE4SS.dll")) ||
            !File.Exists(settingsPath) ||
            !Directory.Exists(Path.Combine(loaderRoot, "Mods")))
        {
            throw new InvalidDataException(
                "The verified UE4SS archive has an unexpected layout.");
        }

        string settings = File.ReadAllText(settingsPath, Utf8);
        settings = SetIniValue(settings, "General", "EnableHotReloadSystem", "0");
        settings = SetIniValue(settings, "General", "bUseUObjectArrayCache", "false");
        settings = SetIniValue(settings, "EngineVersionOverride", "MajorVersion", "5");
        settings = SetIniValue(settings, "EngineVersionOverride", "MinorVersion", "5");
        File.WriteAllText(settingsPath, settings, Utf8);

        if (File.Exists(modsTextPath))
        {
            string[] lines = File.ReadAllLines(modsTextPath, Utf8);
            for (int i = 0; i < lines.Length; i++)
            {
                int separator = lines[i].IndexOf(':');
                if (separator <= 0 || lines[i].TrimStart().StartsWith(';'))
                    continue;
                lines[i] = $"{lines[i][..separator].Trim()} : 0";
            }
            File.WriteAllLines(modsTextPath, lines, Utf8);
        }

        if (File.Exists(modsJsonPath) &&
            JsonNode.Parse(File.ReadAllText(modsJsonPath, Utf8)) is JsonArray mods)
        {
            foreach (JsonNode? node in mods)
            {
                if (node is JsonObject item)
                    item["mod_enabled"] = false;
            }
            File.WriteAllText(
                modsJsonPath,
                mods.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                Utf8);
        }

        string stagedSignatures = Path.Combine(loaderRoot, "UE4SS_Signatures");
        CopyDirectory(_signatureAssetRoot, stagedSignatures);
    }

    private static void InstallStagedFiles(
        string stagingRoot,
        string binaryDirectory,
        string backupDirectory)
    {
        string destinationRoot = Path.GetFullPath(binaryDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var createdFiles = new List<string>();
        var overwrittenFiles = new List<(string Destination, string Backup)>();

        try
        {
            Directory.CreateDirectory(backupDirectory);
            foreach (string source in Directory.EnumerateFiles(
                         stagingRoot,
                         "*",
                         SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(stagingRoot, source);
                string destination = Path.GetFullPath(
                    Path.Combine(binaryDirectory, relative));
                if (!destination.StartsWith(
                        destinationRoot,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"UE4SS package path escapes the game folder: {relative}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    string backup = Path.Combine(backupDirectory, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(destination, backup, overwrite: false);
                    overwrittenFiles.Add((destination, backup));
                }
                else
                {
                    createdFiles.Add(destination);
                }

                string temporary = destination + $".{Guid.NewGuid():N}.tmp";
                try
                {
                    File.Copy(source, temporary, overwrite: false);
                    File.Move(temporary, destination, overwrite: true);
                }
                finally
                {
                    TryDeleteFile(temporary);
                }
            }
        }
        catch
        {
            foreach ((string destination, string backup) in
                     overwrittenFiles.AsEnumerable().Reverse())
            {
                try { File.Copy(backup, destination, overwrite: true); }
                catch { /* Best-effort rollback; the persistent backup remains. */ }
            }
            foreach (string created in createdFiles.AsEnumerable().Reverse())
                TryDeleteFile(created);
            throw;
        }
    }

    private static void ExtractArchiveSafely(string archivePath, string stagingRoot)
    {
        string fullStagingRoot = Path.GetFullPath(stagingRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string normalized = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            string destination = Path.GetFullPath(Path.Combine(stagingRoot, normalized));
            if (!destination.StartsWith(
                    fullStagingRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"UE4SS archive contains an unsafe path: {entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
    }

    private static string SetIniValue(
        string content,
        string section,
        string key,
        string value)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n').ToList();
        int sectionStart = lines.FindIndex(line =>
            line.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
        if (sectionStart < 0)
        {
            if (lines.Count > 0 && lines[^1].Length != 0)
                lines.Add(string.Empty);
            lines.Add($"[{section}]");
            lines.Add($"{key} = {value}");
            return string.Join(Environment.NewLine, lines);
        }

        int sectionEnd = lines.FindIndex(
            sectionStart + 1,
            line => line.TrimStart().StartsWith('['));
        if (sectionEnd < 0)
            sectionEnd = lines.Count;

        for (int i = sectionStart + 1; i < sectionEnd; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith(';'))
                continue;
            int equals = trimmed.IndexOf('=');
            if (equals < 0 ||
                !trimmed[..equals].Trim().Equals(
                    key,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            lines[i] = $"{key} = {value}";
            return string.Join(Environment.NewLine, lines);
        }

        lines.Insert(sectionEnd, $"{key} = {value}");
        return string.Join(Environment.NewLine, lines);
    }

    private static void CopyDirectory(string sourceRoot, string destinationRoot)
    {
        foreach (string source in Directory.EnumerateFiles(
                     sourceRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            string destination = Path.Combine(
                destinationRoot,
                Path.GetRelativePath(sourceRoot, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }
    }

    private static IEnumerable<string> CandidateGameRoots()
    {
        string? configured =
            Environment.GetEnvironmentVariable("HALO_CAMPAIGN_EVOLVED_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
            yield return configured;

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
                continue;
            string root = drive.RootDirectory.FullName;
            yield return Path.Combine(root, "Games", "Halo- Campaign Evolved");
            yield return Path.Combine(root, "Games", "Halo Campaign Evolved");
            yield return Path.Combine(root, "XboxGames", "Halo Campaign Evolved");
            yield return Path.Combine(root, "XboxGames", "Halo- Campaign Evolved");
            yield return Path.Combine(
                root,
                "Program Files (x86)",
                "Steam",
                "steamapps",
                "common",
                "Halo Campaign Evolved");
            yield return Path.Combine(
                root,
                "SteamLibrary",
                "steamapps",
                "common",
                "Halo Campaign Evolved");
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(3),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"HaloMeister/{ReleaseUpdateService.Current.CurrentVersion} " +
            "(+https://github.com/NicmeisteR/halo-meister)");
        return client;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A failed cleanup must not hide the original installation result.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A failed cleanup must not hide the original installation result.
        }
    }
}
