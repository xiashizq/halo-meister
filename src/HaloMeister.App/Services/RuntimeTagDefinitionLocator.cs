namespace HaloMeister.App.Services;

public static class RuntimeTagDefinitionLocator
{
    public const string EnvironmentVariable = "HALOMEISTER_DEFINITIONS";

    public static string BundledDirectory => Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "Definitions",
        "haloce_evolved");

    public static string ResolveCampaignEvolved(string? preferredDirectory = null)
    {
        string? configured = string.IsNullOrWhiteSpace(preferredDirectory)
            ? Environment.GetEnvironmentVariable(EnvironmentVariable)
            : preferredDirectory;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            string full = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(configured.Trim()));
            if (IsDefinitionDirectory(full))
                return full;

            throw new DirectoryNotFoundException(
                $"Campaign Evolved tag definitions were not found at {full}. " +
                $"Remove or correct {EnvironmentVariable} to use Halo Meister's bundled definitions.");
        }

        string bundled = Path.GetFullPath(BundledDirectory);
        if (IsDefinitionDirectory(bundled))
            return bundled;

        throw new DirectoryNotFoundException(
            "Halo Meister's bundled Campaign Evolved tag definitions are missing. " +
            "Extract the complete release ZIP and keep the Assets folder next to HaloMeister.exe.");
    }

    private static bool IsDefinitionDirectory(string path) =>
        Directory.Exists(path) &&
        File.Exists(Path.Combine(path, "_meta.json"));
}
