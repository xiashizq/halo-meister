using HaloMeister.App.Localization;
namespace HaloMeister.App.Services;

public sealed class SoftCeilingService
{
    private readonly ScriptingBridgeService _bridge =
        ScriptingBridgeService.Current;

    public ScriptingBridgeStatus BridgeStatus => _bridge.GetStatus();

    public async Task<bool> ReadDisabledAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureBridgeReady();
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.BlamSoftCeilingRead,
            "read",
            TimeSpan.FromSeconds(15),
            cancellationToken);
        return Parse(result);
    }

    public async Task<bool> SetDisabledAsync(
        bool disabled,
        CancellationToken cancellationToken = default)
    {
        EnsureBridgeReady();
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.BlamSoftCeilingWrite,
            disabled ? "1" : "0",
            TimeSpan.FromSeconds(15),
            cancellationToken);
        bool actual = Parse(result);
        if (actual != disabled)
            throw new IOException(
                "The game reported a different physical-wall state than requested.");
        return actual;
    }

    private static bool Parse(ScriptExecutionResult result)
    {
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);
        return result.Message switch
        {
            "soft_ceilings_disable=0" => false,
            "soft_ceilings_disable=1" => true,
            _ => throw new InvalidDataException(
                "The native bridge returned an invalid soft-ceiling state."),
        };
    }

    private void EnsureBridgeReady()
    {
        ScriptingBridgeStatus status = BridgeStatus;
        if (!status.IsRuntimeReady)
            throw new InvalidOperationException(
                L.Get("bridge.error_not_responding"));
        if (status.IsStale)
            throw new InvalidOperationException(status.Summary);
    }
}
