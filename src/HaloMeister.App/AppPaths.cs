namespace HaloMeister.App;

/// <summary>
/// Central place for the per-user data folder under %LOCALAPPDATA%.
/// The application was previously called "Halo Meister" and stored its data in
/// <c>%LOCALAPPDATA%\HaloMeister</c>. The first time <see cref="DataFolderName"/> is
/// touched, that legacy folder is moved to the new location so settings, backups
/// and downloads are preserved.
/// </summary>
public static class AppPaths
{
    public const string LegacyDataFolderName = "HaloMeister";

    /// <summary>Folder name used under %LOCALAPPDATA% and the temp directory.</summary>
    public static string DataFolderName { get; } = "CartographerToolkit";

    /// <summary>Full path of <c>%LOCALAPPDATA%\CartographerToolkit</c>.</summary>
    public static string DataRoot { get; }

    static AppPaths()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        DataRoot = Path.Combine(localAppData, DataFolderName);
        TryMigrateLegacyData(Path.Combine(localAppData, LegacyDataFolderName), DataRoot);
    }

    /// <summary>Forces the one-time legacy migration to run. Safe to call repeatedly.</summary>
    public static void EnsureInitialized()
    {
        _ = DataRoot;
    }

    private static void TryMigrateLegacyData(string legacyRoot, string newRoot)
    {
        try
        {
            if (!Directory.Exists(legacyRoot) || Directory.Exists(newRoot))
                return;

            try
            {
                Directory.Move(legacyRoot, newRoot);
                return;
            }
            catch (IOException)
            {
                // Fall back to a copy when files are locked or the move cannot complete.
            }
            catch (UnauthorizedAccessException)
            {
            }

            // A failed move may have left nothing behind; copy so the user keeps their data.
            CopyDirectory(legacyRoot, newRoot);
        }
        catch
        {
            // Migration is best-effort: a failure must never prevent the app from starting.
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            try
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
