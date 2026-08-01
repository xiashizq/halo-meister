using HaloMeister.App.Localization;
namespace HaloMeister.App.Services;

public sealed record RuntimeBoundaryState(
    int TotalCount,
    int DisabledCount,
    bool CanRestore)
{
    public int ActiveCount => Math.Max(0, TotalCount - DisabledCount);
    public bool IsDisabled => TotalCount > 0 && DisabledCount == TotalCount;
    public string StatusDisplay =>
        $"{DisabledCount:N0} of {TotalCount:N0} runtime kill/OOB triggers disabled";
}

public sealed class RuntimeBoundaryService
{
    private readonly ScriptingBridgeService _bridge =
        ScriptingBridgeService.Current;

    public ScriptingBridgeStatus BridgeStatus => _bridge.GetStatus();

    public string InstallOrRepairBridge() => _bridge.InstallOrUpdateBridge();

    public Task<RuntimeBoundaryState> ReadAsync(
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            ScriptLanguage.BlamBoundariesRead,
            "read",
            cancellationToken);

    public Task<RuntimeBoundaryState> DisableAsync(
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            ScriptLanguage.BlamBoundariesDisable,
            "disable",
            cancellationToken);

    public Task<RuntimeBoundaryState> RestoreAsync(
        CancellationToken cancellationToken = default)
        => ExecuteAsync(
            ScriptLanguage.BlamBoundariesRestore,
            "restore",
            cancellationToken);

    private async Task<RuntimeBoundaryState> ExecuteAsync(
        ScriptLanguage language,
        string payload,
        CancellationToken cancellationToken)
    {
        EnsureBridgeReady();
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            language,
            payload,
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);

        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string line in result.Message.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf('=');
            if (separator <= 0 ||
                !int.TryParse(line[(separator + 1)..], out int value))
            {
                throw new InvalidDataException(
                    "The native bridge returned invalid runtime-boundary state.");
            }
            values[line[..separator]] = value;
        }
        if (!values.TryGetValue("total", out int total) ||
            !values.TryGetValue("disabled", out int disabled) ||
            !values.TryGetValue("snapshot", out int snapshot) ||
            total is <= 0 or > 1024 ||
            disabled < 0 || disabled > total ||
            snapshot is < 0 or > 1)
        {
            throw new InvalidDataException(
                "The native bridge returned incomplete runtime-boundary state.");
        }
        return new RuntimeBoundaryState(total, disabled, snapshot == 1);
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
