using System.Globalization;
using HaloMeister.App.Localization;

namespace HaloMeister.App.Services;

public sealed record PlayerCoordinates(float X, float Y, float Z)
{
    public string ToPayload() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{X:R},{Y:R},{Z:R}");
}

public sealed class PlayerToolsService
{
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;

    public ScriptingBridgeStatus BridgeStatus => _bridge.GetStatus();

    public async Task<PlayerCoordinates> ReadPositionAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureBridgeReady();
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.PlayerPosition,
            "current",
            cancellationToken: cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);

        const string marker = "Return value: ";
        int offset = result.Message.IndexOf(marker, StringComparison.Ordinal);
        string[] values = offset < 0
            ? []
            : result.Message[(offset + marker.Length)..]
                .Trim()
                .Split(',', StringSplitOptions.TrimEntries);
        if (values.Length != 3 ||
            !float.TryParse(
                values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
            !float.TryParse(
                values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
            !float.TryParse(
                values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) ||
            !float.IsFinite(x) ||
            !float.IsFinite(y) ||
            !float.IsFinite(z))
        {
            throw new InvalidDataException(
                "The game returned an invalid player position. Resume a campaign checkpoint and try again.");
        }
        return new PlayerCoordinates(x, y, z);
    }

    public async Task TeleportAsync(
        PlayerCoordinates destination,
        CancellationToken cancellationToken = default)
    {
        EnsureBridgeReady();
        if (!float.IsFinite(destination.X) ||
            !float.IsFinite(destination.Y) ||
            !float.IsFinite(destination.Z))
        {
            throw new ArgumentOutOfRangeException(
                nameof(destination),
                "Teleport coordinates must be finite numbers.");
        }

        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.PlayerTeleport,
            destination.ToPayload(),
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);
    }

    public async Task SetNoClipAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        EnsureBridgeReady();
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.PlayerNoClip,
            enabled ? "1" : "0",
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);
    }

    public async Task SetInputSuppressedAsync(
        bool suppressed,
        CancellationToken cancellationToken = default)
    {
        EnsureBridgeReady();
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.PlayerInput,
            suppressed ? "suppress" : "restore",
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);
    }

    public async Task<int> ReadActivePlayerTagIndexAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureBridgeReady();
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.PlayerUnitTagRead,
            "read",
            cancellationToken: cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);

        const string marker = "Return value: ";
        int offset = result.Message.IndexOf(marker, StringComparison.Ordinal);
        string value = offset < 0
            ? ""
            : result.Message[(offset + marker.Length)..].Trim();
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int tagIndex) ||
            tagIndex is < 0 or > ushort.MaxValue)
        {
            throw new InvalidDataException(
                "The game returned an invalid controlled-player unit tag index.");
        }
        return tagIndex;
    }

    private void EnsureBridgeReady()
    {
        ScriptingBridgeStatus status = BridgeStatus;
        if (!status.IsRuntimeReady)
        {
            throw new InvalidOperationException(
                L.Get("bridge.error_not_responding_restart"));
        }
        if (status.IsStale)
            throw new InvalidOperationException(status.Summary);
    }
}
