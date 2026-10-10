using System.Text.Json;
using HaloMeister.App.Localization;

namespace HaloMeister.App.Services;

public sealed record GameTextFile(string Language, string Path);

public sealed record GameTextEntry(string Namespace, string Key, string Text);

/// <summary>
/// Lists shipped localization JSON, one file per language. Game text lives in
/// <c>Assets/GameText</c> and subtitles in <c>Assets/Subtitles</c>. Regenerate
/// both with <c>native/HaloMeister.Text/export-game-text.ps1</c>.
/// </summary>
public sealed class GameTextService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _folder;
    private readonly string _missingKey;

    private GameTextService(string folder, string missingKey)
    {
        _folder = folder;
        _missingKey = missingKey;
    }

    public static GameTextService Current { get; } = new("GameText", "game_text.missing");

    public static GameTextService Subtitles { get; } = new("Subtitles", "subtitles.missing");

    public Task<IReadOnlyList<GameTextFile>> ListAsync() => Task.Run(List);

    public Task<IReadOnlyList<GameTextFile>> RefreshAsync() => ListAsync();

    public async Task<IReadOnlyList<GameTextEntry>> ReadAsync(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        GameTextDocument? document = await JsonSerializer.DeserializeAsync<GameTextDocument>(stream, Json);
        if (document?.Entries is null)
            return [];
        return document.Entries
            .Select(entry => new GameTextEntry(
                entry.Namespace ?? "",
                entry.Key ?? "",
                entry.Text ?? ""))
            .ToList();
    }

    private IReadOnlyList<GameTextFile> List()
    {
        string directory = TextDirectory();
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException(L.Get(_missingKey));

        return Directory.EnumerateFiles(directory, "*.json")
            .Select(path => new GameTextFile(Path.GetFileNameWithoutExtension(path), path))
            .Where(file => file.Language.Length > 0)
            .OrderBy(file => file.Language, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string TextDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "Assets", _folder);

    private sealed class GameTextDocument
    {
        public string? Language { get; set; }
        public List<GameTextEntryDto>? Entries { get; set; }
    }

    private sealed class GameTextEntryDto
    {
        public string? Namespace { get; set; }
        public string? Key { get; set; }
        public string? Text { get; set; }
    }
}
