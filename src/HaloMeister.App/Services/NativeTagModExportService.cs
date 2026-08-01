using System.Diagnostics;
using System.Text;
using HaloMeister.App.Models;

namespace HaloMeister.App.Services;

public sealed class NativeTagModExportService
{
    private readonly RuntimeTagModService _tagMods = new();

    public async Task<NativeTagModExportResult> ExportAsync(
        RuntimeTagModDocument document,
        string requestedUtoc,
        string? definitionsDirectory = null)
    {
        string exporter = ResolveExporter();
        string paks = ResolvePaksDirectory();
        string definitions =
            RuntimeTagDefinitionLocator.ResolveCampaignEvolved(definitionsDirectory);

        string output = EnsurePrioritySuffix(requestedUtoc);
        string sidecar = Path.ChangeExtension(output, ".hmtagmod");
        string temporary = Path.Combine(
            Path.GetTempPath(), $"halomeister-{Guid.NewGuid():N}.hmtagmod");
        try
        {
            _tagMods.Save(document, temporary);
            var start = new ProcessStartInfo
            {
                FileName = exporter,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            start.ArgumentList.Add("--paks");
            start.ArgumentList.Add(paks);
            start.ArgumentList.Add("--definitions");
            start.ArgumentList.Add(definitions);
            start.ArgumentList.Add("--mod");
            start.ArgumentList.Add(temporary);
            start.ArgumentList.Add("--output");
            start.ArgumentList.Add(output);

            using Process process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start the native tag exporter.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(timeout.Token);
            string outputText = (await stdout).Trim();
            string errorText = (await stderr).Trim();
            if (process.ExitCode != 0)
                throw new InvalidDataException(
                    string.IsNullOrWhiteSpace(errorText)
                        ? $"Native exporter exited with code {process.ExitCode}."
                        : errorText);

            string ucas = Path.ChangeExtension(output, ".ucas");
            string pak = Path.ChangeExtension(output, ".pak");
            if (!File.Exists(output) || !File.Exists(ucas) || !File.Exists(pak))
                throw new IOException(
                    "The native exporter did not produce the complete .utoc/.ucas/.pak triplet.");
            _tagMods.Save(document, sidecar);
            return new NativeTagModExportResult(
                output, ucas, pak, sidecar, outputText);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch { }
        }
    }

    public NativeTagModInstallResult InstallOverlay(string sourceUtoc)
    {
        if (Process.GetProcessesByName("HaloCampaignEvolved").Any(process =>
            !process.HasExited))
            throw new InvalidOperationException(
                "Close Halo: Campaign Evolved before installing an overlay mod.");

        string source = Path.GetFullPath(sourceUtoc);
        string stem = Path.GetFileNameWithoutExtension(source);
        if (!stem.EndsWith("_P", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The overlay filename must end in _P so it mounts above the base game.");

        string[] sources =
        [
            source,
            Path.ChangeExtension(source, ".ucas"),
            Path.ChangeExtension(source, ".pak"),
        ];
        foreach (string file in sources)
            if (!File.Exists(file))
                throw new FileNotFoundException(
                    $"The overlay triplet is incomplete; {Path.GetFileName(file)} is missing.",
                    file);

        string paks = ResolvePaksDirectory();
        string[] destinations = sources
            .Select(file => Path.Combine(paks, Path.GetFileName(file)))
            .ToArray();
        foreach (string destination in destinations)
            if (File.Exists(destination))
                throw new IOException(
                    $"{Path.GetFileName(destination)} is already installed. " +
                    "Remove or rename the existing overlay first.");

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
        return new NativeTagModInstallResult(stem, paks, destinations);
    }

    public static string EnsurePrioritySuffix(string path)
    {
        string full = Path.GetFullPath(path);
        string stem = Path.GetFileNameWithoutExtension(full);
        if (!stem.EndsWith("_P", StringComparison.OrdinalIgnoreCase))
            stem += "_P";
        return Path.Combine(
            Path.GetDirectoryName(full)!, stem + ".utoc");
    }

    private static string ResolveExporter()
    {
        string[] candidates =
        [
            Path.Combine(
                AppContext.BaseDirectory, "Assets", "Native",
                "halomeister-tagmod-exporter.exe"),
            Path.Combine(
                AppContext.BaseDirectory, "halomeister-tagmod-exporter.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "The bundled native tag exporter is missing. Rebuild or reinstall Halo Meister.");
    }

    private static string ResolvePaksDirectory()
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
            "Halo: Campaign Evolved's Meteorite/Content/Paks directory was not found. " +
            "Set HALO_CAMPAIGN_EVOLVED_ROOT to the game installation folder.");
    }

    private static IEnumerable<string> CandidateGameRoots()
    {
        string? configured =
            Environment.GetEnvironmentVariable("HALO_CAMPAIGN_EVOLVED_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured;
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            yield return Path.Combine(
                drive.RootDirectory.FullName, "Games", "Halo- Campaign Evolved");
            yield return Path.Combine(
                drive.RootDirectory.FullName, "XboxGames", "Halo Campaign Evolved");
            yield return Path.Combine(
                drive.RootDirectory.FullName, "XboxGames", "Halo- Campaign Evolved");
        }
    }
}

public sealed record NativeTagModExportResult(
    string UtocPath,
    string UcasPath,
    string PakPath,
    string SidecarPath,
    string ExporterMessage);

public sealed record NativeTagModInstallResult(
    string Name,
    string PaksDirectory,
    IReadOnlyList<string> InstalledFiles);
