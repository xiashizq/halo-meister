using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using HaloMeister.App.Localization;

namespace HaloMeister.App.Services;

internal sealed record GameBuildProfile(
    string Id,
    string Sha256,
    int PeTimestamp,
    int ImageSize,
    long TagTablePointerOffset,
    long ArenaTableOffset,
    long StringIdStorageRva,
    long StringIdStorageUsedRva,
    long StringIdStringsRva,
    long StringIdCountRva,
    long StringIdBuiltinTableRva);

internal static class GameBuildProfileCatalog
{
    private const string RelativeCatalogPath = "Assets/GameBuildProfiles.json";

    public static GameBuildProfile Resolve(string modulePath)
    {
        string catalogPath = Path.Combine(
            AppContext.BaseDirectory,
            RelativeCatalogPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(catalogPath))
        {
            throw new FileNotFoundException(
                L.Get("build_profile.catalog_missing"),
                catalogPath);
        }

        string hash;
        int timestamp;
        int imageSize;
        using (FileStream stream = File.OpenRead(modulePath))
        {
            hash = Convert.ToHexString(SHA256.HashData(stream));
            stream.Position = 0;
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            timestamp = pe.PEHeaders.CoffHeader.TimeDateStamp;
            imageSize = pe.PEHeaders.PEHeader?.SizeOfImage
                ?? throw new InvalidDataException(
                    L.Get("build_profile.missing_pe_header"));
        }

        foreach (GameBuildProfile profile in Load(catalogPath))
        {
            if (profile.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase) &&
                profile.PeTimestamp == timestamp &&
                profile.ImageSize == imageSize)
            {
                return profile;
            }
        }

        throw new NotSupportedException(
            L.Format(
                "build_profile.unsupported_dll",
                hash,
                $"0x{timestamp:X8}",
                $"0x{imageSize:X8}"));
    }

    private static IReadOnlyList<GameBuildProfile> Load(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        var profiles = new List<GameBuildProfile>();
        foreach (JsonElement item in document.RootElement
                     .GetProperty("profiles")
                     .EnumerateArray())
        {
            JsonElement runtimeTags = item.GetProperty("runtimeTags");
            if (!item.TryGetProperty("researchAnchors", out JsonElement anchors))
            {
                throw new InvalidDataException(
                    $"Build profile '{item.GetProperty("id").GetString()}' is missing researchAnchors.");
            }

            profiles.Add(new GameBuildProfile(
                item.GetProperty("id").GetString()
                    ?? throw new InvalidDataException("A build profile has no id."),
                item.GetProperty("sha256").GetString()
                    ?? throw new InvalidDataException("A build profile has no SHA-256."),
                checked((int)ParseHex(item.GetProperty("peTimestamp"))),
                checked((int)ParseHex(item.GetProperty("imageSize"))),
                checked((long)ParseHex(runtimeTags.GetProperty("tagTablePointer"))),
                checked((long)ParseHex(runtimeTags.GetProperty("arenaTable"))),
                checked((long)ParseHex(anchors.GetProperty("stringIdStorage"))),
                checked((long)ParseHex(anchors.GetProperty("stringIdStorageUsed"))),
                checked((long)ParseHex(anchors.GetProperty("stringIdStrings"))),
                checked((long)ParseHex(anchors.GetProperty("stringIdCount"))),
                checked((long)ParseHex(anchors.GetProperty("stringIdBuiltinTable")))));
        }
        return profiles;
    }

    private static ulong ParseHex(JsonElement value)
    {
        string text = value.GetString()
            ?? throw new InvalidDataException("A build-profile address is not a string.");
        if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            !ulong.TryParse(
                text.AsSpan(2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out ulong parsed))
        {
            throw new InvalidDataException(
                $"Invalid hexadecimal value '{text}' in the build-profile catalog.");
        }
        return parsed;
    }
}
