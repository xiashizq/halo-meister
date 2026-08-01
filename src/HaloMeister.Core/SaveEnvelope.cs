using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HaloMeister.Core;

/// <summary>
/// The outer container: the ASCII magic "zlib", an int32 uncompressed length, then a
/// raw zlib stream holding the tagged property payload.
/// </summary>
public static class BlamContainer
{
    private static readonly byte[] Magic = "zlib"u8.ToArray();

    public static bool LooksLikeContainer(ReadOnlySpan<byte> data)
        => data.Length >= 8 && data[..4].SequenceEqual(Magic);

    public static byte[] Unwrap(byte[] file)
    {
        if (!LooksLikeContainer(file))
            throw new BlamFormatException("Not a Blam save container (missing the 'zlib' magic).");

        int expected = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(4, 4));

        using var input = new MemoryStream(file, 8, file.Length - 8, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(expected > 0 ? expected : 64 * 1024);
        zlib.CopyTo(output);

        byte[] payload = output.ToArray();
        if (expected > 0 && payload.Length != expected)
        {
            throw new BlamFormatException(
                $"Container claims {expected} decompressed byte(s) but produced {payload.Length}.");
        }

        return payload;
    }

    public static byte[] Wrap(byte[] payload)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(payload, 0, payload.Length);

        byte[] deflated = compressed.ToArray();
        byte[] result = new byte[8 + deflated.Length];
        Magic.CopyTo(result, 0);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4, 4), payload.Length);
        deflated.CopyTo(result, 8);
        return result;
    }
}

public enum SaveSourceKind
{
    /// <summary>A bare container file on disk.</summary>
    RawContainer,

    /// <summary>A text file (or clipboard text) holding just the base64 container.</summary>
    Base64Text,

    /// <summary>A PlayFab-style JSON response with the container base64 in a "Value" field.</summary>
    Json,
}

/// <summary>
/// Remembers how a save was packaged so it can be written back in exactly the same shape,
/// with every unrelated field of the original JSON left untouched.
/// </summary>
public sealed class SaveEnvelope
{
    public SaveSourceKind Kind { get; private init; }
    public string? SourcePath { get; set; }

    private JsonNode? _json;
    private string[] _valuePath = Array.Empty<string>();

    /// <summary>Human readable description of where the payload was found.</summary>
    public string Description => Kind switch
    {
        SaveSourceKind.RawContainer => "raw container",
        SaveSourceKind.Base64Text => "base64 text",
        _ => _valuePath.Length > 0 ? $"JSON at {string.Join(" -> ", _valuePath)}" : "JSON",
    };

    public static SaveEnvelope LoadFile(string path, out byte[] payload)
    {
        byte[] bytes = File.ReadAllBytes(path);
        SaveEnvelope envelope = Load(bytes, out payload);
        envelope.SourcePath = path;
        return envelope;
    }

    public static SaveEnvelope Load(byte[] bytes, out byte[] payload)
    {
        if (BlamContainer.LooksLikeContainer(bytes))
        {
            payload = BlamContainer.Unwrap(bytes);
            return new SaveEnvelope { Kind = SaveSourceKind.RawContainer };
        }

        string text = DecodeText(bytes).Trim();

        if (text.StartsWith('{') || text.StartsWith('['))
        {
            JsonNode? root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            if (root is not null && TryFindContainerString(root, new List<string>(), out string[] path, out byte[] found))
            {
                payload = BlamContainer.Unwrap(found);
                return new SaveEnvelope { Kind = SaveSourceKind.Json, _json = root, _valuePath = path };
            }

            throw new BlamFormatException(
                "That JSON does not contain a recognisable save blob. Expected a base64 string " +
                "beginning with the 'zlib' magic (for example data.Data.BlamProgressSave.Value).");
        }

        payload = BlamContainer.Unwrap(FromBase64(text));
        return new SaveEnvelope { Kind = SaveSourceKind.Base64Text };
    }

    /// <summary>Re-packages a payload in the original envelope shape.</summary>
    public byte[] Rebuild(byte[] payload)
    {
        byte[] container = BlamContainer.Wrap(payload);
        string base64 = Convert.ToBase64String(container);

        switch (Kind)
        {
            case SaveSourceKind.RawContainer:
                return container;

            case SaveSourceKind.Base64Text:
                return Encoding.UTF8.GetBytes(base64);

            default:
                JsonNode node = _json ?? throw new InvalidOperationException("No JSON document was loaded.");
                JsonNode cursor = node;
                for (int i = 0; i < _valuePath.Length - 1; i++)
                    cursor = cursor[_valuePath[i]] ?? throw new InvalidOperationException("JSON shape changed.");

                cursor[_valuePath[^1]] = base64;

                string json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                return Encoding.UTF8.GetBytes(json);
        }
    }

    /// <summary>The base64 string a user would paste back into a save editor or web request.</summary>
    public static string ToBase64(byte[] payload) => Convert.ToBase64String(BlamContainer.Wrap(payload));

    /// <summary>The raw zlib container used by the game and stored by automatic backups.</summary>
    public static byte[] ToContainer(byte[] payload) => BlamContainer.Wrap(payload);

    private static bool TryFindContainerString(
        JsonNode node, List<string> path, out string[] foundPath, out byte[] container)
    {
        foundPath = Array.Empty<string>();
        container = Array.Empty<byte>();

        switch (node)
        {
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> kv in obj)
                {
                    if (kv.Value is null) continue;
                    path.Add(kv.Key);
                    if (TryFindContainerString(kv.Value, path, out foundPath, out container)) return true;
                    path.RemoveAt(path.Count - 1);
                }
                return false;

            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is null) continue;
                    path.Add(i.ToString());
                    if (TryFindContainerString(arr[i]!, path, out foundPath, out container)) return true;
                    path.RemoveAt(path.Count - 1);
                }
                return false;

            case JsonValue value when value.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s):
                if (s.Length < 16) return false;
                try
                {
                    byte[] candidate = FromBase64(s);
                    if (!BlamContainer.LooksLikeContainer(candidate)) return false;
                    foundPath = path.ToArray();
                    container = candidate;
                    return true;
                }
                catch (FormatException)
                {
                    return false;
                }

            default:
                return false;
        }
    }

    private static byte[] FromBase64(string text)
    {
        // Tolerate whitespace, newlines and URL-safe alphabets pasted from a browser.
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c) || c == '"' || c == ',') continue;
            builder.Append(c switch { '-' => '+', '_' => '/', _ => c });
        }

        string cleaned = builder.ToString();
        int padding = cleaned.Length % 4;
        if (padding == 2) cleaned += "==";
        else if (padding == 3) cleaned += "=";
        else if (padding == 1) throw new FormatException("Not valid base64.");

        return Convert.FromBase64String(cleaned);
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        return Encoding.UTF8.GetString(bytes);
    }
}
