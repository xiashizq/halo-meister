using System.Globalization;
using System.Text.RegularExpressions;
using HaloMeister.App.Localization;

namespace HaloMeister.App.Services;

public sealed record AllegianceDemoSpawnResult(
    ScriptExecutionResult SpawnResult,
    int? ActorDatum,
    SpawnScaffoldDiagnosis? ScaffoldDiagnosis);

public sealed record AllegianceRosterEntry(
    EnemySpawnChoice Character,
    SpawnVariantChoice Variant,
    int Team,
    int Quantity,
    AiWeaponChoice? Weapon,
    WeaponModelVariant? WeaponVariant);

public sealed record AllegianceRosterSpawnResult(
    ScriptExecutionResult SpawnResult,
    int Created,
    int? LastActor,
    bool AnyHostileFallback);

public sealed record ObjectTeamResult(
    int UnitDatum,
    int ActorDatum,
    int Team,
    string RawMessage);

/// <summary>
/// Friend/foe demo: prefer a matching scaffold squad, birth as player (1) or
/// covenant (3). Friendlies on the dedicated <c>hm_ally</c> scaffold join the
/// player fireteam and follow; borrowed mission squads stay on the player team
/// but do not. Hostiles do not follow. Optional post reinforce via
/// <c>ai_object_set_team</c> + the object allegiance table. Spawned squads get
/// a one-shot combat wake; they are not re-pulsed into berserk afterwards.
/// </summary>
public sealed class AllegianceDemoService
{
    private static readonly Regex ActorDatumPattern = new(
        @"first actor datum 0x([0-9A-Fa-f]{8})",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ActorDatumsListPattern = new(
        @"actor datums\s+((?:0x[0-9A-Fa-f]{8}\s*,?\s*)+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly ScriptingBridgeService _bridge =
        ScriptingBridgeService.Current;
    private readonly EnemySpawnerService _spawner = new();
    private readonly object _combatLock = new();
    private readonly List<TrackedBot> _trackedBots = [];
    private AllegianceBotRecallSettings _recallSettings =
        AllegianceBotRecallSettings.Load();

    private readonly record struct TrackedBot(int ActorDatum, bool Friendly);
    private readonly record struct WorldPoint(float X, float Y, float Z);

    private readonly record struct PlayerPose(
        float X,
        float Y,
        float Z,
        float ForwardX,
        float ForwardY);

    public sealed record BotRecallResult(
        int Considered,
        int Teleported,
        int Failed);

    public ScriptingBridgeStatus BridgeStatus => _bridge.GetStatus();

    public int TrackedBotCount
    {
        get
        {
            lock (_combatLock)
                return _trackedBots.Count;
        }
    }

    public AllegianceBotRecallSettings RecallSettings
    {
        get
        {
            lock (_combatLock)
                return AllegianceBotRecallSettings.Clamp(_recallSettings);
        }
    }

    /// <summary>Friendly = player (1). Human = UNSC allies (2). Hostile = covenant (3).</summary>
    public const int FriendlyTeam = 1;
    public const int HumanTeam = 2;
    public const int HostileTeam = 3;

    public void ApplyRecallSettings(AllegianceBotRecallSettings settings)
    {
        AllegianceBotRecallSettings clamped =
            AllegianceBotRecallSettings.Clamp(settings);
        clamped.Save();
        lock (_combatLock)
            _recallSettings = clamped;
    }

    /// <summary>
    /// True when a forced recall can run (tracked bots exist for the
    /// current include-hostiles setting).
    /// </summary>
    public bool HasRecallTargets
    {
        get
        {
            AllegianceBotRecallSettings settings = RecallSettings;
            lock (_combatLock)
            {
                return _trackedBots.Any(
                    bot => bot.Friendly || settings.IncludeHostiles);
            }
        }
    }

    /// <summary>
    /// Halo 10-foot units along the player's right (negative = left).
    /// Slot 0 is the player. Extra spawn batches stay beside the player
    /// instead of metres ahead.
    /// </summary>
    public static (float Right, float Forward) BotFormationOffset(int slot)
    {
        if (slot <= 0)
            return (0f, 0f);
        float distance = ((slot + 1) / 2) * 0.9f;
        return slot % 2 == 1 ? (-distance, 0f) : (distance, 0f);
    }

    /// <summary>
    /// World XY delta for a recalled bot. Skips the on-player slot and rotates
    /// the formation by the simulation forward so people stand to the side.
    /// </summary>
    public static (float X, float Y) RecallWorldOffset(
        int slot,
        float forwardX,
        float forwardY)
    {
        (float lateral, float ahead) = BotFormationOffset(slot + 1);
        float length = MathF.Sqrt(forwardX * forwardX + forwardY * forwardY);
        if (length < 1e-4f)
        {
            forwardX = 1f;
            forwardY = 0f;
        }
        else
        {
            forwardX /= length;
            forwardY /= length;
        }

        // Same horizontal right as basis_from_view_angles: (forward.Y, -forward.X).
        float rightX = forwardY;
        float rightY = -forwardX;
        return (
            rightX * lateral + forwardX * ahead,
            rightY * lateral + forwardY * ahead);
    }

    public static IReadOnlyList<PlayerTeamOption> CreateTeamOptions() =>
    [
        new(
            L.Get("allegiance_demo.stance_friendly"),
            FriendlyTeam,
            L.Get("allegiance_demo.stance_friendly_desc")),
        new(
            L.Get("allegiance_demo.stance_hostile"),
            HostileTeam,
            L.Get("allegiance_demo.stance_hostile_desc")),
    ];

    public SpawnerCatalog Connect() => _spawner.Connect();

    public SpawnScaffoldInventory ProbeScaffolds() =>
        _spawner.ProbeScaffoldInventory();

    public IReadOnlyList<AiWeaponChoice> GetCompatibleWeapons(
        EnemySpawnChoice character) =>
        _spawner.GetCompatibleWeapons(character);

    public IReadOnlyList<AiWeaponChoice> GetAuthoredWeapons(
        EnemySpawnChoice character) =>
        _spawner.GetAuthoredWeapons(character);

    public IReadOnlyList<WeaponModelVariant> GetWeaponVariants(AiWeaponChoice weapon) =>
        _spawner.ReadWeaponVariants(weapon);

    public string ScaffoldDiagnosisLogPath =>
        EnemySpawnerService.ScaffoldDiagnosisLogPath;

    public async Task<AllegianceDemoSpawnResult> SpawnAsync(
        EnemySpawnChoice character,
        SpawnVariantChoice variant,
        int campaignTeam = FriendlyTeam,
        int count = 1,
        float formationOffsetX = 0,
        float formationOffsetY = 0,
        AiWeaponChoice? weapon = null,
        WeaponModelVariant? weaponVariant = null,
        bool? followPlayer = null,
        CancellationToken cancellationToken = default)
    {
        if (campaignTeam is not (FriendlyTeam or HumanTeam or HostileTeam))
            throw new ArgumentOutOfRangeException(nameof(campaignTeam));
        if (count is < 1 or > 5)
            throw new ArgumentOutOfRangeException(nameof(count));

        // Friendly prefers an ally scaffold. Only dedicated hm_ally joins the
        // player fireteam so they follow; borrowed mission squads do not.
        // Hostile prefers a hostile scaffold and does not join the fireteam.
        // Native keeps squad team patched through actor_new + finalize.
        //
        // Keep initial objective/task for both stances so actor_new inherits
        // attack desire. Dedicated hm_* always keep combat objective.
        bool follow = followPlayer ?? (campaignTeam == FriendlyTeam);
        ScriptExecutionResult result = await _spawner.SpawnGroupAsync(
            character,
            variant,
            count,
            formationOffsetX,
            formationOffsetY,
            weapon,
            followPlayer: follow,
            clearSquadObjective: false,
            campaignTeam: (ushort)campaignTeam,
            weaponVariant: weaponVariant,
            cancellationToken: cancellationToken);
        IReadOnlyList<int> actors = ParseActorDatums(result.Message);
        int? actor = actors.Count > 0
            ? actors[0]
            : TryParseActorDatum(result.Message);
        if (actors.Count == 0 && actor is int loneActor)
            actors = [loneActor];
        SpawnScaffoldDiagnosis? diagnosis = _spawner.LastScaffoldDiagnosis;
        result = await WakeSpawnedSquadAsync(
            result,
            diagnosis,
            preserveFireteam: follow,
            cancellationToken);
        // AI spawn reports Confirmed (including deferred "submitted" mapped in
        // the bridge). Track bots whenever creation succeeded so recall works.
        if (IsSpawnSuccess(result.Outcome))
        {
            TrackSpawnedBots(actors, friendly: campaignTeam is FriendlyTeam or HumanTeam);
        }
        return new AllegianceDemoSpawnResult(result, actor, diagnosis);
    }

    /// <summary>
    /// Spawns the whole roster in as few native waves as possible, then wakes
    /// each squad once.
    /// </summary>
    public async Task<AllegianceRosterSpawnResult> SpawnRosterAsync(
        IReadOnlyList<AllegianceRosterEntry> roster,
        CancellationToken cancellationToken = default)
    {
        EnsureBridgeReady();
        if (roster.Count == 0)
            throw new InvalidOperationException("Add at least one squad member first.");

        var groups = new List<AiWaveGroup>();
        int slot = 0;
        foreach (AllegianceRosterEntry entry in roster)
        {
            if (entry.Team is not (FriendlyTeam or HumanTeam or HostileTeam))
                throw new ArgumentOutOfRangeException(nameof(roster));
            if (entry.Quantity is < 1 or > 50)
                throw new ArgumentOutOfRangeException(nameof(roster));
            int remaining = entry.Quantity;
            while (remaining > 0)
            {
                int batchCount = Math.Min(5, remaining);
                (float right, float forward) = BotFormationOffset(slot++);
                groups.Add(new AiWaveGroup(
                    entry.Character,
                    entry.Variant,
                    batchCount,
                    right,
                    forward,
                    entry.Weapon,
                    entry.WeaponVariant,
                    FollowPlayer: entry.Team == FriendlyTeam,
                    CampaignTeam: (ushort)entry.Team));
                remaining -= batchCount;
            }
        }

        int created = 0;
        var actors = new List<int>();
        var diagnoses = new List<SpawnScaffoldDiagnosis>();
        bool anyHostileFallback = false;
        ScriptExecutionResult? last = null;
        const int waveCap = 32;
        for (int index = 0; index < groups.Count;)
        {
            int takenActors = 0;
            int take = 0;
            while (index + take < groups.Count &&
                   takenActors + groups[index + take].Count <= waveCap)
            {
                takenActors += groups[index + take].Count;
                take++;
            }
            AiWaveGroup[] slice = groups.Skip(index).Take(take).ToArray();
            AiWaveSpawnResult wave = await _spawner.SpawnWaveAsync(
                slice,
                cancellationToken);
            last = wave.Result;
            if (!IsSpawnSuccess(wave.Result.Outcome))
            {
                return new AllegianceRosterSpawnResult(
                    wave.Result,
                    created,
                    actors.Count > 0 ? actors[^1] : null,
                    anyHostileFallback);
            }
            actors.AddRange(ParseActorDatums(wave.Result.Message));
            diagnoses.AddRange(wave.Diagnoses);
            anyHostileFallback |= wave.Diagnoses.Any(
                diagnosis => diagnosis.UsedHostileFallback);
            created += takenActors;
            index += take;
        }

        ScriptExecutionResult result = last
            ?? throw new InvalidOperationException("The roster did not spawn.");
        var wakeTargets = new List<(string SquadName, bool Friendly)>();
        for (int index = 0; index < groups.Count && index < diagnoses.Count; index++)
        {
            string name = string.IsNullOrWhiteSpace(diagnoses[index].SquadName)
                ? groups[index].FollowPlayer
                    ? EnemySpawnerService.DedicatedAllySquadName
                    : EnemySpawnerService.DedicatedHostileSquadName
                : diagnoses[index].SquadName;
            wakeTargets.Add((name, groups[index].FollowPlayer));
        }
        result = await WakeSquadsAsync(result, wakeTargets, cancellationToken);

        int cursor = 0;
        foreach (AiWaveGroup group in groups)
        {
            if (cursor >= actors.Count)
                break;
            int take = Math.Min(group.Count, actors.Count - cursor);
            TrackSpawnedBots(
                actors.GetRange(cursor, take),
                friendly: group.CampaignTeam is FriendlyTeam or HumanTeam);
            cursor += take;
        }
        return new AllegianceRosterSpawnResult(
            result,
            created,
            actors.Count > 0 ? actors[^1] : null,
            anyHostileFallback);
    }

    public void ClearCombatMaintain()
    {
        lock (_combatLock)
            _trackedBots.Clear();
    }

    private void TrackSpawnedBots(IReadOnlyList<int> actors, bool friendly)
    {
        if (actors.Count == 0)
            return;
        lock (_combatLock)
        {
            foreach (int actor in actors)
            {
                if (_trackedBots.Any(bot => bot.ActorDatum == actor))
                    continue;
                _trackedBots.Add(new TrackedBot(actor, friendly));
            }
        }
    }

    /// <summary>
    /// Manually teleport tracked BOTs near the player. Default scope is
    /// friendlies; hostiles are included when the setting is on.
    /// </summary>
    public async Task<BotRecallResult> RecallBotsAsync(
        CancellationToken cancellationToken = default)
    {
        AllegianceBotRecallSettings settings = RecallSettings;

        List<TrackedBot> candidates;
        lock (_combatLock)
        {
            candidates = _trackedBots
                .Where(bot => bot.Friendly || settings.IncludeHostiles)
                .ToList();
        }
        if (candidates.Count == 0)
            return new BotRecallResult(0, 0, 0);

        EnsureObjectBridgeReady();

        PlayerPose player = await ReadPlayerPoseAsync(cancellationToken);
        int teleported = 0;
        int failed = 0;
        var gone = new List<int>();
        int slot = 0;

        foreach (TrackedBot bot in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                (float offsetX, float offsetY) = RecallWorldOffset(
                    slot++,
                    player.ForwardX,
                    player.ForwardY);
                WorldPoint destination = new(
                    player.X + offsetX,
                    player.Y + offsetY,
                    player.Z);
                ScriptExecutionResult move = await TeleportObjectAsync(
                    bot.ActorDatum,
                    destination,
                    cancellationToken);
                if (IsSpawnSuccess(move.Outcome))
                    teleported++;
                else
                {
                    failed++;
                    if (IsForgottenActor(move.Message))
                        gone.Add(bot.ActorDatum);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                if (IsForgottenActor(ex.Message))
                    gone.Add(bot.ActorDatum);
            }
        }

        if (gone.Count > 0)
        {
            lock (_combatLock)
            {
                _trackedBots.RemoveAll(bot => gone.Contains(bot.ActorDatum));
            }
        }

        return new BotRecallResult(candidates.Count, teleported, failed);
    }

    private static bool IsSpawnSuccess(ScriptOutcome outcome) =>
        outcome is ScriptOutcome.Confirmed or ScriptOutcome.Submitted;

    /// <summary>
    /// A missed landing or a bridge timeout leaves the actor tracked so the
    /// next recall can retry. Drop the entry only when the unit is gone.
    /// </summary>
    private static bool IsForgottenActor(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return false;
        return message.Contains("does not resolve", StringComparison.OrdinalIgnoreCase)
            || message.Contains("no longer", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not readable", StringComparison.OrdinalIgnoreCase)
            || message.Contains(
                "orientation before teleporting",
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One-shot combat wake after spawn. Hostiles renew and lock onto the
    /// player. Friendlies skip <c>ai_renew</c> (keeps fireteam follow) but
    /// raise combat status, weapon readiness, and preferred hostile teams.
    /// Berserk is not forced.
    /// </summary>
    private async Task<ScriptExecutionResult> WakeSpawnedSquadAsync(
        ScriptExecutionResult spawnResult,
        SpawnScaffoldDiagnosis? diagnosis,
        bool preserveFireteam,
        CancellationToken cancellationToken)
    {
        if (!IsSpawnSuccess(spawnResult.Outcome))
            return spawnResult;

        string fallback = preserveFireteam
            ? EnemySpawnerService.DedicatedAllySquadName
            : EnemySpawnerService.DedicatedHostileSquadName;
        string squadName = !string.IsNullOrWhiteSpace(diagnosis?.SquadName)
            ? diagnosis!.SquadName
            : fallback;

        var lines = new List<string>();
        var trailing = new List<string>();
        AppendWakeLines(lines, trailing, squadName, preserveFireteam);
        lines.AddRange(trailing);
        return await FinishWakeAsync(spawnResult, lines, squadName, cancellationToken);
    }

    private async Task<ScriptExecutionResult> WakeSquadsAsync(
        ScriptExecutionResult spawnResult,
        IReadOnlyList<(string SquadName, bool Friendly)> targets,
        CancellationToken cancellationToken)
    {
        if (!IsSpawnSuccess(spawnResult.Outcome) || targets.Count == 0)
            return spawnResult;

        var lines = new List<string>();
        var trailing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string squadName, bool friendly) in targets)
        {
            if (!seen.Add(squadName))
                continue;
            AppendWakeLines(lines, trailing, squadName, friendly);
        }
        lines.AddRange(trailing);
        string label = string.Join(
            "+",
            seen);
        return await FinishWakeAsync(spawnResult, lines, label, cancellationToken);
    }

    private static void AppendWakeLines(
        List<string> lines,
        List<string> trailing,
        string squadName,
        bool friendly)
    {
        string hostileSquad = EnemySpawnerService.DedicatedHostileSquadName;
        if (friendly)
        {
            lines.Add($"ai_suppress_combat {squadName} false");
            lines.Add($"ai_force_active {squadName} true");
            lines.Add($"ai_set_weapon_up {squadName} true");
            lines.Add($"ai_prefer_target_team {squadName} covenant");
            lines.Add($"ai_prefer_target_team {squadName} brute");
            lines.Add($"ai_prefer_target_team {squadName} flood");
            lines.Add($"ai_magically_see {squadName} {hostileSquad}");
            trailing.Add($"ai_set_combat_status {squadName} dangerous_enemy");
            trailing.Add(
                $"ai_set_combat_status {squadName} ai_combat_status_dangerous_enemy");
        }
        else
        {
            lines.Add($"ai_suppress_combat {squadName} false");
            lines.Add($"ai_force_active {squadName} true");
            lines.Add($"ai_renew {squadName}");
            lines.Add($"ai_magically_see_object {squadName} (player_get 0)");
            lines.Add("ai_prefer_target (players) true");
        }
    }

    private async Task<ScriptExecutionResult> FinishWakeAsync(
        ScriptExecutionResult spawnResult,
        IReadOnlyList<string> lines,
        string label,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
            return spawnResult;
        if (!await TryRunHaloScriptAsync(lines, cancellationToken))
            return spawnResult;
        return spawnResult with
        {
            Message = $"{spawnResult.Message} wake={label}:combat",
        };
    }

    private async Task<bool> TryRunHaloScriptAsync(
        IReadOnlyList<string> lines,
        CancellationToken cancellationToken)
    {
        try
        {
            ScriptExecutionResult result = await _bridge.ExecuteAsync(
                ScriptLanguage.HaloScript,
                string.Join('\n', lines),
                TimeSpan.FromSeconds(8),
                cancellationToken);
            // HaloScript is handed to the console and comes back "submitted".
            // The console API does not return evaluation output.
            return result.Outcome is ScriptOutcome.Confirmed or ScriptOutcome.Submitted;
        }
        catch
        {
            return false;
        }
    }

    public async Task<ObjectTeamResult> ApplyObjectTeamAsync(
        int team,
        int? actorDatum = null,
        CancellationToken cancellationToken = default)
    {
        if (team is not (FriendlyTeam or HumanTeam or HostileTeam))
            throw new ArgumentOutOfRangeException(nameof(team));

        // Minimal payload: target,team. Lua appends player unit for combat-aim clear.
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
            throw new BridgeFailureException(result.Message);
        return ParseObjectTeam(result.Message);
    }

    public async Task<ScriptExecutionResult> SubmitAllegianceAsync(
        int team,
        bool breakAllegiance,
        CancellationToken cancellationToken = default) =>
        await SubmitAllegiancePairsAsync(
            [(FriendlyTeam, team, breakAllegiance)],
            cancellationToken);

    /// <summary>
    /// Apply or break pairwise <c>ai_allegiance</c> in both directions.
    /// Same-team pairs are skipped.
    /// </summary>
    public async Task<ScriptExecutionResult> SubmitAllegiancePairsAsync(
        IReadOnlyList<(int Left, int Right, bool Break)> pairs,
        CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();
        foreach ((int left, int right, bool breakAllegiance) in pairs)
        {
            if (left == right)
                continue;
            string leftName = HaloScriptTeamName(left);
            string rightName = HaloScriptTeamName(right);
            if (breakAllegiance)
            {
                // ai_allegiance_remove is the inverse of ai_allegiance.
                // ai_allegiance_break is a campaign "betrayal" flag and often
                // does nothing to a relationship we created ourselves.
                lines.Add($"ai_allegiance_remove {leftName} {rightName}");
                lines.Add($"ai_allegiance_remove {rightName} {leftName}");
                lines.Add($"ai_allegiance_break {leftName} {rightName}");
                lines.Add($"ai_allegiance_break {rightName} {leftName}");
            }
            else
            {
                lines.Add($"ai_allegiance {leftName} {rightName}");
                lines.Add($"ai_allegiance {rightName} {leftName}");
            }
        }

        if (lines.Count == 0)
            throw new ArgumentException("No allegiance pairs were provided.", nameof(pairs));

        EnsureBridgeReady();
        return await _bridge.ExecuteAsync(
            ScriptLanguage.HaloScript,
            string.Join('\n', lines),
            TimeSpan.FromSeconds(10),
            cancellationToken);
    }

    /// <summary>
    /// After a ceasefire, AI often stay idle. Nudge both dedicated battle
    /// squads so they notice each other once allegiance is removed.
    /// </summary>
    public async Task WakeBattleCombatAsync(
        CancellationToken cancellationToken = default)
    {
        string ally = EnemySpawnerService.DedicatedAllySquadName;
        string hostile = EnemySpawnerService.DedicatedHostileSquadName;
        await TryRunHaloScriptAsync(
            [
                $"ai_suppress_combat {ally} false",
                $"ai_suppress_combat {hostile} false",
                $"ai_force_active {ally} true",
                $"ai_force_active {hostile} true",
                $"ai_renew {ally}",
                $"ai_renew {hostile}",
                $"ai_set_weapon_up {ally} true",
                $"ai_set_weapon_up {hostile} true",
                $"ai_magically_see {ally} {hostile}",
                $"ai_magically_see {hostile} {ally}",
                $"ai_prefer_target_team {ally} covenant",
                $"ai_prefer_target_team {hostile} human",
            ],
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

    private async Task<PlayerPose> ReadPlayerPoseAsync(
        CancellationToken cancellationToken)
    {
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.PlayerPosition,
            "current",
            TimeSpan.FromSeconds(8),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new BridgeFailureException(result.Message);
        if (!TryParsePlayerPose(result.Message, out PlayerPose pose))
        {
            throw new InvalidDataException(
                "The game returned an invalid player position.");
        }
        return pose;
    }

    private async Task<ScriptExecutionResult> TeleportObjectAsync(
        int actorDatum,
        WorldPoint destination,
        CancellationToken cancellationToken)
    {
        string payload = string.Create(
            CultureInfo.InvariantCulture,
            $"a{(uint)actorDatum:X8},{destination.X:G9},{destination.Y:G9},{destination.Z:G9}");
        return await _bridge.ExecuteAsync(
            ScriptLanguage.ObjectTeleport,
            payload,
            TimeSpan.FromSeconds(12),
            cancellationToken);
    }

    private static bool TryParsePlayerPose(string message, out PlayerPose pose)
    {
        pose = default;
        const string marker = "Return value: ";
        int markerOffset = message.IndexOf(marker, StringComparison.Ordinal);
        if (markerOffset < 0)
            return false;
        string rest = message[(markerOffset + marker.Length)..];
        int paren = rest.IndexOf('(');
        if (paren >= 0)
            rest = rest[..paren];
        string[] values = rest
            .Trim()
            .TrimEnd('.')
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (values.Length < 3 ||
            !float.TryParse(
                values[0],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float x) ||
            !float.TryParse(
                values[1],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float y) ||
            !float.TryParse(
                values[2],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float z))
        {
            return false;
        }

        float forwardX = 1f;
        float forwardY = 0f;
        if (values.Length >= 5 &&
            float.TryParse(
                values[3],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float parsedForwardX) &&
            float.TryParse(
                values[4],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out float parsedForwardY) &&
            parsedForwardX * parsedForwardX + parsedForwardY * parsedForwardY > 1e-8f)
        {
            forwardX = parsedForwardX;
            forwardY = parsedForwardY;
        }

        pose = new PlayerPose(x, y, z, forwardX, forwardY);
        return true;
    }

    private static IReadOnlyList<int> ParseActorDatums(string message)
    {
        Match list = ActorDatumsListPattern.Match(message);
        if (list.Success)
        {
            var parsed = new List<int>();
            foreach (Match hex in Regex.Matches(
                         list.Groups[1].Value,
                         @"0x([0-9A-Fa-f]{8})",
                         RegexOptions.CultureInvariant))
            {
                if (uint.TryParse(
                        hex.Groups[1].Value,
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out uint datum))
                {
                    parsed.Add(unchecked((int)datum));
                }
            }
            if (parsed.Count > 0)
                return parsed;
        }

        int? first = TryParseActorDatum(message);
        return first is int actor ? [actor] : [];
    }

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
        status.EnsureRuntimeReady();
        if (!status.RunningVersion.SupportsLegacyFeature(110))
        {
            throw new InvalidOperationException(
                L.Get("allegiance_demo.requires_bridge_v102"));
        }
    }

    private void EnsureObjectBridgeReady()
    {
        ScriptingBridgeStatus status = BridgeStatus;
        status.EnsureRuntimeReady();
        if (!status.RunningVersion.SupportsLegacyFeature(106))
        {
            throw new InvalidOperationException(
                L.Get("allegiance_demo.requires_bridge_v106"));
        }
    }
}
