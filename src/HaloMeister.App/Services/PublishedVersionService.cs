using System.Net.Http.Headers;
using System.Text.Json;
using HaloMeister.App.Localization;

namespace HaloMeister.App.Services;

public sealed record PublishedVersionManifest(
    string? App,
    string? Bridge,
    string? CampaignMod,
    string? CharactersMod);

public sealed record PublishedVersionReport(IReadOnlyList<string> Lines)
{
    public bool HasUpdate => Lines.Count > 0;

    public string Message => string.Join(Environment.NewLine, Lines);
}

/// <summary>
/// Reads the published version manifest and compares it with this build.
/// </summary>
public sealed class PublishedVersionService
{
    public const string ManifestUrl = "https://halomisterversion.halomod.com/version.json";
    public const string GitHubManifestUrl =
        "https://raw.githubusercontent.com/xiashizq/halo-meister/master/version.json";

    private static readonly string[] ManifestUrls = [ManifestUrl, GitHubManifestUrl];

    private static readonly HttpClient Http = CreateClient();
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PublishedVersionManifest? _cached;
    private DateTimeOffset _cachedAt;

    private PublishedVersionService()
    {
    }

    public static PublishedVersionService Current { get; } = new();

    public async Task<PublishedVersionReport?> CheckAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        PublishedVersionManifest? manifest = await FetchAsync(forceRefresh, cancellationToken);
        return manifest is null ? null : Compare(manifest);
    }

    public async Task<PublishedVersionManifest?> FetchAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh &&
            _cached is not null &&
            DateTimeOffset.UtcNow - _cachedAt < CacheLifetime)
            return _cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh &&
                _cached is not null &&
                DateTimeOffset.UtcNow - _cachedAt < CacheLifetime)
                return _cached;

            foreach (string url in ManifestUrls)
            {
                PublishedVersionManifest? manifest = await TryFetchAsync(url, cancellationToken);
                if (manifest is null)
                    continue;
                _cached = manifest;
                _cachedAt = DateTimeOffset.UtcNow;
                return _cached;
            }

            return _cached;
        }
        catch
        {
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static PublishedVersionReport Compare(PublishedVersionManifest manifest)
    {
        var lines = new List<string>();
        string localApp = ReleaseUpdateService.Current.CurrentVersion;
        string localBridge = BridgeVersion.Application.ToString();
        if (IsNewerSemVer(manifest.App, localApp))
        {
            lines.Add(L.Format(
                "version.manifest_app",
                manifest.App,
                localApp));
        }

        if (IsNewerSemVer(manifest.Bridge, localBridge))
        {
            lines.Add(L.Format(
                "version.manifest_bridge",
                manifest.Bridge,
                localBridge));
        }

        AddModLine(
            lines,
            "builtin_mod.campaign.title",
            manifest.CampaignMod,
            FullPalettesOverlayService.VersionFromFingerprint(
                FullPalettesOverlayService.ExpectedBundledFingerprint));
        AddModLine(
            lines,
            "builtin_mod.characters.title",
            manifest.CharactersMod,
            FullPalettesOverlayService.VersionFromFingerprint(
                FullPalettesOverlayService.ExpectedCharacterFingerprint));
        return new PublishedVersionReport(lines);
    }

    private static void AddModLine(
        List<string> lines,
        string titleKey,
        string? published,
        string? local)
    {
        if (string.IsNullOrWhiteSpace(published) ||
            string.IsNullOrWhiteSpace(local) ||
            string.Equals(published, local, StringComparison.OrdinalIgnoreCase))
            return;

        lines.Add(L.Format(
            "version.manifest_mod",
            L.Get(titleKey),
            published.Trim(),
            local));
    }

    private static bool IsNewerSemVer(string? published, string local)
    {
        return BridgeVersion.TryParse(published, out BridgeVersion remote) &&
               BridgeVersion.TryParse(local, out BridgeVersion current) &&
               !remote.IsLegacy &&
               !current.IsLegacy &&
               remote > current;
    }

    private static async Task<PublishedVersionManifest?> TryFetchAsync(
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(
                body,
                cancellationToken: cancellationToken);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("app", out _))
                return null;

            JsonElement mods = default;
            bool hasMods = root.TryGetProperty("mods", out mods) &&
                           mods.ValueKind == JsonValueKind.Object;
            return new PublishedVersionManifest(
                ReadString(root, "app"),
                ReadString(root, "bridge"),
                hasMods ? ReadString(mods, "campaign") : null,
                hasMods ? ReadString(mods, "characters") : null);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
            return null;
        string? text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"HaloMeister/{ReleaseUpdateService.Current.CurrentVersion}");
        return client;
    }
}
