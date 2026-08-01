using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using HaloMeister.App.Localization;
using HaloMeister.App.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HaloMeister.App.Services;

public sealed record RemoteControlSnapshot(
    bool IsRunning,
    string? PairingUrl,
    IReadOnlyList<string> PairingUrls,
    string Summary);

public sealed class RemoteControlService : IAsyncDisposable
{
    public const int Port = 48731;
    private const string AssetsFolder = "RemoteControl";
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly RuntimeTagMemoryService _game = RuntimeTagMemoryService.Current;
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private readonly WeaponLoaderService _weapons = new();
    private readonly EnemySpawnerService _spawner = new();
    private readonly LiveSkullsService _skulls = new();
    private readonly PlayerToolsService _player = new();
    private readonly int _listenPort;
    private WebApplication? _application;
    private string? _pairingToken;
    private IReadOnlyList<string> _pairingUrls = [];

    private RemoteControlService()
        : this(Port)
    {
    }

    internal RemoteControlService(int listenPort)
    {
        if (listenPort is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(listenPort));
        _listenPort = listenPort;
    }

    public static RemoteControlService Current { get; } = new();

    public event EventHandler? StateChanged;

    public RemoteControlSnapshot Snapshot
    {
        get
        {
            bool running = _application is not null;
            string? primary = _pairingUrls.FirstOrDefault();
            return new RemoteControlSnapshot(
                running,
                primary,
                _pairingUrls,
                running
                    ? primary is null
                        ? "Remote control is running, but no LAN address was found."
                        : "Remote control is available to paired devices on this local network."
                    : "Remote control is off.");
        }
    }

    public async Task<RemoteControlSnapshot> StartAsync(
        CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_application is not null)
                return Snapshot;

            _pairingToken = CreatePairingToken();
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(
                new WebApplicationOptions
                {
                    Args = [],
                    ApplicationName = typeof(RemoteControlService).Assembly.FullName,
                    ContentRootPath = AppContext.BaseDirectory,
                });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(_listenPort));

            WebApplication app = builder.Build();
            ConfigurePipeline(app);

            try
            {
                await app.StartAsync(cancellationToken).ConfigureAwait(false);
                _pairingUrls = BuildPairingUrls(_listenPort, _pairingToken);
                _application = app;
            }
            catch (Exception ex)
            {
                await app.DisposeAsync().ConfigureAwait(false);
                _pairingToken = null;
                _pairingUrls = [];
                if (IsAddressInUse(ex))
                {
                    throw new InvalidOperationException(
                        $"TCP port {_listenPort} is already in use. Stop the phone remote in another Halo Meister instance, then try again.",
                        ex);
                }
                throw;
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
            return Snapshot;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WebApplication? app = _application;
            _application = null;
            _pairingToken = null;
            _pairingUrls = [];
            if (app is not null)
            {
                try
                {
                    await app.StopAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await app.DisposeAsync().ConfigureAwait(false);
                }
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void StopForShutdown(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        Task stopTask = Task.Run(async () =>
        {
            try
            {
                await StopAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch
            {
                // Process shutdown continues even if Kestrel is already stopping.
            }
        });

        try
        {
            stopTask.Wait(timeout);
        }
        catch
        {
            // Never keep the WinUI close path alive for remote-server cleanup.
        }
    }

    private void ConfigurePipeline(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";

            if (!IsLocalClient(context.Connection.RemoteIpAddress))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Halo Meister phone remote accepts local-network devices only.",
                });
                return;
            }

            if (context.Request.Path.StartsWithSegments("/api") &&
                !IsAuthorized(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "This phone is not paired with Halo Meister.",
                });
                return;
            }

            await next(context);
        });

        app.MapGet("/", () => StaticFile("index.html", "text/html; charset=utf-8"));
        app.MapGet("/app.css", () => StaticFile("app.css", "text/css; charset=utf-8"));
        app.MapGet("/app.js", () => StaticFile(
            "app.js",
            "text/javascript; charset=utf-8"));
        app.MapGet("/manifest.webmanifest", () => StaticFile(
            "manifest.webmanifest",
            "application/manifest+json"));
        app.MapGet("/i18n/{lang}.json", (string lang) => LocaleFile(lang));
        app.MapGet("/api/locale", () => Results.Ok(new
        {
            language = LocalizationService.Current.Language,
        }));

        app.MapGet("/api/status", () => Api(async () =>
        {
            ScriptingBridgeStatus bridge = _bridge.GetStatus();
            return Results.Ok(new
            {
                gameConnected = _game.IsConnected,
                processId = _game.ProcessId,
                buildProfile = _game.BuildProfileId,
                bridgeReady = bridge.IsRuntimeReady && !bridge.IsStale,
                bridgeVersion = bridge.RunningVersion,
                bridgeSummary = bridge.Summary,
                language = LocalizationService.Current.Language,
            });
        }, exclusive: false));

        app.MapPost("/api/connect", () => Api(async () =>
        {
            await Task.Run(_game.Connect);
            return Results.Ok(new
            {
                processId = _game.ProcessId,
                buildProfile = _game.BuildProfileId,
                message = L.Format("remote.connected_pid", _game.ProcessId),
            });
        }));

        app.MapGet("/api/weapons", () => Api(async () =>
        {
            IReadOnlyList<LoadableWeapon> weapons = await Task.Run(_weapons.Connect);
            return Results.Ok(weapons.Select(weapon => new
            {
                id = weapon.Tag.Index,
                name = weapon.Name,
                path = weapon.TagPath,
            }));
        }));

        app.MapPost("/api/weapons/{id:int}/load", (int id) => Api(async () =>
        {
            IReadOnlyList<LoadableWeapon> weapons = await Task.Run(_weapons.Connect);
            LoadableWeapon selected = weapons.SingleOrDefault(weapon => weapon.Tag.Index == id)
                ?? throw new InvalidOperationException(
                    "That weapon is no longer loaded in this mission.");
            ScriptExecutionResult result = await _weapons.LoadAsync(selected);
            return Results.Ok(new { message = result.Message });
        }));

        app.MapGet("/api/enemies", () => Api(async () =>
        {
            SpawnerCatalog catalog = await Task.Run(_spawner.Connect);
            return Results.Ok(catalog.Characters.Select(character => new
            {
                id = character.CharacterTag.Index,
                name = character.DisplayName,
                path = character.TagPath,
                category = character.Category,
                variants = character.Variants.Select(variant => new
                {
                    id = variant.VariantBlockIndex,
                    name = variant.Name,
                }),
            }));
        }));

        app.MapPost(
            "/api/enemies/{id:int}/spawn",
            (int id, SpawnRequest request) => Api(async () =>
            {
                SpawnerCatalog catalog = await Task.Run(_spawner.Connect);
                EnemySpawnChoice choice = catalog.Characters.SingleOrDefault(
                        item => item.CharacterTag.Index == id)
                    ?? throw new InvalidOperationException(
                        "That enemy is no longer loaded in this mission.");
                SpawnVariantChoice variant = choice.Variants.SingleOrDefault(
                        item => item.VariantBlockIndex == request.VariantId)
                    ?? throw new InvalidOperationException(
                        "That enemy variant is no longer available.");
                ScriptExecutionResult result = string.Equals(
                    request.Mode,
                    "team",
                    StringComparison.OrdinalIgnoreCase)
                    ? await _spawner.SpawnTeamAsync(choice, variant)
                    : await _spawner.SpawnAsync(choice, variant);
                return Results.Ok(new { message = result.Message });
            }));

        app.MapGet("/api/skulls", () => Api(async () =>
        {
            IReadOnlyList<LiveSkullItem> skulls = await _skulls.ReadAsync();
            return Results.Ok(skulls.Select(skull => new
            {
                id = skull.Name,
                name = skull.DisplayName,
                enabled = skull.IsEnabled,
            }));
        }));

        app.MapPut(
            "/api/skulls/{name}",
            (string name, ToggleRequest request) => Api(async () =>
            {
                await _skulls.SetAsync(name, request.Enabled);
                return Results.Ok(new
                {
                    message = $"{name} is now {(request.Enabled ? "on" : "off")}.",
                });
            }));

        app.MapGet("/api/player", () => Api(async () =>
        {
            PlayerCoordinates position = await _player.ReadPositionAsync();
            return Results.Ok(position);
        }));

        app.MapPost(
            "/api/player/teleport",
            (TeleportRequest request) => Api(async () =>
            {
                var destination = new PlayerCoordinates(request.X, request.Y, request.Z);
                await _player.TeleportAsync(destination);
                return Results.Ok(new
                {
                    message = $"Teleported to {destination.ToPayload()}.",
                });
            }));
    }

    private async Task<IResult> Api(
        Func<Task<IResult>> action,
        bool exclusive = true)
    {
        if (exclusive)
            await _operationGate.WaitAsync();
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            return Results.Problem(
                "The operation was cancelled.",
                statusCode: StatusCodes.Status408RequestTimeout);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                ex.Message,
                statusCode: StatusCodes.Status409Conflict);
        }
        finally
        {
            if (exclusive)
                _operationGate.Release();
        }
    }

    private bool IsAuthorized(HttpRequest request)
    {
        string? token = _pairingToken;
        string header = request.Headers.Authorization.ToString();
        if (token is null ||
            !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string candidate = header["Bearer ".Length..].Trim();
        byte[] expected = Encoding.UTF8.GetBytes(token);
        byte[] actual = Encoding.UTF8.GetBytes(candidate);
        return actual.Length == expected.Length &&
               CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static IResult StaticFile(string fileName, string contentType)
    {
        string root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            AssetsFolder));
        string path = Path.GetFullPath(Path.Combine(root, fileName));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !File.Exists(path))
        {
            return Results.NotFound();
        }
        return Results.File(path, contentType);
    }

    private static IResult LocaleFile(string lang)
    {
        string normalized = lang.Trim();
        if (normalized.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            normalized = LocalizationService.ChineseSimplified;
        else if (normalized.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            normalized = LocalizationService.Japanese;
        else if (normalized.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
            normalized = LocalizationService.Korean;
        else
            normalized = LocalizationService.English;

        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Assets", "i18n"));
        string path = Path.GetFullPath(Path.Combine(root, $"{normalized}.json"));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !File.Exists(path))
        {
            return Results.NotFound();
        }

        return Results.File(path, "application/json; charset=utf-8");
    }

    private static IReadOnlyList<string> BuildPairingUrls(int port, string token)
    {
        string escaped = Uri.EscapeDataString(token);
        string lang = Uri.EscapeDataString(LocalizationService.Current.Language);
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter =>
                adapter.OperationalStatus == OperationalStatus.Up &&
                adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses.Select(
                address => new
                {
                    address.Address,
                    IsTailscale =
                        adapter.Name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
                        adapter.Description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
                        IsTailscaleAddress(address.Address),
                }))
            .Where(item =>
                item.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(item.Address) &&
                !item.Address.Equals(IPAddress.Any) &&
                (IsPrivateAddress(item.Address) || item.IsTailscale))
            .GroupBy(item => item.Address)
            .Select(group => group.OrderByDescending(item => item.IsTailscale).First())
            .OrderByDescending(item => item.IsTailscale)
            .ThenBy(item => item.Address.ToString(), StringComparer.Ordinal)
            .Select(item => $"http://{item.Address}:{port}/?pair={escaped}&lang={lang}")
            .ToArray();
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
               bytes[0] == 192 && bytes[1] == 168;
    }

    private static bool IsLocalClient(IPAddress? address)
    {
        if (address is null)
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return IPAddress.IsLoopback(address) ||
               address.AddressFamily == AddressFamily.InterNetwork &&
               (IsPrivateAddress(address) || IsTailscaleAddress(address));
    }

    private static bool IsTailscaleAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }

    private static bool IsAddressInUse(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException
                {
                    SocketErrorCode: SocketError.AddressAlreadyInUse,
                })
            {
                return true;
            }
        }
        return false;
    }

    private static string CreatePairingToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _weapons.Dispose();
        _spawner.Dispose();
        _operationGate.Dispose();
        _lifecycleGate.Dispose();
    }

    private sealed record SpawnRequest(int VariantId, string? Mode);
    private sealed record ToggleRequest(bool Enabled);
    private sealed record TeleportRequest(float X, float Y, float Z);
}
