using System.Diagnostics;
using HaloMeister.App.Localization;

namespace HaloMeister.App.Services;

public sealed class FullPalettesOverlayService
{
    public const string OverlayStem = "MMYJ_FULL_VEHI_WAP_P";

    // Previous shipping name; cleaned up on install/remove so old copies
    // do not keep mounting after a rename.
    private const string LegacyOverlayStem = "HM_FullPalettes_P";

    private static readonly string[] Extensions = [".utoc", ".ucas", ".pak"];

    public bool IsGameRunning =>
        Process.GetProcessesByName("HaloCampaignEvolved").Any(process => !process.HasExited);

    public bool IsInstalled()
    {
        string paks = ResolvePaksDirectory();
        return HasCompleteTriplet(paks, OverlayStem) ||
               HasCompleteTriplet(paks, LegacyOverlayStem);
    }

    private static bool HasCompleteTriplet(string paks, string stem) =>
        Extensions.All(extension =>
            File.Exists(Path.Combine(paks, stem + extension)));

    public FullPalettesOverlayResult Install()
    {
        EnsureGameClosed();
        string paks = ResolvePaksDirectory();
        RemoveStem(paks, LegacyOverlayStem);
        string[] sources = ResolveBundledSources();
        string[] destinations = sources
            .Select(source => Path.Combine(paks, Path.GetFileName(source)))
            .ToArray();

        foreach (string destination in destinations)
            if (File.Exists(destination))
                throw new IOException(
                    L.Format(
                        "vehicle_workshop.full_palettes_already_installed",
                        Path.GetFileName(destination)));

        var copied = new List<string>();
        try
        {
            for (int index = 0; index < sources.Length; index++)
            {
                string temporary = destinations[index] + $".{Guid.NewGuid():N}.tmp";
                File.Copy(sources[index], temporary, false);
                File.Move(temporary, destinations[index], false);
                copied.Add(destinations[index]);
            }
        }
        catch
        {
            foreach (string file in copied)
            {
                try { File.Delete(file); }
                catch { }
            }
            throw;
        }

        return new FullPalettesOverlayResult(
            Installed: true,
            PaksDirectory: paks,
            Files: destinations,
            Message: L.Get("vehicle_workshop.full_palettes_installed_restart"));
    }

    public FullPalettesOverlayResult Remove()
    {
        EnsureGameClosed();
        string paks = ResolvePaksDirectory();
        var removed = new List<string>();
        removed.AddRange(RemoveStem(paks, OverlayStem));
        removed.AddRange(RemoveStem(paks, LegacyOverlayStem));

        if (removed.Count == 0)
            throw new FileNotFoundException(
                L.Get("vehicle_workshop.full_palettes_not_installed"));

        return new FullPalettesOverlayResult(
            Installed: false,
            PaksDirectory: paks,
            Files: removed,
            Message: L.Get("vehicle_workshop.full_palettes_removed_restart"));
    }

    private void EnsureGameClosed()
    {
        if (IsGameRunning)
            throw new InvalidOperationException(
                L.Get("vehicle_workshop.full_palettes_close_game"));
    }

    private static List<string> RemoveStem(string paks, string stem)
    {
        var removed = new List<string>();
        foreach (string extension in Extensions)
        {
            string path = Path.Combine(paks, stem + extension);
            if (!File.Exists(path)) continue;
            File.Delete(path);
            removed.Add(path);
        }
        return removed;
    }

    private static string[] ResolveBundledSources()
    {
        string[] roots =
        [
            Path.Combine(AppContext.BaseDirectory, "Assets", "Overlays"),
            Path.Combine(AppContext.BaseDirectory, "Overlays"),
        ];
        foreach (string root in roots)
        {
            string[] candidates = Extensions
                .Select(extension => Path.Combine(root, OverlayStem + extension))
                .ToArray();
            if (candidates.All(File.Exists))
                return candidates;
        }

        throw new FileNotFoundException(
            L.Get("vehicle_workshop.full_palettes_bundle_missing"));
    }

    public static string ResolvePaksDirectory()
    {
        foreach (string root in CandidateGameRoots())
        {
            string full;
            try { full = Path.GetFullPath(root); }
            catch { continue; }

            string[] candidates =
            [
                Path.Combine(full, "Content", "Meteorite", "Content", "Paks"),
                Path.Combine(full, "Meteorite", "Content", "Paks"),
                Path.Combine(full, "Content", "Paks"),
                full,
            ];
            foreach (string candidate in candidates)
                if (Directory.Exists(candidate) &&
                    Directory.EnumerateFiles(candidate, "*.utoc").Any())
                    return candidate;
        }

        throw new DirectoryNotFoundException(
            L.Get("vehicle_workshop.error_paks_not_found"));
    }

    private static IEnumerable<string> CandidateGameRoots()
    {
        string? configured =
            Environment.GetEnvironmentVariable("HALO_CAMPAIGN_EVOLVED_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
            yield return configured;

        string? remembered = TryRememberedGameRoot();
        if (!string.IsNullOrWhiteSpace(remembered))
            yield return remembered!;

        foreach (string root in EnumerateUninstallLocations())
            yield return root;

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            string letter = drive.RootDirectory.FullName;
            yield return Path.Combine(letter, "Games", "Halo- Campaign Evolved");
            yield return Path.Combine(letter, "Games", "Halo Campaign Evolved");
            yield return Path.Combine(letter, "XboxGames", "Halo Campaign Evolved");
            yield return Path.Combine(letter, "XboxGames", "Halo- Campaign Evolved");
            yield return Path.Combine(
                letter, "Program Files (x86)", "Steam", "steamapps", "common",
                "Halo Campaign Evolved");
            yield return Path.Combine(
                letter, "SteamLibrary", "steamapps", "common",
                "Halo Campaign Evolved");
            yield return Path.Combine(
                letter, "PG", "Steam", "steamapps", "common",
                "Halo Campaign Evolved");
        }
    }

    private static string? TryRememberedGameRoot()
    {
        try
        {
            string? mainPath = ScriptingBridgeService.Current.FindInstalledMainPath();
            if (string.IsNullOrWhiteSpace(mainPath)) return null;
            string? directory = Path.GetDirectoryName(mainPath);
            // ...\Meteorite\Binaries\WinGDK\HaloCampaignEvolved.exe → game root
            for (int i = 0; i < 4 && directory is not null; i++)
            {
                if (Directory.Exists(Path.Combine(directory, "Meteorite", "Content", "Paks")) ||
                    Directory.Exists(Path.Combine(directory, "Content", "Meteorite", "Content", "Paks")))
                    return directory;
                directory = Path.GetDirectoryName(directory);
            }
            return Path.GetDirectoryName(mainPath);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateUninstallLocations()
    {
        foreach (string location in EnumerateUninstallHive(
                     Microsoft.Win32.Registry.LocalMachine,
                     @"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
            yield return location;
        foreach (string location in EnumerateUninstallHive(
                     Microsoft.Win32.Registry.LocalMachine,
                     @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"))
            yield return location;
        foreach (string location in EnumerateUninstallHive(
                     Microsoft.Win32.Registry.CurrentUser,
                     @"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
            yield return location;
    }

    private static IEnumerable<string> EnumerateUninstallHive(
        Microsoft.Win32.RegistryKey root,
        string path)
    {
        using Microsoft.Win32.RegistryKey? hive = root.OpenSubKey(path);
        if (hive is null) yield break;
        foreach (string name in hive.GetSubKeyNames())
        {
            using Microsoft.Win32.RegistryKey? sub = hive.OpenSubKey(name);
            string? display = sub?.GetValue("DisplayName") as string;
            string? location = sub?.GetValue("InstallLocation") as string;
            if (string.IsNullOrWhiteSpace(display) ||
                string.IsNullOrWhiteSpace(location))
                continue;
            if (display.Contains("Campaign Evolved", StringComparison.OrdinalIgnoreCase))
                yield return location;
        }
    }
}

public sealed record FullPalettesOverlayResult(
    bool Installed,
    string PaksDirectory,
    IReadOnlyList<string> Files,
    string Message);
