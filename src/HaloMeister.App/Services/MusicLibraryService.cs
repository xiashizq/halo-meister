using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HaloMeister.App.Localization;

namespace HaloMeister.App.Services;

public sealed record MusicTrack(string Package, string Name, string Folder, string Kind);

public sealed record MusicPermutation(
    int Index,
    string Name,
    string Language,
    string Location,
    uint MediaId);

public sealed record MusicCue(
    string Package,
    string Language,
    IReadOnlyList<string> Languages,
    IReadOnlyList<MusicPermutation> Permutations);

public sealed record MusicFile(
    string Name,
    string Path,
    double Seconds,
    int Channels,
    int SampleRate);

public sealed record MusicExportBatch(
    IReadOnlyList<MusicFile> Files,
    IReadOnlyList<string> Failed);

/// <summary>
/// Lists, plays, and exports Campaign Evolved music. The samples live in the
/// game's Wwise pak set, so a small native helper (the same decode path Baboon
/// uses) stays running and answers one JSON command at a time.
/// </summary>
public sealed class MusicLibraryService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _stderr = new();
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private IReadOnlyList<MusicTrack>? _tracks;

    public static MusicLibraryService Current { get; } = new();

    public async Task<IReadOnlyList<MusicTrack>> ListAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureReadyAsync();
            return _tracks ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            ResetUnlocked();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MusicTrack>> RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            ResetUnlocked();
            await EnsureReadyAsync();
            return _tracks ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            ResetUnlocked();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<int>>> PlayableIndicesAsync(
        IReadOnlyList<string> packages,
        string? language)
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureReadyAsync();
            using JsonDocument doc = await SendAsync(new
            {
                cmd = "playable",
                packages,
                language,
            });
            EnsureOk(doc);
            var map = new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("results", out JsonElement results))
            {
                foreach (JsonElement item in results.EnumerateArray())
                {
                    if (!item.TryGetProperty("package", out JsonElement packageElement))
                        continue;
                    string package = packageElement.GetString() ?? "";
                    var indices = new List<int>();
                    if (item.TryGetProperty("indices", out JsonElement indexArray))
                    {
                        foreach (JsonElement index in indexArray.EnumerateArray())
                        {
                            if (index.TryGetInt32(out int value))
                                indices.Add(value);
                        }
                    }

                    map[package] = indices;
                }
            }

            return map;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            ResetUnlocked();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MusicCue> ResolveAsync(string package, string? language)
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureReadyAsync();
            using JsonDocument doc = await SendAsync(new
            {
                cmd = "resolve",
                package,
                language,
            });
            EnsureOk(doc);
            return ParseCue(package, doc.RootElement);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            ResetUnlocked();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MusicFile> ExportAsync(
        string package,
        string? language,
        int index,
        string outputPath)
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureReadyAsync();
            using JsonDocument doc = await SendAsync(new
            {
                cmd = "export",
                package,
                language,
                index,
                @out = outputPath,
            });
            EnsureOk(doc);
            return ParseFile(doc.RootElement.GetProperty("file"));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            ResetUnlocked();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<MusicExportBatch> ExportAllAsync(
        string package,
        string? language,
        string outputDirectory)
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureReadyAsync();
            using JsonDocument doc = await SendAsync(new
            {
                cmd = "exportAll",
                package,
                language,
                @out = outputDirectory,
            });
            EnsureOk(doc);
            var files = new List<MusicFile>();
            if (doc.RootElement.TryGetProperty("files", out JsonElement fileArray))
            {
                foreach (JsonElement file in fileArray.EnumerateArray())
                    files.Add(ParseFile(file));
            }

            var failed = new List<string>();
            if (doc.RootElement.TryGetProperty("failed", out JsonElement failedArray))
            {
                foreach (JsonElement item in failedArray.EnumerateArray())
                {
                    string name = item.TryGetProperty("name", out JsonElement nameElement)
                        ? nameElement.GetString() ?? ""
                        : "";
                    string error = item.TryGetProperty("error", out JsonElement errorElement)
                        ? errorElement.GetString() ?? ""
                        : "";
                    failed.Add(string.IsNullOrEmpty(name) ? error : $"{name}: {error}");
                }
            }

            return new MusicExportBatch(files, failed);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            ResetUnlocked();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Shutdown()
    {
        if (!_gate.Wait(TimeSpan.FromSeconds(2)))
        {
            Kill();
            return;
        }

        try
        {
            ResetUnlocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureReadyAsync()
    {
        if (_process is { HasExited: false } && _tracks is not null && _stdin is not null && _stdout is not null)
            return;

        ResetUnlocked();
        string paks = ResolvePaks();
        string exe = ResolveExe();
        var process = new Process();
        process.StartInfo.FileName = exe;
        process.StartInfo.ArgumentList.Add("--paks");
        process.StartInfo.ArgumentList.Add(paks);
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardInput = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
        process.StartInfo.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (!process.Start())
            throw new InvalidOperationException(L.Get("music.tool_exited"));

        _process = process;
        _stdin = process.StandardInput;
        _stdin.AutoFlush = true;
        _stdout = process.StandardOutput;
        StartErrorDrain(process);
        string? line = await _stdout.ReadLineAsync();
        if (string.IsNullOrWhiteSpace(line))
            throw new IOException(ExitMessage());

        using JsonDocument doc = JsonDocument.Parse(line);
        EnsureOk(doc);
        _tracks = ParseTracks(doc.RootElement);
    }

    private async Task<JsonDocument> SendAsync(object command)
    {
        if (_process is null || _process.HasExited || _stdin is null || _stdout is null)
            throw new IOException(L.Get("music.tool_exited"));

        await _stdin.WriteLineAsync(JsonSerializer.Serialize(command, Json));
        string? response = await _stdout.ReadLineAsync();
        if (string.IsNullOrWhiteSpace(response))
            throw new IOException(ExitMessage());
        return JsonDocument.Parse(response);
    }

    private void StartErrorDrain(Process process)
    {
        StreamReader errors = process.StandardError;
        _ = Task.Run(async () =>
        {
            try
            {
                while (await errors.ReadLineAsync() is { } line)
                {
                    lock (_stderr)
                    {
                        _stderr.AppendLine(line);
                        if (_stderr.Length > 8000)
                            _stderr.Remove(0, _stderr.Length - 4000);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException)
            {
            }
        });
    }

    private void ResetUnlocked()
    {
        _tracks = null;
        _stdin = null;
        _stdout = null;
        Kill();
        _process = null;
        lock (_stderr)
            _stderr.Clear();
    }

    private void Kill()
    {
        Process? process = _process;
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private string ExitMessage()
    {
        string tail;
        lock (_stderr)
            tail = _stderr.ToString().Trim();
        return string.IsNullOrWhiteSpace(tail)
            ? L.Get("music.tool_exited")
            : tail;
    }

    private static void EnsureOk(JsonDocument doc)
    {
        if (doc.RootElement.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True)
            return;
        string error = doc.RootElement.TryGetProperty("error", out JsonElement errorElement)
            ? errorElement.GetString() ?? L.Get("music.tool_exited")
            : L.Get("music.tool_exited");
        throw new InvalidOperationException(error);
    }

    private static IReadOnlyList<MusicTrack> ParseTracks(JsonElement root)
    {
        var tracks = new List<MusicTrack>();
        if (!root.TryGetProperty("tracks", out JsonElement array))
            return tracks;
        foreach (JsonElement item in array.EnumerateArray())
        {
            string kind = ReadString(item, "kind");
            tracks.Add(new MusicTrack(
                ReadString(item, "package"),
                ReadString(item, "name"),
                ReadString(item, "folder"),
                string.IsNullOrEmpty(kind) ? "music" : kind));
        }
        return tracks;
    }

    private static MusicCue ParseCue(string package, JsonElement root)
    {
        var languages = new List<string>();
        if (root.TryGetProperty("languages", out JsonElement languageArray))
        {
            foreach (JsonElement language in languageArray.EnumerateArray())
            {
                string? value = language.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    languages.Add(value);
            }
        }

        var permutations = new List<MusicPermutation>();
        if (root.TryGetProperty("permutations", out JsonElement permArray))
        {
            foreach (JsonElement item in permArray.EnumerateArray())
            {
                permutations.Add(new MusicPermutation(
                    item.TryGetProperty("index", out JsonElement index) ? index.GetInt32() : permutations.Count,
                    ReadString(item, "name"),
                    ReadString(item, "language"),
                    ReadString(item, "location"),
                    item.TryGetProperty("mediaId", out JsonElement mediaId) ? mediaId.GetUInt32() : 0));
            }
        }

        return new MusicCue(
            package,
            ReadString(root, "language"),
            languages,
            permutations);
    }

    private static MusicFile ParseFile(JsonElement item) => new(
        ReadString(item, "name"),
        ReadString(item, "path"),
        item.TryGetProperty("seconds", out JsonElement seconds) ? seconds.GetDouble() : 0,
        item.TryGetProperty("channels", out JsonElement channels) ? channels.GetInt32() : 0,
        item.TryGetProperty("sampleRate", out JsonElement rate) ? rate.GetInt32() : 0);

    private static string ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string ResolvePaks() =>
        GameInstallationService.Current.TryGetPaksDirectory()
        ?? throw new InvalidOperationException(L.Get("music.no_install"));

    private static string ResolveExe()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Assets", "Native", "halomeister-music.exe"),
            Path.Combine(AppContext.BaseDirectory, "halomeister-music.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(L.Get("music.tool_missing"));
    }
}
