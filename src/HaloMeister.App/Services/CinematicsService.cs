using System.Text.RegularExpressions;
using HaloMeister.App.Localization;
using HaloMeister.App.Models;

namespace HaloMeister.App.Services;

public sealed record LevelCinematic(
    int Index,
    string Name,
    string TagPath,
    string TransitionPath)
{
    public string DisplayName => $"{Index + 1}. {Name}";

    public string PlayScript => CinematicsService.BuildPlayScript(TagPath, TransitionPath);
}

public sealed record LevelCinematicsSession(
    string ScenarioPath,
    IReadOnlyList<LevelCinematic> Cinematics);

public sealed class CinematicsService
{
    private const string CinematicExtension = "cinematic";
    private const string TransitionExtension = "cinematic_transition";
    private const int MaximumCinematicEntries = 256;

    private readonly RuntimeTagMemoryService _memory = RuntimeTagMemoryService.Current;
    private readonly RuntimeTagDefinitionService _definitions = new();
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;

    public static string BuildPlayScript(string cinematicPath, string? transitionPath)
    {
        string cinematic = ToTagReference(cinematicPath);
        string transition = ToTagReference(transitionPath ?? "");
        var lines = new List<string>();
        if (transition.Length > 0)
            lines.Add($"(cinematic_transition_fade_out_from_game {transition})");

        lines.Add($"(cinematic_set {cinematic})");
        lines.Add("(cinematic_start)");
        return string.Join(Environment.NewLine, lines);
    }

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
        return await _bridge.ExecuteAsync(
            ScriptLanguage.HaloScript,
            cinematic.PlayScript,
            TimeSpan.FromSeconds(15),
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
        IReadOnlyList<RuntimeTagFieldValue> root = ReadRoot(scenario);
        CollectCinematicBlocks(scenario, root, tagsByIndex, ordered, seen);
        CollectCutsceneResources(root, tagsByIndex, ordered, seen);

        if (ordered.Count == 0)
            CollectByLevelPath(scenario, tags, ordered);

        var cinematics = new List<LevelCinematic>(ordered.Count);
        for (int index = 0; index < ordered.Count; index++)
        {
            RuntimeTagEntry cinematic = ordered[index];
            string transition = ResolveTransitionPath(cinematic, tagsByIndex);
            cinematics.Add(new LevelCinematic(
                index,
                cinematic.LeafName,
                FormatScriptPath(cinematic.Name, CinematicExtension),
                transition));
        }

        return new LevelCinematicsSession(scenario.Name, cinematics);
    }

    private void CollectCinematicBlocks(
        RuntimeTagEntry owner,
        IReadOnlyList<RuntimeTagFieldValue> fields,
        IReadOnlyDictionary<int, RuntimeTagEntry> tagsByIndex,
        List<RuntimeTagEntry> ordered,
        HashSet<int> seen)
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
                TryAddReferenced(reference, "cine", tagsByIndex, ordered, seen);
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
        IReadOnlyDictionary<int, RuntimeTagEntry> tagsByIndex)
    {
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
        return FormatScriptPath(tag.Name, TransitionExtension);
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
