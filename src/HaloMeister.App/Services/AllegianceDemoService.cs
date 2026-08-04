using System.Globalization;
using System.Text.RegularExpressions;
using HaloMeister.App.Localization;

namespace HaloMeister.App.Services;

public sealed record AllegianceDemoSpawnResult(
    ScriptExecutionResult SpawnResult,
    int? ActorDatum);

public sealed record ObjectTeamResult(
    int UnitDatum,
    int ActorDatum,
    int Team,
    string RawMessage);

/// <summary>
/// Demo helpers for the proper campaign-team path: spawn AI, then apply
/// <c>ai_object_set_team</c> + the object allegiance table.
/// Does not patch scenario squad teams, scan actor combat/objective memory,
/// or touch the global <c>ai_allegiance</c> matrix during unit apply.
/// </summary>
public sealed class AllegianceDemoService
{
    private static readonly Regex ActorDatumPattern = new(
        @"first actor datum 0x([0-9A-Fa-f]{8})",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly ScriptingBridgeService _bridge =
        ScriptingBridgeService.Current;
    private readonly EnemySpawnerService _spawner = new();

    public ScriptingBridgeStatus BridgeStatus => _bridge.GetStatus();

    public static IReadOnlyList<PlayerTeamOption> TeamOptions { get; } =
        PlayerTeamService.Options
            .Where(option => option.Value >= 0)
            .ToArray();

    public SpawnerCatalog Connect() => _spawner.Connect();

    public async Task<AllegianceDemoSpawnResult> SpawnAsync(
        EnemySpawnChoice character,
        SpawnVariantChoice variant,
        CancellationToken cancellationToken = default)
    {
        // Borrowed hostile squads often point at a combat objective. Temporarily
        // write authored <none> (-1) into that squad's initial objective/task
        // around actor_new — same value Sapien shows as <none>.
        ScriptExecutionResult result = await _spawner.SpawnGroupAsync(
            character,
            variant,
            count: 1,
            followPlayer: false,
            clearSquadObjective: true,
            cancellationToken: cancellationToken);
        int? actor = TryParseActorDatum(result.Message);
        return new AllegianceDemoSpawnResult(result, actor);
    }

    public async Task<ObjectTeamResult> ApplyObjectTeamAsync(
        int team,
        int? actorDatum = null,
        CancellationToken cancellationToken = default)
    {
        if (TeamOptions.All(option => option.Value != team))
            throw new ArgumentOutOfRangeException(nameof(team));

        // Minimal payload: target,team. Lua may append player unit; native ignores it.
        string payload = actorDatum is int actor
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"a{(uint)actor:X8},{team}")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"last,{team}");

        EnsureBridgeReady();
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.ObjectTeam,
            payload,
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);
        return ParseObjectTeam(result.Message);
    }

    public async Task<ScriptExecutionResult> SubmitAllegianceAsync(
        int team,
        bool breakAllegiance,
        CancellationToken cancellationToken = default)
    {
        string teamName = HaloScriptTeamName(team);
        string verb = breakAllegiance ? "ai_allegiance_break" : "ai_allegiance";
        string expression =
            $"{verb} player {teamName}\n{verb} {teamName} player";
        EnsureBridgeReady();
        return await _bridge.ExecuteAsync(
            ScriptLanguage.HaloScript,
            expression,
            TimeSpan.FromSeconds(10),
            cancellationToken);
    }

    public static string HaloScriptTeamName(int team) =>
        team switch
        {
            0 => "default",
            1 => "player",
            2 => "human",
            3 => "covenant",
            4 => "brute",
            5 => "mule",
            6 => "spare",
            7 => "covenant_player",
            8 => "flood",
            9 => "sentinel",
            10 => "heretic",
            11 => "prophet",
            12 => "guilty",
            13 => "berserk_hostile_to_all",
            _ => throw new ArgumentOutOfRangeException(nameof(team)),
        };

    private static int? TryParseActorDatum(string message)
    {
        Match match = ActorDatumPattern.Match(message);
        if (!match.Success)
            return null;
        if (!uint.TryParse(
                match.Groups[1].Value,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out uint datum))
            return null;
        return unchecked((int)datum);
    }

    private static ObjectTeamResult ParseObjectTeam(string message)
    {
        Dictionary<string, string> fields = message
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => parts[0],
                parts => parts[1],
                StringComparer.OrdinalIgnoreCase);
        if (!fields.TryGetValue("unit", out string? unitText) ||
            !fields.TryGetValue("actor", out string? actorText) ||
            !fields.TryGetValue("team", out string? teamText) ||
            !TryParseHexDatum(unitText, out int unit) ||
            !TryParseHexDatum(actorText, out int actor) ||
            !int.TryParse(
                teamText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int team))
        {
            throw new InvalidDataException(
                "The native object-team hook returned an invalid result.");
        }

        return new ObjectTeamResult(unit, actor, team, message);
    }

    private static bool TryParseHexDatum(string text, out int value)
    {
        value = 0;
        ReadOnlySpan<char> span = text.AsSpan();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            span = span[2..];
        if (!uint.TryParse(
                span,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out uint datum))
            return false;
        value = unchecked((int)datum);
        return true;
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
        if (status.RunningVersion is < 96)
        {
            throw new InvalidOperationException(
                L.Get("allegiance_demo.requires_bridge_v96"));
        }
    }
}
