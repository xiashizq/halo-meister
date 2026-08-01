namespace HaloMeister.App.Localization;

internal static class AppLanguageStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HaloMeister",
        "ui-language.txt");

    public static string? Load()
    {
        try
        {
            if (!File.Exists(StorePath))
                return null;

            string value = File.ReadAllText(StorePath).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string language)
    {
        try
        {
            string? directory = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(StorePath, language);
        }
        catch
        {
            // Preference persistence must never break startup.
        }
    }
}
