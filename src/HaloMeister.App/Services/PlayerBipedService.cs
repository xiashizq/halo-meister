using HaloMeister.App.Models;
using HaloMeister.App.Localization;

namespace HaloMeister.App.Services;

public sealed record PlayerBipedChoice(
    string Name,
    string Category,
    RuntimeTagEntry BipedTag,
    bool IsOriginal)
{
    public string TagPath => BipedTag.Name;
    public string Detail => $"{Category} · {TagPath}  [bipd]";
}

public sealed record PlayerBipedSession(
    RuntimeTagEntry PlayerBiped,
    RuntimeTagEntry ActiveBiped,
    IReadOnlyList<PlayerBipedChoice> Choices);

public sealed class PlayerBipedService : IDisposable
{
    private const uint ConfirmedPlayerDatum = 0xFBB2195C;

    private readonly RuntimeTagMemoryService _memory = RuntimeTagMemoryService.Current;
    private readonly RuntimeTagDefinitionService _definitions = new();
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private readonly List<PlayerRepresentationSnapshot> _representations = [];
    private IReadOnlyList<RuntimeTagEntry> _tags = [];
    private RuntimeTagEntry? _playerBiped;
    private long _capturedPlayerNameAddress;
    private int _activeBipedIndex = -1;

    public int ProcessId => _memory.ProcessId;
    public ScriptingBridgeStatus BridgeStatus => _bridge.GetStatus();
    public bool CanRestore =>
        _representations.Count > 0 &&
        _playerBiped is not null &&
        _playerBiped.NameAddress == _capturedPlayerNameAddress &&
        _memory.IsConnected;

    public PlayerBipedSession Connect()
    {
        if (_definitions.SchemaCount == 0)
            _definitions.LoadDirectory(
                RuntimeTagDefinitionLocator.ResolveCampaignEvolved());
        if (!_definitions.HasSchema("bipd") ||
            !_definitions.HasSchema("matg") ||
            !_definitions.HasSchema("scnr"))
            throw new InvalidDataException(
                "The loaded definitions do not provide the [bipd], [matg], and [scnr] schemas.");

        if (!_memory.IsConnected)
            throw new InvalidOperationException(
                "Connect to the game from the header first.");
        _tags = _memory.ReadTags();
        _playerBiped = FindPlayerBiped();
        _representations.Clear();
        _capturedPlayerNameAddress = 0;
        _activeBipedIndex = _playerBiped.Index;
        return BuildSession();
    }

    public PlayerBipedSession Refresh()
    {
        if (!_memory.IsConnected)
            throw new InvalidOperationException("Connect to the running game first.");
        _tags = _memory.ReadTags();
        _playerBiped = FindPlayerBiped();
        if (_representations.Count > 0 &&
            _playerBiped.NameAddress != _capturedPlayerNameAddress)
        {
            _representations.Clear();
            _capturedPlayerNameAddress = 0;
            _activeBipedIndex = _playerBiped.Index;
        }
        return BuildSession();
    }

    public void Apply(PlayerBipedChoice choice)
    {
        RuntimeTagEntry player = _playerBiped
            ?? throw new InvalidOperationException("The original player biped is not available.");
        RuntimeTagEntry target = _tags.FirstOrDefault(tag =>
                tag.Index == choice.BipedTag.Index && IsUsableBiped(tag))
            ?? throw new InvalidOperationException(
                "That biped tag is no longer loaded. Refresh the page and choose it again.");
        if (_representations.Count > 0 &&
            player.NameAddress != _capturedPlayerNameAddress)
            throw new InvalidOperationException(
                "The live tag table moved after globals were captured. Reconnect before applying another biped.");

        CapturePlayerRepresentations(player);
        byte[] targetReference = _memory.BuildTagReference(target);
        byte[] targetVariant = ReadDefaultModelVariant(target);
        var completedWrites = new List<MemorySnapshot>();
        try
        {
            foreach (PlayerRepresentationSnapshot representation in _representations)
            {
                completedWrites.Add(new MemorySnapshot(
                    representation.Unit.Address,
                    _memory.ReadBytes(
                        representation.Unit.Address,
                        representation.Unit.OriginalBytes.Length)));
                _memory.WriteVerified(representation.Unit.Address, targetReference);

                completedWrites.Add(new MemorySnapshot(
                    representation.Variant.Address,
                    _memory.ReadBytes(
                        representation.Variant.Address,
                        representation.Variant.OriginalBytes.Length)));
                _memory.WriteVerified(representation.Variant.Address, targetVariant);
            }
            _activeBipedIndex = target.Index;
        }
        catch
        {
            foreach (MemorySnapshot write in completedWrites.AsEnumerable().Reverse())
            {
                try { _memory.WriteVerified(write.Address, write.OriginalBytes); }
                catch { }
            }
            throw;
        }
    }

    public void Restore()
    {
        RuntimeTagEntry player = _playerBiped
            ?? throw new InvalidOperationException("The original player biped is not available.");
        if (_representations.Count == 0)
            throw new InvalidOperationException("No original globals state has been captured.");
        if (player.NameAddress != _capturedPlayerNameAddress)
            throw new InvalidOperationException(
                "The live tag table moved after globals were captured. Reconnect instead of restoring stale addresses.");

        foreach (PlayerRepresentationSnapshot representation in _representations)
        {
            _memory.WriteVerified(
                representation.Unit.Address,
                representation.Unit.OriginalBytes);
            _memory.WriteVerified(
                representation.Variant.Address,
                representation.Variant.OriginalBytes);
        }
        _activeBipedIndex = player.Index;
    }

    public async Task<ScriptExecutionResult> SpawnForBumpPossessionAsync(
        PlayerBipedChoice choice,
        CancellationToken cancellationToken = default)
    {
        ScriptingBridgeStatus status = _bridge.GetStatus();
        if (!status.IsRuntimeReady)
            throw new InvalidOperationException(
                L.Get("bridge.error_not_responding"));
        if (status.IsStale)
            throw new InvalidOperationException(status.Summary);

        RuntimeTagEntry target = _tags.FirstOrDefault(tag =>
                tag.Index == choice.BipedTag.Index && IsUsableBiped(tag))
            ?? throw new InvalidOperationException(
                "That biped tag is no longer loaded. Refresh and select it again.");
        uint datum = RuntimeTagMemoryService.BuildRuntimeDatum(target);
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.BlamBipedPossess,
            datum.ToString("X8"),
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);
        return result;
    }

    public async Task<ScriptExecutionResult> DisableBumpPossessionAsync(
        CancellationToken cancellationToken = default)
    {
        ScriptExecutionResult result = await _bridge.ExecuteAsync(
            ScriptLanguage.BlamBumpPossessionOff,
            "off",
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(result.Message);
        return result;
    }

    public void Dispose() { }

    private PlayerBipedSession BuildSession()
    {
        RuntimeTagEntry player = _playerBiped ?? FindPlayerBiped();
        RuntimeTagEntry active = _tags.FirstOrDefault(tag => tag.Index == _activeBipedIndex)
            ?? player;
        PlayerBipedChoice[] choices = _tags
            .Where(IsUsableBiped)
            .Select(tag => new PlayerBipedChoice(
                DisplayName(tag),
                Categorize(tag.Name),
                tag,
                tag.Index == player.Index))
            .OrderBy(choice => choice.BipedTag.Index == active.Index ? 0 : 1)
            .ThenBy(choice => CategoryOrder(choice.Category))
            .ThenBy(choice => choice.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.TagPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (choices.Length == 0)
            throw new InvalidDataException("No usable [bipd] tags are loaded in this mission.");
        return new PlayerBipedSession(player, active, choices);
    }

    private void CapturePlayerRepresentations(RuntimeTagEntry player)
    {
        if (_representations.Count > 0) return;
        PlayerRepresentationLocation[] available = FindPlayerRepresentations().ToArray();
        PlayerRepresentationLocation[] selected = available
            .Where(location =>
                location.Biped.Index == player.Index ||
                PlayerNameScore(location.Biped.Name) >= 60 ||
                (string.Equals(
                     location.OwnerGroup,
                     "scnr",
                     StringComparison.OrdinalIgnoreCase) &&
                 location.ElementIndex == 0))
            .ToArray();
        if (selected.Length == 0)
        {
            PlayerRepresentationLocation? fallback = available
                .OrderBy(location =>
                    string.Equals(location.OwnerGroup, "matg", StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : 1)
                .ThenBy(location => location.ElementIndex)
                .FirstOrDefault();
            if (fallback is not null) selected = [fallback];
        }

        _representations.AddRange(selected.Select(location =>
            new PlayerRepresentationSnapshot(
                location.OwnerGroup,
                new MemorySnapshot(
                    location.Unit.Address,
                    _memory.ReadBytes(location.Unit.Address, location.Unit.Size)),
                new MemorySnapshot(
                    location.Variant.Address,
                    _memory.ReadBytes(location.Variant.Address, location.Variant.Size)))));
        if (_representations.Count == 0)
            throw new InvalidDataException(
                "No usable globals or scenario player representation was found.");
        _capturedPlayerNameAddress = player.NameAddress;
    }

    private IEnumerable<PlayerRepresentationLocation> FindPlayerRepresentations()
    {
        foreach (RuntimeTagEntry owner in _tags.Where(tag =>
                     tag.DataAddress > 0 &&
                     (string.Equals(tag.Group, "matg", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(tag.Group, "scnr", StringComparison.OrdinalIgnoreCase))))
        {
            IReadOnlyList<RuntimeTagFieldValue> root;
            try
            {
                root = _definitions.ReadRootFields(
                    owner.Group,
                    owner.DataAddress,
                    _memory.ReadBytes,
                    ResolveOrNull);
            }
            catch
            {
                continue;
            }

            foreach (RuntimeTagFieldValue block in root.Where(field =>
                         field.CanOpenBlock &&
                         string.Equals(
                             field.ChildBlockDefinition,
                             "player_representation_block",
                             StringComparison.OrdinalIgnoreCase)))
            {
                for (int element = 0; element < block.ChildCount; element++)
                {
                    IReadOnlyList<RuntimeTagFieldValue> fields;
                    try
                    {
                        fields = _definitions.ReadBlockFields(
                            owner.Group,
                            block.ChildBlockDefinition!,
                            block.ChildAddress,
                            element,
                            _memory.ReadBytes,
                            ResolveOrNull);
                    }
                    catch
                    {
                        continue;
                    }

                    RuntimeTagFieldValue? unit = fields.FirstOrDefault(field =>
                        field.IsTagReference &&
                        string.Equals(
                            CleanFieldName(field.Name),
                            "third person unit",
                            StringComparison.OrdinalIgnoreCase));
                    RuntimeTagFieldValue? variant = fields.FirstOrDefault(field =>
                        field.Type == "string_id" &&
                        string.Equals(
                            CleanFieldName(field.Name),
                            "third person variant",
                            StringComparison.OrdinalIgnoreCase));
                    RuntimeTagEntry? biped = unit is null
                        ? null
                        : _tags.FirstOrDefault(tag =>
                            tag.Index == unit.ReferencedTagIndex &&
                            IsUsableBiped(tag));
                    if (unit is null || variant is null || variant.Size != 4 || biped is null)
                        continue;

                    yield return new PlayerRepresentationLocation(
                        owner.Group,
                        element,
                        biped,
                        unit,
                        variant);
                }
            }
        }
    }

    private byte[] ReadDefaultModelVariant(RuntimeTagEntry biped)
    {
        IReadOnlyList<RuntimeTagFieldValue> fields = _definitions.ReadRootFields(
            biped.Group,
            biped.DataAddress,
            _memory.ReadBytes,
            ResolveOrNull);
        RuntimeTagFieldValue variant = fields.FirstOrDefault(field =>
                field.Type == "string_id" &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "default model variant",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"The [bipd] schema did not resolve the default model variant for {biped.Name}.");
        if (variant.Size != 4)
            throw new InvalidDataException(
                $"The default model variant for {biped.Name} has unexpected size {variant.Size}.");
        return _memory.ReadBytes(variant.Address, variant.Size);
    }

    private RuntimeTagEntry FindPlayerBiped()
    {
        RuntimeTagEntry[] bipeds = _tags.Where(IsUsableBiped).ToArray();
        PlayerRepresentationLocation[] representations =
            FindPlayerRepresentations().ToArray();

        RuntimeTagEntry? exact = representations
            .Select(location => location.Biped)
            .FirstOrDefault(tag =>
                RuntimeTagMemoryService.BuildRuntimeDatum(tag) == ConfirmedPlayerDatum);
        if (exact is not null) return exact;

        RuntimeTagEntry? representedChief = representations
            .Select(location => (location.Biped, Score: PlayerNameScore(location.Biped.Name)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Biped.Name.Length)
            .Select(item => item.Biped)
            .FirstOrDefault();
        if (representedChief is not null) return representedChief;

        RuntimeTagEntry? firstGlobal = representations
            .Where(location =>
                string.Equals(location.OwnerGroup, "matg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(location => location.ElementIndex)
            .Select(location => location.Biped)
            .FirstOrDefault();
        if (firstGlobal is not null) return firstGlobal;

        exact = bipeds.FirstOrDefault(tag =>
            RuntimeTagMemoryService.BuildRuntimeDatum(tag) == ConfirmedPlayerDatum);
        if (exact is not null) return exact;

        RuntimeTagEntry? named = bipeds
            .Select(tag => (Tag: tag, Score: PlayerNameScore(tag.Name)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Tag.Name.Length)
            .Select(item => item.Tag)
            .FirstOrDefault();
        return named ?? throw new InvalidDataException(
            "The Master Chief player [bipd] is not loaded. Enter an offline campaign mission and reconnect.");
    }

    private static bool IsUsableBiped(RuntimeTagEntry tag) =>
        string.Equals(tag.Group, "bipd", StringComparison.OrdinalIgnoreCase) &&
        tag.DataAddress > 0 &&
        tag.RootCount > 0 &&
        !tag.Name.Contains(@"\stimuli\", StringComparison.OrdinalIgnoreCase) &&
        !tag.Name.Contains("/stimuli/", StringComparison.OrdinalIgnoreCase);

    private long? ResolveOrNull(uint encoded) =>
        _memory.TryResolveOffset(encoded, out long address) ? address : null;

    private static int PlayerNameScore(string name)
    {
        string value = Normalize(name);
        int score = 0;
        if (value.Contains("masterchief", StringComparison.Ordinal)) score += 100;
        if (value.Contains("master_chief", StringComparison.Ordinal)) score += 100;
        if (value.Contains("chief", StringComparison.Ordinal)) score += 60;
        if (value.Contains("player", StringComparison.Ordinal)) score += 40;
        if (value.Contains("spartan", StringComparison.Ordinal)) score += 30;
        return score;
    }

    private static string Categorize(string path)
    {
        string value = Normalize(path);
        if (value.Contains("flood", StringComparison.Ordinal) ||
            value.Contains("infection", StringComparison.Ordinal) ||
            value.Contains("combat_form", StringComparison.Ordinal)) return "Flood";
        if (value.Contains("elite", StringComparison.Ordinal)) return "Elite";
        if (value.Contains("grunt", StringComparison.Ordinal)) return "Grunt";
        if (value.Contains("jackal", StringComparison.Ordinal) ||
            value.Contains("skirmisher", StringComparison.Ordinal)) return "Jackal";
        if (value.Contains("hunter", StringComparison.Ordinal)) return "Hunter";
        if (value.Contains("marine", StringComparison.Ordinal) ||
            value.Contains("crewman", StringComparison.Ordinal)) return "Human";
        if (value.Contains("chief", StringComparison.Ordinal) ||
            value.Contains("spartan", StringComparison.Ordinal)) return "Spartan";
        if (value.Contains("sentinel", StringComparison.Ordinal) ||
            value.Contains("monitor", StringComparison.Ordinal)) return "Forerunner";
        return "Other";
    }

    private static int CategoryOrder(string category) => category switch
    {
        "Spartan" => 0,
        "Elite" => 1,
        "Grunt" => 2,
        "Jackal" => 3,
        "Hunter" => 4,
        "Flood" => 5,
        "Human" => 6,
        "Forerunner" => 7,
        _ => 8,
    };

    private static string DisplayName(RuntimeTagEntry tag)
    {
        string value = Normalize(tag.Name);
        if (value.Contains("meteorite", StringComparison.Ordinal) ||
            value.Contains("prequel", StringComparison.Ordinal) ||
            value.Contains("mkiv", StringComparison.Ordinal) ||
            value.Contains("mark_iv", StringComparison.Ordinal))
            return "Mark IV (prequel mission)";
        return Humanize(tag.LeafName);
    }

    private static string CleanFieldName(string name)
    {
        int description = name.IndexOfAny(['#', '{', ':']);
        string value = description >= 0 ? name[..description] : name;
        int path = value.LastIndexOf('/');
        return (path >= 0 ? value[(path + 1)..] : value).Trim();
    }

    private static string Normalize(string value) =>
        value.Replace("\\", "/", StringComparison.Ordinal)
            .Replace("-", "_", StringComparison.Ordinal)
            .ToLowerInvariant();

    private static string Humanize(string value)
    {
        string text = value.Replace('_', ' ').Replace('-', ' ').Trim();
        return text.Length == 0
            ? "Unnamed biped"
            : string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private sealed record MemorySnapshot(long Address, byte[] OriginalBytes);

    private sealed record PlayerRepresentationSnapshot(
        string OwnerGroup,
        MemorySnapshot Unit,
        MemorySnapshot Variant);

    private sealed record PlayerRepresentationLocation(
        string OwnerGroup,
        int ElementIndex,
        RuntimeTagEntry Biped,
        RuntimeTagFieldValue Unit,
        RuntimeTagFieldValue Variant);
}
