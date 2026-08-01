using System.Text.Json;

namespace HaloMeister.App.Localization;

public sealed class LocalizationService
{
    public const string English = "en";
    public const string ChineseSimplified = "zh-Hans";
    public const string Japanese = "ja";
    public const string Korean = "ko";

    private static readonly string[] Supported =
    [
        English,
        ChineseSimplified,
        Japanese,
        Korean,
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, Dictionary<string, string>> _catalogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private Dictionary<string, string> _active = new(StringComparer.Ordinal);
    private Dictionary<string, string> _fallback = new(StringComparer.Ordinal);

    public static LocalizationService Current { get; } = new();

    public string Language { get; private set; } = English;

    public event EventHandler? LanguageChanged;

    public IReadOnlyList<(string Code, string NativeName)> Languages { get; } =
    [
        (English, "English"),
        (ChineseSimplified, "简体中文"),
        (Japanese, "日本語"),
        (Korean, "한국어"),
    ];

    private LocalizationService()
    {
        EnsureLoaded(English);
        _fallback = _catalogs[English];
        _active = _fallback;

        string preferred = AppLanguageStore.Load() ?? DetectSystemLanguage();
        SetLanguage(preferred, persist: false);
    }

    public void SetLanguage(string language, bool persist = true)
    {
        string normalized = Normalize(language);
        EnsureLoaded(normalized);
        EnsureLoaded(English);

        lock (_gate)
        {
            if (string.Equals(Language, normalized, StringComparison.OrdinalIgnoreCase)
                && _active.Count > 0)
            {
                return;
            }

            Language = normalized;
            _fallback = _catalogs[English];
            _active = _catalogs[normalized];
        }

        if (persist)
            AppLanguageStore.Save(normalized);

        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;

        lock (_gate)
        {
            if (_active.TryGetValue(key, out string? value) && !string.IsNullOrEmpty(value))
                return value;
            if (_fallback.TryGetValue(key, out value) && !string.IsNullOrEmpty(value))
                return value;
        }

        return key;
    }

    public string Format(string key, params object?[] args)
    {
        string template = Get(key);
        try
        {
            return args.Length == 0 ? template : string.Format(template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    public bool Has(string key)
    {
        lock (_gate)
        {
            return _active.ContainsKey(key) || _fallback.ContainsKey(key);
        }
    }

    private void EnsureLoaded(string language)
    {
        lock (_gate)
        {
            if (_catalogs.ContainsKey(language))
                return;

            _catalogs[language] = LoadCatalog(language);
        }
    }

    private static Dictionary<string, string> LoadCatalog(string language)
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "i18n",
            $"{language}.json");

        if (!File.Exists(path))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            string json = File.ReadAllText(path);
            Dictionary<string, string>? map = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
            return map is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(map, StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string Normalize(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return English;

        string value = language.Trim().Replace('_', '-');
        foreach (string supported in Supported)
        {
            if (value.Equals(supported, StringComparison.OrdinalIgnoreCase))
                return supported;
        }

        if (value.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return ChineseSimplified;
        if (value.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            return Japanese;
        if (value.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
            return Korean;

        return English;
    }

    private static string DetectSystemLanguage()
    {
        try
        {
            string? name = System.Globalization.CultureInfo.CurrentUICulture.Name;
            return Normalize(name ?? English);
        }
        catch
        {
            return English;
        }
    }
}
