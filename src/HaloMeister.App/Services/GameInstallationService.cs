using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace HaloMeister.App.Services;

/// <summary>
/// Finds and remembers the installed game while it is closed. This keeps file-based
/// tools independent from the live process connection used by runtime tools.
/// </summary>
public sealed class GameInstallationService
{
    /// <summary>
    /// Title folder names used by Microsoft Store / Xbox PC Game Pass installs.
    /// </summary>
    public static readonly string[] XboxTitleFolderNames =
    [
        "Halo Campaign Evolved",
        "Halo- Campaign Evolved",
    ];

    private readonly string _rememberedPath;
    private string? _binaryDirectory;

    private GameInstallationService()
    {
        _rememberedPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HaloMeister",
            "game-binary-directory.txt");
        Refresh();
    }

    public static GameInstallationService Current { get; } = new();

    public string? BinaryDirectory => _binaryDirectory;

    public void Refresh()
    {
        _binaryDirectory = CandidateRoots()
            .Select(Ue4ssLoaderInstaller.ResolveBinaryDirectory)
            .FirstOrDefault(directory => directory is not null);
        if (_binaryDirectory is not null)
            Persist(_binaryDirectory);
    }

    public void Remember(string selectedPath)
    {
        string binaryDirectory = Ue4ssLoaderInstaller.ResolveBinaryDirectory(selectedPath)
            ?? throw new DirectoryNotFoundException(
                "Could not find HaloCampaignEvolved.exe under the selected folder.");
        _binaryDirectory = binaryDirectory;
        Persist(binaryDirectory);
    }

    public string? TryGetPaksDirectory()
    {
        foreach (string root in CandidateRoots())
        {
            foreach (string candidate in PaksCandidates(root))
            {
                if (Directory.Exists(candidate) &&
                    Directory.EnumerateFiles(candidate, "*.utoc").Any())
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Installation roots worth probing for binaries / Paks / UE4SS. Shared by
    /// bridge discovery so Store WinGDK and Steam Win64 stay aligned.
    /// </summary>
    public IEnumerable<string> EnumerateCandidateRoots() => CandidateRoots();

    private IEnumerable<string> CandidateRoots()
    {
        string? configured = Environment.GetEnvironmentVariable("HALO_CAMPAIGN_EVOLVED_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
            yield return configured;

        if (!string.IsNullOrWhiteSpace(_binaryDirectory))
            yield return _binaryDirectory;

        if (File.Exists(_rememberedPath))
        {
            string remembered = TryRead(_rememberedPath);
            if (!string.IsNullOrWhiteSpace(remembered))
                yield return remembered;
        }

        foreach (string location in EnumerateUninstallLocations())
            yield return location;

        // Microsoft Store / Xbox PC libraries from .GamingRoot (custom install drives).
        foreach (string titleRoot in EnumerateXboxTitleRoots())
            yield return titleRoot;

        foreach (string library in EnumerateSteamLibraries())
            yield return Path.Combine(library, "steamapps", "common", "Halo Campaign Evolved");

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
                root, "Program Files (x86)", "Steam", "steamapps", "common", "Halo Campaign Evolved");
            yield return Path.Combine(
                root, "SteamLibrary", "steamapps", "common", "Halo Campaign Evolved");
            yield return Path.Combine(
                root, "PG", "Steam", "steamapps", "common", "Halo Campaign Evolved");
        }
    }

    /// <summary>
    /// Reads each drive's <c>.GamingRoot</c> marker and yields Halo title folders under
    /// the Xbox library path. Default layout is <c>{Drive}\XboxGames\Halo Campaign Evolved</c>;
    /// users can relocate the library folder in the Xbox app.
    /// </summary>
    public static IEnumerable<string> EnumerateXboxTitleRoots()
    {
        foreach (string library in EnumerateXboxLibraryFolders())
        {
            foreach (string title in XboxTitleFolderNames)
                yield return Path.Combine(library, title);
        }
    }

    private static IEnumerable<string> EnumerateXboxLibraryFolders()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
                continue;

            string driveRoot = drive.RootDirectory.FullName;
            string? gamingRootPath = Path.Combine(driveRoot, ".GamingRoot");
            bool foundFromMarker = false;
            foreach (string library in ReadGamingRootLibraries(gamingRootPath))
            {
                foundFromMarker = true;
                if (seen.Add(library))
                    yield return library;
            }

            // Drive is Xbox-eligible even when the marker has no Folder path — default library.
            if (foundFromMarker || File.Exists(gamingRootPath))
            {
                string defaultLibrary = Path.Combine(driveRoot, "XboxGames");
                if (seen.Add(defaultLibrary))
                    yield return defaultLibrary;
            }
        }
    }

    private static IEnumerable<string> ReadGamingRootLibraries(string gamingRootPath)
    {
        if (!File.Exists(gamingRootPath))
            return [];

        string text;
        try
        {
            // Xbox writes .GamingRoot as UTF-16 LE; fall back to UTF-8 for hand-edited files.
            byte[] bytes = File.ReadAllBytes(gamingRootPath);
            text = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE
                ? Encoding.Unicode.GetString(bytes)
                : Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return [];
        }

        var libraries = new List<string>();
        // Preferred: <Folder path="D:\XboxGames" />
        try
        {
            XDocument document = XDocument.Parse(text);
            foreach (XElement folder in document.Descendants().Where(element =>
                         element.Name.LocalName.Equals("Folder", StringComparison.OrdinalIgnoreCase)))
            {
                string? path = (string?)folder.Attribute("path") ?? (string?)folder.Attribute("Path");
                if (!string.IsNullOrWhiteSpace(path))
                    libraries.Add(path.Trim());
            }
        }
        catch
        {
            // Not well-formed XML — try a loose path attribute match.
            foreach (Match match in Regex.Matches(
                         text,
                         "path\\s*=\\s*\"(?<path>[^\"]+)\"",
                         RegexOptions.IgnoreCase))
            {
                string path = match.Groups["path"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(path))
                    libraries.Add(path);
            }
        }

        return libraries;
    }

    private static IEnumerable<string> PaksCandidates(string root)
    {
        string? current;
        try { current = Path.GetFullPath(root); }
        catch { yield break; }

        // From WinGDK\...\Scripts\main.lua up to Content needs ~8 hops; keep margin.
        for (int depth = 0; depth < 10 && current is not null; depth++)
        {
            yield return Path.Combine(current, "Content", "Meteorite", "Content", "Paks");
            yield return Path.Combine(current, "Meteorite", "Content", "Paks");
            yield return Path.Combine(current, "Content", "Paks");
            current = Path.GetDirectoryName(current);
        }
    }

    private static IEnumerable<string> EnumerateSteamLibraries()
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
                continue;
            string[] manifests =
            [
                Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)", "Steam", "steamapps", "libraryfolders.vdf"),
                Path.Combine(drive.RootDirectory.FullName, "Steam", "steamapps", "libraryfolders.vdf"),
                Path.Combine(drive.RootDirectory.FullName, "PG", "Steam", "steamapps", "libraryfolders.vdf"),
            ];
            foreach (string manifest in manifests)
            {
                string content = TryRead(manifest);
                foreach (Match match in Regex.Matches(
                             content,
                             "\"path\"\\s+\"(?<path>[^\"]+)\"",
                             RegexOptions.IgnoreCase))
                {
                    yield return match.Groups["path"].Value.Replace(@"\\", @"\");
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateUninstallLocations()
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (RegistryKey hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (string path in new[] { key, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                using RegistryKey? parent = hive.OpenSubKey(path);
                if (parent is null)
                    continue;
                foreach (string name in parent.GetSubKeyNames())
                {
                    using RegistryKey? entry = parent.OpenSubKey(name);
                    string? displayName = entry?.GetValue("DisplayName") as string;
                    string? installLocation = entry?.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(displayName) &&
                        displayName.Contains("Campaign Evolved", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(installLocation))
                    {
                        yield return installLocation;
                    }
                }
            }
        }
    }

    private void Persist(string binaryDirectory)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_rememberedPath)!);
            File.WriteAllText(_rememberedPath, binaryDirectory);
        }
        catch
        {
            // Discovery remains usable even when a path cannot be remembered.
        }
    }

    private static string TryRead(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch { return string.Empty; }
    }
}
