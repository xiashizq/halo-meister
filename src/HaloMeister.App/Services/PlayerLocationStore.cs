using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HaloMeister.App.Services;

public sealed record SavedPlayerLocation(
    Guid Id,
    string Name,
    PlayerCoordinates Position,
    DateTimeOffset SavedAt)
{
    public string Detail => string.Create(
        CultureInfo.InvariantCulture,
        $"{Position.X:0.###}, {Position.Y:0.###}, {Position.Z:0.###}");
}

/// <summary>Persists named player teleport destinations outside a game session.</summary>
public sealed class PlayerLocationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _locationsPath;

    public PlayerLocationStore()
    {
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        _locationsPath = Path.Combine(
            localAppData,
            "HaloMeister",
            "PlayerTools",
            "locations.json");
    }

    public IReadOnlyList<SavedPlayerLocation> Load()
    {
        if (!File.Exists(_locationsPath))
            return [];

        try
        {
            SavedPlayerLocation[] locations = JsonSerializer.Deserialize<SavedPlayerLocation[]>(
                File.ReadAllText(_locationsPath, Encoding.UTF8),
                JsonOptions) ?? [];
            if (locations.Any(location =>
                    location.Id == Guid.Empty ||
                    string.IsNullOrWhiteSpace(location.Name) ||
                    !float.IsFinite(location.Position.X) ||
                    !float.IsFinite(location.Position.Y) ||
                    !float.IsFinite(location.Position.Z)))
            {
                throw new InvalidDataException("Saved player locations contain invalid data.");
            }

            return locations
                .OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Saved player locations could not be read.", ex);
        }
    }

    public SavedPlayerLocation Save(string name, PlayerCoordinates position)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 80 || name.IndexOfAny(['\r', '\n', '\t']) >= 0)
        {
            throw new ArgumentException(
                "Location names must contain 1–80 printable characters.",
                nameof(name));
        }

        List<SavedPlayerLocation> locations = [.. Load()];
        SavedPlayerLocation? existing = locations.FirstOrDefault(location =>
            string.Equals(location.Name, name, StringComparison.OrdinalIgnoreCase));
        var saved = new SavedPlayerLocation(
            existing?.Id ?? Guid.NewGuid(), name, position, DateTimeOffset.UtcNow);
        if (existing is not null)
            locations.Remove(existing);
        locations.Add(saved);
        Write(locations);
        return saved;
    }

    public void Delete(Guid id)
    {
        List<SavedPlayerLocation> locations = [.. Load()];
        if (locations.RemoveAll(location => location.Id == id) == 0)
            throw new InvalidOperationException("That saved location no longer exists.");
        Write(locations);
    }

    private void Write(IReadOnlyCollection<SavedPlayerLocation> locations)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_locationsPath)!);
        string temporary = _locationsPath + ".tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(
                locations.OrderBy(location => location.Name, StringComparer.OrdinalIgnoreCase),
                JsonOptions),
            new UTF8Encoding(false));
        File.Move(temporary, _locationsPath, true);
    }
}
