using System.Text.Json;

namespace HaloMeister.App.Services;

public sealed class RuntimeCustomizationPreferenceStore
{
    private readonly string _path;

    public RuntimeCustomizationPreferenceStore()
    {
        string local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        _path = Path.Combine(
            local,
            "HaloMeister",
            "runtime-customization.json");
    }

    public IReadOnlyDictionary<string, string?> Load(string profileId)
    {
        Dictionary<string, Dictionary<string, string?>> all = LoadAll();
        return all.TryGetValue(profileId, out Dictionary<string, string?>? profile)
            ? new Dictionary<string, string?>(
                profile,
                StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    }

    public void Set(string profileId, string segment, string? tag)
    {
        Dictionary<string, Dictionary<string, string?>> all = LoadAll();
        if (!all.TryGetValue(profileId, out Dictionary<string, string?>? profile))
        {
            profile = new Dictionary<string, string?>(
                StringComparer.OrdinalIgnoreCase);
            all[profileId] = profile;
        }
        profile[segment] = tag;
        SaveAll(all);
    }

    private Dictionary<string, Dictionary<string, string?>> LoadAll()
    {
        if (!File.Exists(_path))
            return new Dictionary<string, Dictionary<string, string?>>(
                StringComparer.OrdinalIgnoreCase);
        try
        {
            string json = File.ReadAllText(_path);
            Dictionary<string, Dictionary<string, string?>>? parsed =
                JsonSerializer.Deserialize<
                    Dictionary<string, Dictionary<string, string?>>>(json);
            if (parsed is null)
                return new(StringComparer.OrdinalIgnoreCase);

            var normalized =
                new Dictionary<string, Dictionary<string, string?>>(
                    StringComparer.OrdinalIgnoreCase);
            foreach ((string key, Dictionary<string, string?> value) in parsed)
            {
                normalized[key] = new Dictionary<string, string?>(
                    value,
                    StringComparer.OrdinalIgnoreCase);
            }
            return normalized;
        }
        catch
        {
            return new Dictionary<string, Dictionary<string, string?>>(
                StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveAll(
        Dictionary<string, Dictionary<string, string?>> preferences)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (directory is null)
            throw new InvalidOperationException(
                "Runtime customization preference path is invalid.");
        Directory.CreateDirectory(directory);
        string temporary = _path + ".tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(
                preferences,
                new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, true);
    }
}
