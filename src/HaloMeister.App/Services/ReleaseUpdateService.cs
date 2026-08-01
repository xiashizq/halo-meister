using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace HaloMeister.App.Services;

public sealed record ReleaseUpdateResult(
    string CurrentVersion,
    string? LatestVersion,
    Uri? ReleaseUri,
    bool UpdateAvailable,
    string Message);

public sealed class ReleaseUpdateService
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/NicmeisteR/halo-meister/releases/latest";
    private static readonly HttpClient Http = CreateClient();
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private ReleaseUpdateResult? _cachedResult;

    private ReleaseUpdateService()
    {
    }

    public static ReleaseUpdateService Current { get; } = new();

    public string CurrentVersion { get; } = ReadCurrentVersion();

    public async Task<ReleaseUpdateResult> CheckAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _cachedResult is not null)
            return _cachedResult;

        await _checkGate.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && _cachedResult is not null)
                return _cachedResult;

            using HttpResponseMessage response = await Http.GetAsync(
                LatestReleaseApi,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(
                body,
                cancellationToken: cancellationToken);
            JsonElement root = document.RootElement;

            string tag = root.GetProperty("tag_name").GetString()?.Trim() ?? string.Empty;
            string latest = tag.StartsWith('v') ? tag[1..] : tag;
            string releaseUrl = root.GetProperty("html_url").GetString() ?? string.Empty;
            if (!TryCompareVersions(latest, CurrentVersion, out int comparison) ||
                !Uri.TryCreate(releaseUrl, UriKind.Absolute, out Uri? releaseUri))
            {
                throw new InvalidDataException(
                    "GitHub returned release metadata with an invalid version or URL.");
            }

            bool available = comparison > 0;
            _cachedResult = new ReleaseUpdateResult(
                CurrentVersion,
                latest,
                releaseUri,
                available,
                available
                    ? $"Halo Meister {latest} is available. You have {CurrentVersion}."
                    : $"Halo Meister {CurrentVersion} is up to date.");
            return _cachedResult;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"HaloMeister/{ReadCurrentVersion()} (+https://github.com/NicmeisteR/halo-meister)");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static string ReadCurrentVersion()
    {
        Assembly assembly = Assembly.GetEntryAssembly() ?? typeof(ReleaseUpdateService).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+', 2)[0];

        Version? version = assembly.GetName().Version;
        return version is null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    private static bool TryCompareVersions(string left, string right, out int comparison)
    {
        comparison = 0;
        if (!TryParseVersion(left, out int[]? leftParts, out string? leftPreRelease) ||
            !TryParseVersion(right, out int[]? rightParts, out string? rightPreRelease))
            return false;

        for (int index = 0; index < leftParts.Length; index++)
        {
            comparison = leftParts[index].CompareTo(rightParts[index]);
            if (comparison != 0)
                return true;
        }

        comparison = ComparePreRelease(leftPreRelease, rightPreRelease);
        return true;
    }

    private static bool TryParseVersion(
        string value,
        out int[] parts,
        out string? preRelease)
    {
        string withoutMetadata = value.Split('+', 2)[0];
        string[] releaseAndSuffix = withoutMetadata.Split('-', 2);
        string[] numericParts = releaseAndSuffix[0].Split('.');
        parts = new int[3];
        preRelease = releaseAndSuffix.Length == 2 ? releaseAndSuffix[1] : null;
        return numericParts.Length == 3 &&
               int.TryParse(numericParts[0], out parts[0]) &&
               int.TryParse(numericParts[1], out parts[1]) &&
               int.TryParse(numericParts[2], out parts[2]);
    }

    private static int ComparePreRelease(string? left, string? right)
    {
        if (left is null)
            return right is null ? 0 : 1;
        if (right is null)
            return -1;

        string[] leftParts = left.Split('.');
        string[] rightParts = right.Split('.');
        for (int index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
        {
            if (index >= leftParts.Length)
                return -1;
            if (index >= rightParts.Length)
                return 1;

            bool leftNumeric = int.TryParse(leftParts[index], out int leftNumber);
            bool rightNumeric = int.TryParse(rightParts[index], out int rightNumber);
            int result = (leftNumeric, rightNumeric) switch
            {
                (true, true) => leftNumber.CompareTo(rightNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.Compare(leftParts[index], rightParts[index], StringComparison.Ordinal),
            };
            if (result != 0)
                return result;
        }
        return 0;
    }
}
