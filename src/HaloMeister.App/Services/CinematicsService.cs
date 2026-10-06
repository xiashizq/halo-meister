using System.Text.RegularExpressions;
using HaloMeister.App.Localization;
using HaloMeister.App.Models;

namespace HaloMeister.App.Services;

public sealed record LevelCinematic(
    int Index,
    string Name,
    string TagPath,
    string TransitionPath,
    string? ZoneSetName,
    int FadeOutTicks,
    int ZoneSwitchTicks)
{
    public string DisplayName => $"{Index + 1}. {Name}";
}

public sealed record LevelCinematicsSession(
    string ScenarioPath,
    IReadOnlyList<LevelCinematic> Cinematics);

public sealed class CinematicsService
{
    private const string CinematicExtension = "cinematic";
    private const string TransitionExtension = "cinematic_transition";
    private const int MaximumCinematicEntries = 256;
    private const int MaximumZoneSets = 64;
    private const int TicksPerSecond = 30;
    private const int MaxWaitTicks = TicksPerSecond * 8;
    private const int DefaultFadeOutTicks = TicksPerSecond;
    private const int DefaultZoneSwitchTicks = TicksPerSecond;

    private readonly RuntimeTagMemoryService _memory = RuntimeTagMemoryService.Current;
    private readonly RuntimeTagDefinitionService _definitions = new();
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;

    private static string ToTagReference(string path)
    {
        string value = path.Replace('/', '\\').Trim().Trim('"');
        int slash = value.LastIndexOf('\\');
        int dot = value.LastIndexOf('.');
        if (dot > slash)
            value = value[..dot];
        return value;
    }

    public async Task<ScriptExecutionResult> PlayAsync(
        LevelCinematic cinematic,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cinematic);
        if (!_memory.IsConnected)
            throw new InvalidOperationException(L.Get("cinematics.not_connected"));

        ScriptingBridgeStatus status = _bridge.GetStatus();
        status.EnsureRuntimeReady();

        string reference = ToTagReference(cinematic.TagPath);
        string zone = ToScriptToken(cinematic.ZoneSetName);
        ScriptExecutionResult result = await ExecuteScriptAsync(
            BuildTeardownScript(),
            cancellationToken);
        if (result.Outcome == ScriptOutcome.Failed)
            return result;

        // cinematic_stop only latches on a later game tick. The console runs
        // every line of one request on the game thread, so the next phase has
        // to be a separate request.
        await Task.Delay(TicksToDelay(2), cancellationToken);

        result = await ExecuteScriptAsync(
            BuildFadeOutScript(reference, zone),
            cancellationToken);
        if (result.Outcome == ScriptOutcome.Failed)
            return result;

        await Task.Delay(TicksToDelay(cinematic.FadeOutTicks), cancellationToken);
        if (zone.Length > 0)
        {
            result = await ExecuteScriptAsync(
                $"(switch_zone_set {zone})",
                cancellationToken);
            if (result.Outcome == ScriptOutcome.Failed)
                return result;

            await Task.Delay(
                TicksToDelay(Math.Max(cinematic.ZoneSwitchTicks, DefaultZoneSwitchTicks)),
                cancellationToken);
        }

        return await ExecuteScriptAsync(
            BuildStartScript(reference),
            cancellationToken);
    }

    public LevelCinematicsSession Scan()
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException(L.Get("cinematics.not_connected"));

        EnsureDefinitions();
        IReadOnlyList<RuntimeTagEntry> tags = _memory.ReadTags();
        var tagsByIndex = new Dictionary<int, RuntimeTagEntry>(tags.Count);
        foreach (RuntimeTagEntry tag in tags)
            tagsByIndex.TryAdd(tag.Index, tag);

        RuntimeTagEntry scenario = tags.FirstOrDefault(tag =>
                string.Equals(tag.Group, "scnr", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0)
            ?? throw new InvalidDataException(L.Get("cinematics.no_scenario"));

        var ordered = new List<RuntimeTagEntry>();
        var seen = new HashSet<int>();
        var scenarioSlotByTag = new Dictionary<int, int>();
        IReadOnlyList<RuntimeTagFieldValue> root = ReadRoot(scenario);
        CollectCinematicBlocks(
            scenario, root, tagsByIndex, ordered, seen, scenarioSlotByTag);
        CollectCutsceneResources(root, tagsByIndex, ordered, seen);

        if (ordered.Count == 0)
            CollectByLevelPath(scenario, tags, ordered);

        var cinematics = new List<LevelCinematic>(ordered.Count);
        for (int index = 0; index < ordered.Count; index++)
        {
            RuntimeTagEntry cinematic = ordered[index];
            string transition = ResolveTransitionPath(cinematic, tagsByIndex, out RuntimeTagEntry? transitionTag);
            bool hasScenarioSlot = scenarioSlotByTag.TryGetValue(
                cinematic.Index,
                out int scenarioSlot);
            string? zoneSet = hasScenarioSlot
                ? ResolveZoneSetName(scenario, root, scenarioSlot, cinematic.LeafName)
                : null;
            (int fadeOutTicks, int zoneSwitchTicks) = ReadTransitionTiming(transitionTag);
            cinematics.Add(new LevelCinematic(
                index,
                cinematic.LeafName,
                FormatScriptPath(cinematic.Name, CinematicExtension),
                transition,
                zoneSet,
                fadeOutTicks,
                zoneSwitchTicks));
        }

        return new LevelCinematicsSession(scenario.Name, cinematics);
    }

    private void CollectCinematicBlocks(
        RuntimeTagEntry owner,
        IReadOnlyList<RuntimeTagFieldValue> fields,
        IReadOnlyDictionary<int, RuntimeTagEntry> tagsByIndex,
        List<RuntimeTagEntry> ordered,
        HashSet<int> seen,
        Dictionary<int, int>? scenarioSlotByTag = null)
    {
        foreach (RuntimeTagFieldValue block in fields)
        {
            if (!string.Equals(block.Type, "block", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    block.ChildBlockDefinition,
                    "scenario_cinematics_block",
                    StringComparison.OrdinalIgnoreCase) ||
                !block.CanOpenBlock)
                continue;

            int count = Math.Min(block.ChildCount, MaximumCinematicEntries);
            for (int index = 0; index < count; index++)
            {
                IReadOnlyList<RuntimeTagFieldValue> element = ReadBlock(owner, block, index);
                RuntimeTagFieldValue? reference = element.FirstOrDefault(field => field.IsTagReference);
                int countBefore = ordered.Count;
                if (!TryAddReferenced(reference, "cine", tagsByIndex, ordered, seen) ||
                    scenarioSlotByTag is null ||
                    ordered.Count == countBefore)
                    continue;

                scenarioSlotByTag[ordered[^1].Index] = index;
            }
        }
    }

    private void CollectCutsceneResources(
        IReadOnlyList<RuntimeTagFieldValue> scenarioFields,
        IReadOnlyDictionary<int, RuntimeTagEntry> tagsByIndex,
        List<RuntimeTagEntry> ordered,
        HashSet<int> seen)
    {
        foreach (RuntimeTagFieldValue field in scenarioFields)
        {
            if (!field.IsTagReference ||
                !field.AllowedTagGroups.Contains("cin*", StringComparer.OrdinalIgnoreCase) ||
                !tagsByIndex.TryGetValue(field.ReferencedTagIndex, out RuntimeTagEntry? resource) ||
                !string.Equals(resource.Group, "cin*", StringComparison.OrdinalIgnoreCase) ||
                resource.DataAddress <= 0 ||
                !_definitions.HasSchema(resource.Group))
                continue;

            CollectCinematicBlocks(
                resource,
                ReadRoot(resource),
                tagsByIndex,
                ordered,
                seen);
        }
    }

    private static void CollectByLevelPath(
        RuntimeTagEntry scenario,
        IReadOnlyList<RuntimeTagEntry> tags,
        List<RuntimeTagEntry> ordered)
    {
        HashSet<string> tokens = LevelTokens(scenario.Name);
        if (tokens.Count == 0)
            return;

        foreach (RuntimeTagEntry tag in tags
                     .Where(tag =>
                         string.Equals(tag.Group, "cine", StringComparison.OrdinalIgnoreCase) &&
                         BelongsToLevel(tag.Name, tokens))
                     .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase))
            ordered.Add(tag);
    }

    private string ResolveTransitionPath(
        RuntimeTagEntry cinematic,
        IReadOnlyDictionary<int, RuntimeTagEntry> tagsByIndex,
        out RuntimeTagEntry? transitionTag)
    {
        transitionTag = null;
        if (cinematic.DataAddress <= 0 || !_definitions.HasSchema("cine"))
            return "";

        IReadOnlyList<RuntimeTagFieldValue> root = ReadRoot(cinematic);
        RuntimeTagFieldValue? transition = root.FirstOrDefault(field =>
            field.IsTagReference &&
            string.Equals(
                CleanFieldName(field.Name),
                "transition settings",
                StringComparison.OrdinalIgnoreCase));
        if (!TryResolveReference(transition, "citr", tagsByIndex, out RuntimeTagEntry? tag))
            return "";
        transitionTag = tag;
        return FormatScriptPath(tag.Name, TransitionExtension);
    }

    private string? ResolveZoneSetName(
        RuntimeTagEntry scenario,
        IReadOnlyList<RuntimeTagFieldValue> scenarioFields,
        int cinematicSlot,
        string cinematicName)
    {
        if (cinematicSlot is < 0 or >= 32)
            return null;

        RuntimeTagFieldValue? zoneSets = scenarioFields.FirstOrDefault(field =>
            field.CanOpenBlock &&
            string.Equals(
                CleanFieldName(field.Name),
                "zone sets",
                StringComparison.OrdinalIgnoreCase));
        if (zoneSets is null)
            return null;

        uint bit = 1u << cinematicSlot;
        string? namedMatch = null;
        string? lastMatch = null;
        int count = Math.Min(zoneSets.ChildCount, MaximumZoneSets);
        for (int index = 0; index < count; index++)
        {
            IReadOnlyList<RuntimeTagFieldValue> element = ReadBlock(scenario, zoneSets, index);
            RuntimeTagFieldValue? flags = element.FirstOrDefault(field =>
                string.Equals(
                    CleanFieldName(field.Name),
                    "cinematic zones",
                    StringComparison.OrdinalIgnoreCase));
            if (flags is null ||
                !TryParseUnsigned(flags.Value, out uint mask) ||
                (mask & bit) == 0)
                continue;

            RuntimeTagFieldValue? nameField = element.FirstOrDefault(field =>
                string.Equals(field.Type, "string_id", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "name",
                    StringComparison.OrdinalIgnoreCase));
            if (nameField is null ||
                !TryParseUnsigned(nameField.Value, out uint stringId) ||
                !_memory.TryGetStringIdName(stringId, out string? name) ||
                string.IsNullOrWhiteSpace(name))
                continue;

            lastMatch = name;
            if (name.Contains(cinematicName, StringComparison.OrdinalIgnoreCase))
                namedMatch = name;
        }

        return namedMatch ?? lastMatch;
    }

    private (int FadeOutTicks, int ZoneSwitchTicks) ReadTransitionTiming(
        RuntimeTagEntry? transition)
    {
        if (transition is null || transition.DataAddress <= 0 || !_definitions.HasSchema("citr"))
            return (DefaultFadeOutTicks, DefaultZoneSwitchTicks);

        var sleepTicks = new List<int>();
        foreach (RuntimeTagFieldValue field in ReadRoot(transition))
        {
            if (!CleanFieldName(field.Name).EndsWith(
                    "sleep time",
                    StringComparison.OrdinalIgnoreCase) ||
                !TryParseSigned(field.Value, out int ticks))
                continue;
            sleepTicks.Add(ticks);
        }

        int fadeOut = sleepTicks.Count > 0 ? sleepTicks[0] : DefaultFadeOutTicks;
        // Element 3 is "fade post core load": how long the transition waits
        // after the cinematic zone finishes loading.
        int zoneSwitch = sleepTicks.Count > 3 ? sleepTicks[3] : DefaultZoneSwitchTicks;
        return (fadeOut, zoneSwitch);
    }

    private async Task<ScriptExecutionResult> ExecuteScriptAsync(
        string script,
        CancellationToken cancellationToken) =>
        await _bridge.ExecuteAsync(
            ScriptLanguage.HaloScript,
            script,
            TimeSpan.FromSeconds(15),
            cancellationToken);

    private static string BuildTeardownScript() =>
        string.Join(Environment.NewLine, [
            "(cinematic_stop)",
            "(cinematic_skip_stop_internal)",
            "(cinematic_reset)",
        ]);

    private static string BuildFadeOutScript(string cinematicReference, string zoneToken)
    {
        var lines = new List<string>();
        if (zoneToken.Length > 0)
            lines.Add($"(prepare_to_switch_to_zone_set {zoneToken})");

        lines.Add($"(cinematic_set {cinematicReference})");
        lines.Add("(cinematic_skip_start_internal)");
        lines.Add($"(cinematic_fade_out_from_game {cinematicReference})");
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildStartScript(string cinematicReference) =>
        string.Join(Environment.NewLine, [
            $"(cinematic_fade_in_to_cinematic {cinematicReference})",
            "(cinematic_start)",
        ]);

    private static TimeSpan TicksToDelay(int ticks)
    {
        int clamped = Math.Clamp(ticks, 1, MaxWaitTicks);
        return TimeSpan.FromMilliseconds(clamped * 1000.0 / TicksPerSecond);
    }

    private static string ToScriptToken(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        string value = name.Trim();
        if (Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            return value;

        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static bool TryParseUnsigned(string value, out uint parsed)
    {
        parsed = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        value = value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(
                value[2..],
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out parsed);
        return uint.TryParse(
            value,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out parsed);
    }

    private static bool TryParseSigned(string value, out int parsed)
    {
        parsed = 0;
        if (!TryParseUnsigned(value, out uint unsigned) || unsigned > int.MaxValue)
            return false;
        parsed = (int)unsigned;
        return true;
    }

    private static bool TryAddReferenced(
        RuntimeTagFieldValue? reference,
        string group,
        IReadOnlyDictionary<int, RuntimeTagEntry> tagsByIndex,
        List<RuntimeTagEntry> ordered,
        HashSet<int> seen)
    {
        if (!TryResolveReference(reference, group, tagsByIndex, out RuntimeTagEntry? tag) ||
            !seen.Add(tag.Index))
            return false;
        ordered.Add(tag);
        return true;
    }

    private static bool TryResolveReference(
        RuntimeTagFieldValue? reference,
        string group,
        IReadOnlyDictionary<int, RuntimeTagEntry> tagsByIndex,
        out RuntimeTagEntry tag)
    {
        tag = null!;
        if (reference is null ||
            reference.ReferencedTagIndex < 0 ||
            reference.ReferencedTagIndex == ushort.MaxValue ||
            !tagsByIndex.TryGetValue(reference.ReferencedTagIndex, out RuntimeTagEntry? resolved) ||
            !string.Equals(resolved.Group, group, StringComparison.OrdinalIgnoreCase))
            return false;
        tag = resolved;
        return true;
    }

    private static string FormatScriptPath(string tagName, string extension)
    {
        string path = tagName.Replace('/', '\\').Trim().Trim('"');
        string suffix = "." + extension;
        if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            path += suffix;
        return path;
    }

    private static HashSet<string> LevelTokens(string scenarioPath)
    {
        string[] parts = scenarioPath
            .Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (parts.Length == 0)
            return tokens;

        AddToken(tokens, parts[^1]);
        if (parts.Length >= 2)
            AddToken(tokens, parts[^2]);
        return tokens;
    }

    private static void AddToken(HashSet<string> tokens, string value)
    {
        if (value.Length >= 3)
            tokens.Add(value);
        Match code = Regex.Match(value, @"^[A-Za-z]+\d+");
        if (code.Success && code.Value.Length >= 3)
            tokens.Add(code.Value);
    }

    private static bool BelongsToLevel(string cinematicPath, HashSet<string> tokens)
    {
        string[] parts = cinematicPath
            .Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        for (int index = 0; index < parts.Length - 1; index++)
        {
            if (parts[index].Equals("cinematics", StringComparison.OrdinalIgnoreCase) &&
                tokens.Contains(parts[index + 1]))
                return true;
        }

        string leaf = parts[^1];
        foreach (string token in tokens)
        {
            if (leaf.Equals(token, StringComparison.OrdinalIgnoreCase) ||
                leaf.StartsWith(token + "_", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void EnsureDefinitions()
    {
        if (_definitions.SchemaCount == 0)
            _definitions.LoadDirectory(RuntimeTagDefinitionLocator.ResolveCampaignEvolved());
        if (!_definitions.HasSchema("scnr") || !_definitions.HasSchema("cine"))
            throw new InvalidDataException(L.Get("cinematics.no_schema"));
    }

    private IReadOnlyList<RuntimeTagFieldValue> ReadRoot(RuntimeTagEntry tag) =>
        _definitions.ReadRootFields(
            tag.Group,
            tag.DataAddress,
            _memory.ReadBytes,
            ResolveOrNull);

    private IReadOnlyList<RuntimeTagFieldValue> ReadBlock(
        RuntimeTagEntry tag,
        RuntimeTagFieldValue block,
        int index) =>
        _definitions.ReadBlockFields(
            tag.Group,
            block.ChildBlockDefinition!,
            block.ChildAddress,
            index,
            _memory.ReadBytes,
            ResolveOrNull);

    private long? ResolveOrNull(uint encoded) =>
        _memory.TryResolveOffset(encoded, out long address) ? address : null;

    private static string CleanFieldName(string name)
    {
        int description = name.IndexOfAny(['#', '{', ':', '^', '*', '!', '~']);
        string value = description >= 0 ? name[..description] : name;
        int path = value.LastIndexOf('/');
        return (path >= 0 ? value[(path + 1)..] : value).Trim();
    }
}
