using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HaloMeister.App.Localization;

namespace HaloMeister.App.Services;

public sealed record CharacterModelFile(string Id, string Name, string Folder, string Kind);

public sealed record CharacterModelVariant(string Name);

public sealed record CharacterModelRegion(
    string Name,
    IReadOnlyList<string> Permutations,
    string Selected);

public sealed record CharacterModelAnimation(string Name, string Kind, int Frames, int Index);

public sealed record CharacterModelPose(int Frames, int Bones, float[] Matrices);

public sealed record CharacterModelPreview(
    string Id,
    string Name,
    string Kind,
    string Variant,
    int Vertices,
    int Triangles,
    float[] Positions,
    float[] Normals,
    int[] Indices,
    ushort[] Joints,
    float[] Weights,
    int Bones,
    IReadOnlyList<CharacterModelVariant> Variants,
    IReadOnlyList<CharacterModelRegion> Regions,
    IReadOnlyList<CharacterModelAnimation> Animations);

/// <summary>
/// Lists and previews Campaign Evolved character models. Geometry, variants,
/// and animations come from the same tag and mesh-sync path Baboon uses.
/// </summary>
public sealed class CharacterModelLibraryService
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
    private IReadOnlyList<CharacterModelFile>? _files;

    public static CharacterModelLibraryService Current { get; } = new();

    public async Task<IReadOnlyList<CharacterModelFile>> ListAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureReadyAsync();
            return _files ?? [];
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

    public async Task<IReadOnlyList<CharacterModelFile>> RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            ResetUnlocked();
            await EnsureReadyAsync();
            return _files ?? [];
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

    public async Task<CharacterModelPreview> InspectAsync(
        string id,
        string? variant,
        IReadOnlyDictionary<string, string>? picks)
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureReadyAsync();
            using JsonDocument doc = await SendAsync(new
            {
                cmd = "inspect",
                id,
                variant,
                picks,
            });
            EnsureOk(doc);
            return ParsePreview(doc.RootElement);
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

    public async Task<CharacterModelPose> PoseAsync(string id, int index)
    {
        await _gate.WaitAsync();
        try
        {
            await EnsureReadyAsync();
            using JsonDocument doc = await SendAsync(new
            {
                cmd = "pose",
                id,
                index,
            });
            EnsureOk(doc);
            JsonElement root = doc.RootElement;
            return new CharacterModelPose(
                root.TryGetProperty("frames", out JsonElement frames) ? frames.GetInt32() : 0,
                root.TryGetProperty("bones", out JsonElement bones) ? bones.GetInt32() : 0,
                DecodeFloats(ReadString(root, "matrices")));
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
        if (_process is { HasExited: false } && _files is not null && _stdin is not null && _stdout is not null)
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
            throw new InvalidOperationException(L.Get("character_model.tool_exited"));

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
        _files = ParseFiles(doc.RootElement);
    }

    private async Task<JsonDocument> SendAsync(object command)
    {
        if (_process is null || _process.HasExited || _stdin is null || _stdout is null)
            throw new IOException(L.Get("character_model.tool_exited"));

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
        _files = null;
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
            ? L.Get("character_model.tool_exited")
            : tail;
    }

    private static void EnsureOk(JsonDocument doc)
    {
        if (doc.RootElement.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True)
            return;
        string error = doc.RootElement.TryGetProperty("error", out JsonElement errorElement)
            ? errorElement.GetString() ?? L.Get("character_model.tool_exited")
            : L.Get("character_model.tool_exited");
        throw new InvalidOperationException(error);
    }

    private static IReadOnlyList<CharacterModelFile> ParseFiles(JsonElement root)
    {
        var files = new List<CharacterModelFile>();
        if (!root.TryGetProperty("files", out JsonElement array))
            return files;
        foreach (JsonElement item in array.EnumerateArray())
        {
            files.Add(new CharacterModelFile(
                ReadString(item, "id"),
                ReadString(item, "name"),
                ReadString(item, "folder"),
                ReadString(item, "kind")));
        }
        return files;
    }

    private static CharacterModelPreview ParsePreview(JsonElement root)
    {
        var variants = new List<CharacterModelVariant>();
        if (root.TryGetProperty("variants", out JsonElement variantArray))
        {
            foreach (JsonElement item in variantArray.EnumerateArray())
                variants.Add(new CharacterModelVariant(ReadString(item, "name")));
        }

        var regions = new List<CharacterModelRegion>();
        if (root.TryGetProperty("regions", out JsonElement regionArray))
        {
            foreach (JsonElement item in regionArray.EnumerateArray())
            {
                var perms = new List<string>();
                if (item.TryGetProperty("permutations", out JsonElement permArray))
                {
                    foreach (JsonElement perm in permArray.EnumerateArray())
                    {
                        string? name = perm.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                            perms.Add(name);
                    }
                }
                regions.Add(new CharacterModelRegion(
                    ReadString(item, "name"),
                    perms,
                    ReadString(item, "selected")));
            }
        }

        var animations = new List<CharacterModelAnimation>();
        if (root.TryGetProperty("animations", out JsonElement animationArray))
        {
            foreach (JsonElement item in animationArray.EnumerateArray())
            {
                animations.Add(new CharacterModelAnimation(
                    ReadString(item, "name"),
                    ReadString(item, "kind"),
                    item.TryGetProperty("frames", out JsonElement frames) ? frames.GetInt32() : 0,
                    item.TryGetProperty("index", out JsonElement index) ? index.GetInt32() : 0));
            }
        }

        float[] positions = DecodeFloats(ReadString(root, "positions"));
        float[] normals = DecodeFloats(ReadString(root, "normals"));
        int[] indices = DecodeIndices(ReadString(root, "indices"));
        ushort[] joints = DecodeJoints(ReadString(root, "joints"));
        float[] weights = DecodeFloats(ReadString(root, "weights"));
        return new CharacterModelPreview(
            ReadString(root, "id"),
            ReadString(root, "name"),
            ReadString(root, "kind"),
            ReadString(root, "variant"),
            root.TryGetProperty("vertices", out JsonElement vertices) ? vertices.GetInt32() : positions.Length / 3,
            root.TryGetProperty("triangles", out JsonElement triangles) ? triangles.GetInt32() : indices.Length / 3,
            positions,
            normals,
            indices,
            joints,
            weights,
            root.TryGetProperty("bones", out JsonElement bones) ? bones.GetInt32() : 0,
            variants,
            regions,
            animations);
    }

    private static float[] DecodeFloats(string base64)
    {
        if (string.IsNullOrEmpty(base64))
            return [];
        byte[] bytes = Convert.FromBase64String(base64);
        var values = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, values, 0, values.Length * 4);
        return values;
    }

    private static ushort[] DecodeJoints(string base64)
    {
        if (string.IsNullOrEmpty(base64))
            return [];
        byte[] bytes = Convert.FromBase64String(base64);
        var values = new ushort[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, values, 0, values.Length * 2);
        return values;
    }

    private static int[] DecodeIndices(string base64)
    {
        if (string.IsNullOrEmpty(base64))
            return [];
        byte[] bytes = Convert.FromBase64String(base64);
        var values = new int[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, values, 0, values.Length * 4);
        return values;
    }

    private static string ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string ResolvePaks() =>
        GameInstallationService.Current.TryGetPaksDirectory()
        ?? throw new InvalidOperationException(L.Get("character_model.no_install"));

    private static string ResolveExe()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Assets", "Native", "halomeister-models.exe"),
            Path.Combine(AppContext.BaseDirectory, "halomeister-models.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(L.Get("character_model.tool_missing"));
    }
}
