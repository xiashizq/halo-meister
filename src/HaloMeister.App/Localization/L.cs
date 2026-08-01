namespace HaloMeister.App.Localization;

/// <summary>
/// Short accessor for UI strings from the JSON translation dictionaries.
/// </summary>
public static class L
{
    public static string Get(string key) => LocalizationService.Current.Get(key);

    public static string Format(string key, params object?[] args)
        => LocalizationService.Current.Format(key, args);

    public static string T(string key) => Get(key);
}
