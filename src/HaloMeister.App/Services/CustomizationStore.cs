using System.Text.RegularExpressions;
using System.Diagnostics;
using HaloMeister.App.Models;

namespace HaloMeister.App.Services;

public sealed record CustomizationProfile(
    string Id,
    string ConfigPath,
    DateTime LastWriteTime)
{
    public string DisplayName => Id.Equals("invalid_id", StringComparison.OrdinalIgnoreCase)
        ? $"Offline / fallback  •  {LastWriteTime:g}"
        : $"{Id}  •  {LastWriteTime:g}";
}

public sealed partial class CustomizationStore
{
    private const string SettingKey = "ObjectCustomizationNames";
    private readonly MeteoriteConfigStore _configStore;
    private readonly Func<bool> _isGameRunning;
    private List<ConfigDocument> _documents = [];
    private ConfigDocument? _document;
    private IniSetting? _setting;

    public CustomizationStore(
        MeteoriteConfigStore? configStore = null,
        Func<bool>? isGameRunning = null)
    {
        _configStore = configStore ?? new MeteoriteConfigStore();
        _isGameRunning = isGameRunning ??
            (() => Process.GetProcessesByName("HaloCampaignEvolved").Length > 0);
    }

    public string? ConfigPath => _document?.FullPath;
    public bool IsGameRunning => _isGameRunning();

    public IReadOnlyList<CustomizationProfile> GetProfiles()
    {
        return _configStore.LoadDocuments()
            .Where(document => Path.GetFileName(document.FullPath).Equals(
                "HaloGlobalGameUserSettings.ini",
                StringComparison.OrdinalIgnoreCase))
            .Select(document => new CustomizationProfile(
                Path.GetFileName(Path.GetDirectoryName(document.FullPath)) ?? "Unknown profile",
                document.FullPath,
                document.LoadedWriteTimeUtc.ToLocalTime()))
            .OrderBy(profile => profile.Id.Equals("invalid_id", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(profile => profile.LastWriteTime)
            .ToList();
    }

    public IReadOnlyList<string> Load(string? configPath = null)
    {
        _documents = _configStore.LoadDocuments().ToList();
        IEnumerable<ConfigDocument> candidates = _documents.Where(document =>
            Path.GetFileName(document.FullPath).Equals(
                "HaloGlobalGameUserSettings.ini",
                StringComparison.OrdinalIgnoreCase));
        _document = configPath is null
            ? candidates
                .OrderBy(document => (Path.GetFileName(Path.GetDirectoryName(document.FullPath)) ?? string.Empty)
                    .Equals("invalid_id", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(document => document.LoadedWriteTimeUtc)
                .FirstOrDefault()
            : candidates.FirstOrDefault(document =>
                Path.GetFullPath(document.FullPath).Equals(
                    Path.GetFullPath(configPath),
                    StringComparison.OrdinalIgnoreCase));

        if (_document is null)
            throw new FileNotFoundException(
                "HaloGlobalGameUserSettings.ini was not found. Run Campaign Evolved once, then reload this page.");

        IniDocumentModel ini = IniDocumentModel.Parse(_document);
        _setting = ini.Settings.FirstOrDefault(setting =>
            setting.Key.Equals(SettingKey, StringComparison.OrdinalIgnoreCase));

        if (_setting is null)
            throw new InvalidDataException(
                $"{SettingKey} is missing from {Path.GetFileName(_document.FullPath)}. Open the game's Customization screen once, then reload.");

        return TagNameRegex().Matches(_setting.Value)
            .Select(match => match.Groups["tag"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ConfigBackup> SaveAsync(IEnumerable<string> selectedTags)
    {
        if (_setting is null)
            throw new InvalidOperationException("Reload the customization file before saving.");
        if (IsGameRunning)
            throw new InvalidOperationException(
                "Campaign Evolved is running. Close the game before saving so it cannot overwrite the equipped cosmetics.");

        string[] tags = selectedTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // The game writes a blank value when all slots use their defaults.
        string value = tags.Length == 0
            ? string.Empty
            : "(" + string.Join(",", tags.Select(tag =>
                $"(TagName=\"{Escape(tag)}\")")) + ")";
        _setting.SetValue(value);
        return await _configStore.SaveAsync(_documents);
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    [GeneratedRegex(@"TagName\s*=\s*""(?<tag>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex TagNameRegex();
}
