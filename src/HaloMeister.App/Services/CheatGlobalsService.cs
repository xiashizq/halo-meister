using HaloMeister.App.Localization;

namespace HaloMeister.App.Services;

public sealed record CheatGlobalDefinition(
    string Name,
    string DisplayNameKey,
    string DescriptionKey);

public sealed class CheatGlobalItem
{
    public required CheatGlobalDefinition Definition { get; init; }
    public bool IsEnabled { get; set; }

    public string Name => Definition.Name;
    public string DisplayName => L.Get(Definition.DisplayNameKey);
    public string Description => L.Get(Definition.DescriptionKey);
}

public sealed class CheatGlobalsService
{
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;

    public static IReadOnlyList<CheatGlobalDefinition> Catalog { get; } =
    [
        new(
            "infinite_health",
            "cheat_globals.infinite_health",
            "cheat_globals.infinite_health_desc"),
        new(
            "infinite_ammo",
            "cheat_globals.infinite_ammo",
            "cheat_globals.infinite_ammo_desc"),
        new(
            "jetpack",
            "cheat_globals.jetpack",
            "cheat_globals.jetpack_desc"),
    ];

    public ScriptingBridgeStatus BridgeStatus => _bridge.GetStatus();

    public async Task<IReadOnlyList<CheatGlobalItem>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureBridgeReady();
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.BlamCheatGlobalsRead,
            "read",
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);

        Dictionary<string, bool> values = ParseValues(result.Message);
        return Catalog.Select(definition => new CheatGlobalItem
        {
            Definition = definition,
            IsEnabled = values.TryGetValue(definition.Name, out bool enabled) && enabled,
        }).ToArray();
    }

    public async Task SetAsync(
        string name,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (!Catalog.Any(item => item.Name == name))
            throw new ArgumentOutOfRangeException(nameof(name));
        EnsureBridgeReady();
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.BlamCheatGlobalWrite,
            $"{name}={(enabled ? 1 : 0)}",
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);

        Dictionary<string, bool> values = ParseValues(result.Message);
        if (!values.TryGetValue(name, out bool actual) || actual != enabled)
            throw new InvalidDataException(L.Get("cheat_globals.error_not_retained"));
    }

    private static Dictionary<string, bool> ParseValues(string message)
    {
        var values = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (string line in message.Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf('=');
            if (separator <= 0 || separator + 2 != line.Length ||
                (line[^1] != '0' && line[^1] != '1'))
            {
                throw new InvalidDataException(
                    L.Get("cheat_globals.error_invalid_hook_value"));
            }
            values[line[..separator]] = line[^1] == '1';
        }
        return values;
    }

    private void EnsureBridgeReady()
    {
        ScriptingBridgeStatus status = BridgeStatus;
        if (!status.IsRuntimeReady)
            throw new InvalidOperationException(L.Get("bridge.error_not_responding_restart"));
        if (status.IsStale)
            throw new InvalidOperationException(status.Summary);
    }
}
