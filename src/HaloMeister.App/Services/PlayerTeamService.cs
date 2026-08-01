using HaloMeister.App.Localization;
namespace HaloMeister.App.Services;

public sealed record PlayerTeamOption(string Label, int Value, string Description);

public sealed record PlayerTeamState(
    PlayerTeamOption Selected,
    bool HasSnapshot);

public sealed class PlayerTeamService
{
    private readonly ScriptingBridgeService _bridge =
        ScriptingBridgeService.Current;

    public static IReadOnlyList<PlayerTeamOption> Options { get; } =
    [
        new("Original allegiance", -1,
            "Restore the team the current player body had before switching."),
        new("Player", 1,
            "The standard controlled-player campaign team."),
        new("Human / UNSC", 2,
            "Human allies treat the player as friendly."),
        new("Covenant", 3,
            "Covenant AI treat the player as one of their own."),
        new("Brute", 4,
            "Brute-team AI treat the player as friendly."),
        new("Mule", 5,
            "Uses the engine's auxiliary Mule campaign team."),
        new("Covenant player", 7,
            "Uses the campaign's dedicated Covenant-player team."),
        new("Flood", 8,
            "Flood AI treat the player as one of their own."),
        new("Sentinel", 9,
            "Sentinel AI treat the player as one of their own."),
        new("Heretic", 10,
            "Heretic-team AI treat the player as friendly."),
        new("Prophet", 11,
            "Prophet-team AI treat the player as friendly."),
        new("Guilty", 12,
            "Uses the Guilty Spark campaign team."),
        new("Hostile to all", 13,
            "Makes the player hostile to every campaign team."),
    ];

    public ScriptingBridgeStatus BridgeStatus => _bridge.GetStatus();

    public async Task<PlayerTeamState> ReadAsync(
        CancellationToken cancellationToken = default) =>
        Parse(await ExecuteAsync("read", cancellationToken));

    public async Task<PlayerTeamState> SetAsync(
        int team,
        CancellationToken cancellationToken = default)
    {
        if (!Options.Any(option => option.Value == team) || team < 0)
            throw new ArgumentOutOfRangeException(nameof(team));
        return Parse(await ExecuteAsync(
            team.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken));
    }

    public async Task<PlayerTeamState> RestoreAsync(
        CancellationToken cancellationToken = default) =>
        Parse(await ExecuteAsync("restore", cancellationToken));

    private async Task<string> ExecuteAsync(
        string action,
        CancellationToken cancellationToken)
    {
        EnsureBridgeReady();
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.PlayerTeam,
            action,
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);
        return result.Message;
    }

    private static PlayerTeamState Parse(string message)
    {
        Dictionary<string, string> fields = message
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1]);
        if (!fields.TryGetValue("team", out string? teamText) ||
            !int.TryParse(
                teamText,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out int value) ||
            !fields.TryGetValue("override", out string? overrideText) ||
            !int.TryParse(
                overrideText,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out _) ||
            !fields.TryGetValue("snapshot", out string? snapshotText) ||
            snapshotText is not ("0" or "1"))
        {
            throw new InvalidDataException(
                "The native player-team hook returned an invalid result.");
        }

        PlayerTeamOption selected = Options.FirstOrDefault(
                option => option.Value == value)
            ?? new PlayerTeamOption(
                $"Unknown team ({value})",
                value,
                "The game returned a campaign team that Halo Meister does not recognize.");
        return new PlayerTeamState(selected, snapshotText == "1");
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
