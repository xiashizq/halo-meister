using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HaloMeister.Core;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;
using Windows.Security.Credentials;

namespace HaloMeister.App.Services;

public sealed record TrafficEntry(
    DateTimeOffset Time,
    string Method,
    string Host,
    string Path,
    int? StatusCode,
    bool IsPlayFab,
    bool WasPatched);

public sealed record InterceptedSave(
    byte[] ResponseBytes,
    string Endpoint,
    string BackupPath,
    bool WasPatched);

public sealed record DirectPatchResult(int? DataVersion, string Message);

public sealed record PlayFabGetResult(
    HaloSave Save,
    int? DataVersion,
    string PayloadHash,
    string BackupPath);

public sealed record PlayFabTestFlowResult(
    PlayFabGetResult Before,
    DirectPatchResult Patch,
    PlayFabGetResult After,
    bool Verified);

/// <summary>
/// Local explicit HTTP(S) proxy. HTTPS is decrypted only for PlayFab hosts; every other
/// TLS connection remains an opaque tunnel. Headers and bodies are never logged.
/// </summary>
public sealed class PlayFabProxyService : IDisposable
{
    private const string CredentialResource = "HaloMeister.PlayFab.ClientSession";

    public static PlayFabProxyService Current { get; } = new();

    private readonly object _gate = new();
    private readonly object _sessionGate = new();
    private readonly WindowsProxyRecovery _proxyRecovery = new();
    private ProxyServer? _proxy;
    private ExplicitProxyEndPoint? _endpoint;
    private PlayFabSession? _session;

    public event Action<TrafficEntry>? TrafficObserved;
    public event Action<InterceptedSave>? SaveIntercepted;
    public event Action<string>? Error;
    public event Action? StateChanged;
    public event Action? SessionChanged;

    public SaveBackupStore Backups { get; } = new();
    public Func<byte[]?>? PatchPayloadProvider { get; set; }
    public bool PatchResponses { get; set; }
    public bool PatchOutgoingWrites { get; set; }
    public bool IsRunning => _proxy?.ProxyRunning == true;
    public bool HasCapturedSession
    {
        get
        {
            lock (_sessionGate) return _session is not null;
        }
    }
    public string? SessionHost
    {
        get
        {
            lock (_sessionGate) return _session?.Origin.Host;
        }
    }
    public bool HasSavedSession
    {
        get
        {
            try
            {
                return new PasswordVault().FindAllByResource(CredentialResource).Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
    public int Port { get; private set; } = 8877;

    private PlayFabProxyService()
    {
        _proxyRecovery.RecoverStaleProxy();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Stop();
    }

    public void Start(int port = 8877)
    {
        lock (_gate)
        {
            if (IsRunning) return;
            Port = port;
            _proxyRecovery.Capture();

            var proxy = new ProxyServer(
                rootCertificateName: "Halo Meister Local Proxy Root",
                rootCertificateIssuerName: "Halo Meister Local Proxy Root",
                userTrustRootCertificate: true,
                machineTrustRootCertificate: false,
                trustRootCertificateAsAdmin: false);
            proxy.EnableHttp2 = true;
            proxy.ExceptionFunc = ex => Error?.Invoke($"Proxy connection error: {ex.Message}");
            var endpoint = new ExplicitProxyEndPoint(IPAddress.Loopback, port, decryptSsl: true);

            proxy.BeforeRequest += OnBeforeRequest;
            proxy.BeforeResponse += OnBeforeResponse;
            endpoint.BeforeTunnelConnectRequest += OnBeforeTunnelConnect;
            proxy.AddEndPoint(endpoint);

            try
            {
                proxy.Start(changeSystemProxySettings: false);
                proxy.SetAsSystemHttpProxy(endpoint);
                proxy.SetAsSystemHttpsProxy(endpoint);
                _proxyRecovery.ForceIpv4Loopback(port);
                _proxy = proxy;
                _endpoint = endpoint;
            }
            catch
            {
                Cleanup(proxy, endpoint);
                throw;
            }
        }

        StateChanged?.Invoke();
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_proxy is not { } proxy) return;
            Cleanup(proxy, _endpoint);
            _proxy = null;
            _endpoint = null;
        }

        StateChanged?.Invoke();
    }

    private void Cleanup(ProxyServer proxy, ExplicitProxyEndPoint? endpoint)
    {
        proxy.BeforeRequest -= OnBeforeRequest;
        proxy.BeforeResponse -= OnBeforeResponse;
        if (endpoint is not null)
            endpoint.BeforeTunnelConnectRequest -= OnBeforeTunnelConnect;

        try { proxy.RestoreOriginalProxySettings(); }
        catch (Exception ex) { Error?.Invoke($"Could not restore the previous Windows proxy settings: {ex.Message}"); }

        try { _proxyRecovery.Restore(); }
        catch (Exception ex) { Error?.Invoke($"Could not restore the saved Windows proxy snapshot: {ex.Message}"); }

        try { if (proxy.ProxyRunning) proxy.Stop(); }
        catch (Exception ex) { Error?.Invoke($"Could not stop the local proxy cleanly: {ex.Message}"); }

        try
        {
            if (proxy.CertificateManager.IsRootCertificateUserTrusted())
                proxy.CertificateManager.RemoveTrustedRootCertificate(machineTrusted: false);
        }
        catch (Exception ex) { Error?.Invoke($"Could not remove the temporary Halo Meister root certificate: {ex.Message}"); }
    }

    private Task OnBeforeTunnelConnect(object sender, TunnelConnectSessionEventArgs e)
    {
        string host = e.HttpClient.Request.RequestUri.Host;
        bool playFab = IsPlayFabHost(host);
        e.DecryptSsl = playFab;
        Publish("CONNECT", host, "/", null, playFab, false);
        return Task.CompletedTask;
    }

    private async Task OnBeforeRequest(object sender, SessionEventArgs e)
    {
        Uri uri = e.HttpClient.Request.RequestUri;
        bool playFab = IsPlayFabHost(uri.Host);
        Publish(e.HttpClient.Request.Method, uri.Host, uri.AbsolutePath, null, playFab, false);

        if (playFab)
            CaptureSession(uri, e.HttpClient.Request.Headers.GetFirstHeader("X-Authorization")?.Value);

        if (!playFab || !PatchOutgoingWrites || !e.HttpClient.Request.HasBody)
            return;

        try
        {
            byte[] body = await e.GetRequestBody();
            if (!TryReadSave(body, out HaloSave captured)) return;

            string backup = Backups.Save(
                SaveEnvelope.ToContainer(captured.OriginalPayload),
                "before-outgoing-patch");
            byte[]? replacement = PatchPayloadProvider?.Invoke();
            if (replacement is null) return;

            e.SetRequestBody(captured.Envelope.Rebuild(replacement));
            Publish(e.HttpClient.Request.Method, uri.Host, uri.AbsolutePath, null, true, true);
            _ = backup;
        }
        catch (Exception ex)
        {
            Error?.Invoke($"Outgoing PlayFab patch failed: {ex.Message}");
        }
    }

    private async Task OnBeforeResponse(object sender, SessionEventArgs e)
    {
        Uri uri = e.HttpClient.Request.RequestUri;
        bool playFab = IsPlayFabHost(uri.Host);
        int status = e.HttpClient.Response.StatusCode;
        if (!playFab)
        {
            Publish(e.HttpClient.Request.Method, uri.Host, uri.AbsolutePath, status, false, false);
            return;
        }

        try
        {
            if (!e.HttpClient.Response.HasBody) return;
            byte[] originalBody = await e.GetResponseBody();
            if (!TryReadSave(originalBody, out HaloSave captured))
            {
                Publish(e.HttpClient.Request.Method, uri.Host, uri.AbsolutePath, status, true, false);
                return;
            }

            string backupPath = Backups.Save(
                SaveEnvelope.ToContainer(captured.OriginalPayload),
                "playfab-response");
            byte[] effectiveBody = originalBody;
            bool patched = false;

            if (PatchResponses && PatchPayloadProvider?.Invoke() is { } replacement)
            {
                effectiveBody = captured.Envelope.Rebuild(replacement);
                e.SetResponseBody(effectiveBody);
                patched = true;
            }

            Publish(e.HttpClient.Request.Method, uri.Host, uri.AbsolutePath, status, true, patched);
            SaveIntercepted?.Invoke(new InterceptedSave(
                effectiveBody,
                $"{uri.Host}{uri.AbsolutePath}",
                backupPath,
                patched));
        }
        catch (Exception ex)
        {
            Error?.Invoke($"PlayFab response capture failed: {ex.Message}");
        }
    }

    private void Publish(string method, string host, string path, int? status, bool playFab, bool patched)
        => TrafficObserved?.Invoke(new TrafficEntry(
            DateTimeOffset.Now, method, host, path, status, playFab, patched));

    public async Task<DirectPatchResult> PatchToPlayFabAsync(CancellationToken cancellationToken = default)
    {
        PlayFabSession session;
        lock (_sessionGate)
        {
            session = _session
                ?? throw new InvalidOperationException(
                    "No PlayFab authentication is available. Open Progress & profile and choose Authenticate first.");
        }

        byte[] payload = PatchPayloadProvider?.Invoke()
            ?? throw new InvalidOperationException("There is no edited save loaded.");

        var requestBody = new
        {
            Data = new Dictionary<string, string>
            {
                ["BlamProgressSave"] = SaveEnvelope.ToBase64(payload),
            },
            Permission = "Private",
        };

        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(session.Origin, "/Client/UpdateUserData"))
        {
            Content = JsonContent.Create(requestBody),
        };
        request.Headers.TryAddWithoutValidation("X-Authorization", session.Ticket);

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        string responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string detail = TryGetPlayFabError(responseText) ?? response.ReasonPhrase ?? "Unknown PlayFab error";
            throw new InvalidOperationException($"PlayFab rejected the save ({(int)response.StatusCode}): {detail}");
        }

        int? dataVersion = null;
        try
        {
            using JsonDocument json = JsonDocument.Parse(responseText);
            if (json.RootElement.TryGetProperty("data", out JsonElement data)
                && data.TryGetProperty("DataVersion", out JsonElement version))
            {
                dataVersion = version.GetInt32();
            }
        }
        catch (JsonException)
        {
            // A successful status is sufficient; the version is informational.
        }

        return new DirectPatchResult(
            dataVersion,
            dataVersion is { } value
                ? $"PlayFab accepted the edited save. New data version: {value}."
                : "PlayFab accepted the edited save.");
    }

    public async Task<PlayFabGetResult> GetSaveFromPlayFabAsync(
        CancellationToken cancellationToken = default)
    {
        PlayFabSession session = GetSession();
        string responseText = await SendClientRequestAsync(
            session,
            "/Client/GetUserData",
            new { Keys = new[] { "BlamProgressSave" } },
            cancellationToken);

        HaloSave save;
        try
        {
            save = HaloSave.LoadBytes(Encoding.UTF8.GetBytes(responseText));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"PlayFab returned data, but it did not contain a readable BlamProgressSave: {ex.Message}",
                ex);
        }

        string backupPath = Backups.Save(
            SaveEnvelope.ToContainer(save.OriginalPayload),
            "playfab-test-get");
        int? dataVersion = TryGetDataVersion(responseText);
        string hash = Convert.ToHexString(SHA256.HashData(save.OriginalPayload));
        return new PlayFabGetResult(save, dataVersion, hash, backupPath);
    }

    public async Task<PlayFabTestFlowResult> RunGetPatchGetAsync(
        CancellationToken cancellationToken = default)
    {
        byte[] expectedPayload = PatchPayloadProvider?.Invoke()
            ?? throw new InvalidOperationException("There is no edited save loaded.");

        PlayFabGetResult before = await GetSaveFromPlayFabAsync(cancellationToken);
        DirectPatchResult patch = await PatchToPlayFabAsync(cancellationToken);
        PlayFabGetResult after = await GetSaveFromPlayFabAsync(cancellationToken);
        bool verified = expectedPayload.AsSpan().SequenceEqual(after.Save.OriginalPayload);
        return new PlayFabTestFlowResult(before, patch, after, verified);
    }

    public string SaveSessionToCredentialLocker()
    {
        PlayFabSession session = GetSession();
        var vault = new PasswordVault();
        RemoveSavedCredentials(vault);
        vault.Add(new PasswordCredential(
            CredentialResource,
            session.Origin.Host,
            session.Ticket));
        return session.Origin.Host;
    }

    public string LoadSessionFromCredentialLocker()
    {
        var vault = new PasswordVault();
        IReadOnlyList<PasswordCredential> credentials;
        try
        {
            credentials = vault.FindAllByResource(CredentialResource);
        }
        catch (Exception)
        {
            throw new InvalidOperationException(
                "No saved PlayFab authentication exists. Open Progress & profile and choose Authenticate first.");
        }

        PasswordCredential credential = credentials.First();
        credential.RetrievePassword();
        lock (_sessionGate)
        {
            _session = new PlayFabSession(
                new Uri($"https://{credential.UserName}"),
                credential.Password,
                DateTimeOffset.Now);
        }
        SessionChanged?.Invoke();
        return credential.UserName;
    }

    public void DeleteSavedSession()
    {
        RemoveSavedCredentials(new PasswordVault());
        SessionChanged?.Invoke();
    }

    private PlayFabSession GetSession()
    {
        lock (_sessionGate)
        {
            return _session
                ?? throw new InvalidOperationException(
                    "No PlayFab authentication is available. Open Progress & profile and choose Authenticate first.");
        }
    }

    private static void RemoveSavedCredentials(PasswordVault vault)
    {
        try
        {
            foreach (PasswordCredential credential in vault.FindAllByResource(CredentialResource))
                vault.Remove(credential);
        }
        catch (Exception)
        {
            // PasswordVault throws when no matching resource exists.
        }
    }

    private static async Task<string> SendClientRequestAsync(
        PlayFabSession session,
        string path,
        object body,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(session.Origin, path))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("X-Authorization", session.Ticket);

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        string responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string detail = TryGetPlayFabError(responseText)
                ?? response.ReasonPhrase
                ?? "Unknown PlayFab error";
            throw new InvalidOperationException(
                $"PlayFab rejected the request ({(int)response.StatusCode}): {detail}");
        }

        return responseText;
    }

    private static int? TryGetDataVersion(string responseText)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(responseText);
            if (json.RootElement.TryGetProperty("data", out JsonElement data)
                && data.TryGetProperty("DataVersion", out JsonElement version))
            {
                return version.GetInt32();
            }
        }
        catch (JsonException)
        {
            // Version is informational.
        }

        return null;
    }

    private void CaptureSession(Uri requestUri, string? ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket)) return;

        bool changed;
        lock (_sessionGate)
        {
            changed = _session?.Ticket != ticket
                || _session.Origin.AbsoluteUri != requestUri.GetLeftPart(UriPartial.Authority) + "/";
            _session = new PlayFabSession(
                new Uri(requestUri.GetLeftPart(UriPartial.Authority)),
                ticket,
                DateTimeOffset.Now);
        }

        if (changed) SessionChanged?.Invoke();
    }

    private static string? TryGetPlayFabError(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("errorMessage", out JsonElement message))
                return message.GetString();
            if (root.TryGetProperty("error", out JsonElement error))
                return error.GetString();
        }
        catch (JsonException)
        {
            // Fall back to the HTTP reason phrase.
        }

        return null;
    }

    private static bool TryReadSave(byte[] bytes, out HaloSave save)
    {
        try
        {
            save = HaloSave.LoadBytes(bytes);
            if (save.Envelope.Kind == SaveSourceKind.Json) return true;
        }
        catch (Exception)
        {
            // Ordinary PlayFab JSON is expected not to contain a save.
        }

        save = null!;
        return false;
    }

    private static bool IsPlayFabHost(string host)
        => host.Equals("playfabapi.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".playfabapi.com", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        Stop();
        lock (_sessionGate) _session = null;
        GC.SuppressFinalize(this);
    }

    private sealed record PlayFabSession(Uri Origin, string Ticket, DateTimeOffset CapturedAt);
}
