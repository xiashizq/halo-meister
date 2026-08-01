using System.Buffers.Binary;
using HaloMeister.App.Models;

namespace HaloMeister.App.Services;

public sealed record RuntimeArmorVariantResult(
    string ModelTag,
    int VariantIndex,
    int RepresentationCount,
    string RuntimeMessage);

public sealed record RuntimeWeaponVariantResult(
    string ModelTag,
    int VariantIndex,
    string RuntimeMessage);

public sealed class RuntimeCustomizationVariantService : IDisposable
{
    private readonly RuntimeTagMemoryService _memory = RuntimeTagMemoryService.Current;
    private readonly RuntimeTagDefinitionService _definitions = new();
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private readonly List<MemorySnapshot> _originalVariants = [];
    private IReadOnlyList<RuntimeTagEntry> _tags = [];
    private long _capturedPlayerNameAddress;

    public bool CanRestore =>
        _memory.IsConnected &&
        _originalVariants.Count > 0 &&
        _tags.Any(tag =>
            tag.NameAddress == _capturedPlayerNameAddress &&
            IsUsableBiped(tag));

    public async Task<RuntimeArmorVariantResult> ApplyAsync(
        int variantIndex,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        EnsureBridgeReady();
        _tags = _memory.ReadTags();

        PlayerRepresentationLocation[] representations =
            FindPlayerRepresentations().ToArray();
        RuntimeTagEntry activePlayer = FindPlayerBiped(representations);
        RuntimeTagEntry masterChief = FindMasterChiefBiped();
        ModelVariantLocation selected = FindModelVariant(masterChief, variantIndex);
        PlayerRepresentationLocation[] targets = representations
            .Where(location =>
                location.Biped.Index == activePlayer.Index ||
                PlayerNameScore(location.Biped.Name) >= 60 ||
                (string.Equals(
                     location.OwnerGroup,
                     "scnr",
                     StringComparison.OrdinalIgnoreCase) &&
                 location.ElementIndex == 0))
            .ToArray();
        if (targets.Length == 0)
            throw new InvalidDataException(
                "No loaded globals or scenario player representation can receive the armor variant.");

        if (_originalVariants.Count > 0 &&
            activePlayer.NameAddress != _capturedPlayerNameAddress)
        {
            _originalVariants.Clear();
            _capturedPlayerNameAddress = 0;
        }
        if (_originalVariants.Count == 0)
        {
            _originalVariants.AddRange(targets.Select(target =>
                new MemorySnapshot(
                    target.Variant.Address,
                    _memory.ReadBytes(target.Variant.Address, target.Variant.Size))));
            _capturedPlayerNameAddress = activePlayer.NameAddress;
        }

        var completed = new List<MemorySnapshot>();
        try
        {
            foreach (PlayerRepresentationLocation target in targets)
            {
                completed.Add(new MemorySnapshot(
                    target.Variant.Address,
                    _memory.ReadBytes(target.Variant.Address, target.Variant.Size)));
                _memory.WriteVerified(target.Variant.Address, selected.VariantName);
            }

            uint stringId = BinaryPrimitives.ReadUInt32LittleEndian(
                selected.VariantName);
            ScriptExecutionResult runtime = await _bridge.ExecuteAsync(
                ScriptLanguage.BlamObjectVariant,
                stringId.ToString("X8"),
                TimeSpan.FromSeconds(15),
                cancellationToken);
            if (runtime.Outcome != ScriptOutcome.Confirmed)
                throw new InvalidOperationException(runtime.Message);

            return new RuntimeArmorVariantResult(
                selected.Model.Name,
                selected.BlockIndex,
                targets.Length,
                runtime.Message);
        }
        catch
        {
            foreach (MemorySnapshot write in completed.AsEnumerable().Reverse())
            {
                try { _memory.WriteVerified(write.Address, write.OriginalBytes); }
                catch { }
            }
            throw;
        }
    }

    public async Task<RuntimeWeaponVariantResult> ApplyWeaponAsync(
        string segment,
        int variantIndex,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        EnsureBridgeReady();
        _tags = _memory.ReadTags();

        RuntimeTagEntry model = FindWeaponModel(segment);
        ModelVariantLocation selected = ReadModelVariant(model, variantIndex);
        uint stringId = BinaryPrimitives.ReadUInt32LittleEndian(
            selected.VariantName);
        ScriptExecutionResult runtime = await _bridge.ExecuteAsync(
            ScriptLanguage.BlamWeaponVariant,
            $"{segment},{stringId:X8}",
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (runtime.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(runtime.Message);

        return new RuntimeWeaponVariantResult(
            selected.Model.Name,
            selected.BlockIndex,
            runtime.Message);
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRestore)
            throw new InvalidOperationException(
                "No compatible live armor snapshot is available to restore.");
        EnsureBridgeReady();
        foreach (MemorySnapshot snapshot in _originalVariants)
            _memory.WriteVerified(snapshot.Address, snapshot.OriginalBytes);

        uint originalStringId = BinaryPrimitives.ReadUInt32LittleEndian(
            _originalVariants[0].OriginalBytes);
        ScriptExecutionResult runtime = await _bridge.ExecuteAsync(
            ScriptLanguage.BlamObjectVariant,
            originalStringId.ToString("X8"),
            TimeSpan.FromSeconds(15),
            cancellationToken);
        if (runtime.Outcome != ScriptOutcome.Confirmed)
            throw new InvalidOperationException(runtime.Message);

        _originalVariants.Clear();
        _capturedPlayerNameAddress = 0;
    }

    private void EnsureBridgeReady()
    {
        ScriptingBridgeStatus status = _bridge.GetStatus();
        if (!status.IsRuntimeReady)
            throw new InvalidOperationException(
                "The in-game bridge is not ready. Repair/update it, restart the game, and load an offline mission.");
    }

    private void EnsureReady()
    {
        if (_definitions.SchemaCount == 0)
            _definitions.LoadDirectory(
                RuntimeTagDefinitionLocator.ResolveCampaignEvolved());
        if (!_definitions.HasSchema("bipd") ||
            !_definitions.HasSchema("hlmt") ||
            !_definitions.HasSchema("matg") ||
            !_definitions.HasSchema("scnr"))
            throw new InvalidDataException(
                "The loaded definitions do not provide the [bipd], [hlmt], [matg], and [scnr] schemas.");
        if (!_memory.IsConnected)
            throw new InvalidOperationException(
                "Connect to a loaded offline campaign mission first.");
    }

    private ModelVariantLocation FindModelVariant(
        RuntimeTagEntry player,
        int requestedIndex)
    {
        IReadOnlyList<RuntimeTagFieldValue> bipedRoot = ReadRoot(player);
        RuntimeTagFieldValue modelReference = bipedRoot.FirstOrDefault(field =>
                field.IsTagReference &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "model",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"The player biped {player.Name} does not expose its [hlmt] model reference.");
        RuntimeTagEntry model = _tags.FirstOrDefault(tag =>
                tag.Index == modelReference.ReferencedTagIndex &&
                string.Equals(tag.Group, "hlmt", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0 &&
                tag.RootCount > 0)
            ?? throw new InvalidDataException(
                $"The player biped {player.Name} does not resolve to a loaded [hlmt].");

        return ReadModelVariant(model, requestedIndex);
    }

    private ModelVariantLocation ReadModelVariant(
        RuntimeTagEntry model,
        int requestedIndex)
    {
        IReadOnlyList<RuntimeTagFieldValue> modelRoot = ReadRoot(model);
        RuntimeTagFieldValue variants = modelRoot.FirstOrDefault(field =>
                field.CanOpenBlock &&
                string.Equals(
                    field.ChildBlockDefinition,
                    "model_variant_block",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"The player model {model.Name} does not expose model variants.");
        if (requestedIndex < 0 || requestedIndex >= variants.ChildCount)
            throw new InvalidDataException(
                $"Model variant block {requestedIndex} is unavailable in {model.Name} " +
                $"({variants.ChildCount} variants loaded).");

        IReadOnlyList<RuntimeTagFieldValue> fields =
            ReadBlock(model, variants, requestedIndex);
        RuntimeTagFieldValue name = fields.FirstOrDefault(field =>
                field.Type == "string_id" &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "name",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"Model variant block {requestedIndex} has no string-id name.");
        if (name.Size != 4)
            throw new InvalidDataException(
                $"Model variant block {requestedIndex} has an unexpected name size.");

        return new ModelVariantLocation(
            model,
            requestedIndex,
            _memory.ReadBytes(name.Address, name.Size));
    }

    private RuntimeTagEntry FindWeaponModel(string segment)
    {
        string[] keywords = segment.ToLowerInvariant() switch
        {
            "assaultrifle" => ["assault_rifle"],
            "battlerifle" => ["battle_rifle"],
            "energysword" => ["energy_sword"],
            "fuelrod" => ["flak_cannon", "fuel_rod"],
            "magnum" => ["magnum"],
            "needler" => ["needler"],
            "sniperrifle" => ["sniper_rifle"],
            "spnkr" => ["rocket_launcher"],
            _ => throw new InvalidDataException(
                $"Unknown weapon customization slot {segment}."),
        };

        foreach (RuntimeTagEntry weapon in _tags
                     .Where(tag =>
                         string.Equals(
                             tag.Group,
                             "weap",
                             StringComparison.OrdinalIgnoreCase) &&
                         tag.DataAddress > 0 &&
                         keywords.Any(keyword =>
                             tag.Name.Contains(
                                 keyword,
                                 StringComparison.OrdinalIgnoreCase)))
                     .OrderBy(tag =>
                         tag.Name.EndsWith(
                             $"\\{keywords[0]}",
                             StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                     .ThenBy(tag => tag.Name.Length))
        {
            IReadOnlyList<RuntimeTagFieldValue> root;
            try { root = ReadRoot(weapon); }
            catch { continue; }
            RuntimeTagFieldValue? reference = root.FirstOrDefault(field =>
                field.IsTagReference &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "model",
                    StringComparison.OrdinalIgnoreCase));
            RuntimeTagEntry? model = reference is null
                ? null
                : _tags.FirstOrDefault(tag =>
                    tag.Index == reference.ReferencedTagIndex &&
                    string.Equals(
                        tag.Group,
                        "hlmt",
                        StringComparison.OrdinalIgnoreCase) &&
                    tag.DataAddress > 0 &&
                    tag.RootCount > 0);
            if (model is not null)
                return model;
        }

        throw new InvalidDataException(
            $"The {segment} weapon model is not loaded in this mission.");
    }

    private IEnumerable<PlayerRepresentationLocation> FindPlayerRepresentations()
    {
        foreach (RuntimeTagEntry owner in _tags.Where(tag =>
                     tag.DataAddress > 0 &&
                     (string.Equals(tag.Group, "matg", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(tag.Group, "scnr", StringComparison.OrdinalIgnoreCase))))
        {
            IReadOnlyList<RuntimeTagFieldValue> root;
            try { root = ReadRoot(owner); }
            catch { continue; }

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
                    try { fields = ReadBlock(owner, block, element); }
                    catch { continue; }

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
                    if (variant?.Size != 4 || biped is null)
                        continue;
                    yield return new PlayerRepresentationLocation(
                        owner.Group,
                        element,
                        biped,
                        variant);
                }
            }
        }
    }

    private static RuntimeTagEntry FindPlayerBiped(
        IReadOnlyList<PlayerRepresentationLocation> representations)
    {
        RuntimeTagEntry? player = representations
            .Select(location => (
                location.Biped,
                Score: PlayerNameScore(location.Biped.Name) +
                    (string.Equals(
                        location.OwnerGroup,
                        "matg",
                        StringComparison.OrdinalIgnoreCase) ? 10 : 0)))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Biped.Name.Length)
            .Select(item => item.Biped)
            .FirstOrDefault();
        return player ?? throw new InvalidDataException(
            "No loaded player representation resolves to a usable [bipd].");
    }

    private RuntimeTagEntry FindMasterChiefBiped()
    {
        RuntimeTagEntry? exact = _tags.FirstOrDefault(tag =>
            IsUsableBiped(tag) &&
            string.Equals(
                tag.Name.Replace('/', '\\'),
                @"objects\characters\spartans\spartans",
                StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        return _tags
            .Where(IsUsableBiped)
            .Select(tag => (Tag: tag, Score: PlayerNameScore(tag.Name)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Tag.Name.Length)
            .Select(item => item.Tag)
            .FirstOrDefault()
            ?? throw new InvalidDataException(
                "The Master Chief Spartan [bipd] is not loaded in this mission.");
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

    private static bool IsUsableBiped(RuntimeTagEntry tag) =>
        string.Equals(tag.Group, "bipd", StringComparison.OrdinalIgnoreCase) &&
        tag.DataAddress > 0 &&
        tag.RootCount > 0;

    private static int PlayerNameScore(string name)
    {
        string value = name.Replace('\\', '/').ToLowerInvariant();
        int score = 0;
        if (value.Contains("masterchief", StringComparison.Ordinal) ||
            value.Contains("master_chief", StringComparison.Ordinal)) score += 100;
        if (value.Contains("chief", StringComparison.Ordinal)) score += 60;
        if (value.Contains("player", StringComparison.Ordinal)) score += 40;
        if (value.Contains("spartan", StringComparison.Ordinal)) score += 30;
        return score;
    }

    private static string CleanFieldName(string name)
    {
        int description = name.IndexOfAny(['#', '{', ':', '^', '*', '!', '~']);
        string value = description >= 0 ? name[..description] : name;
        int path = value.LastIndexOf('/');
        return (path >= 0 ? value[(path + 1)..] : value).Trim();
    }

    public void Dispose() { }

    private sealed record MemorySnapshot(long Address, byte[] OriginalBytes);
    private sealed record ModelVariantLocation(
        RuntimeTagEntry Model,
        int BlockIndex,
        byte[] VariantName);
    private sealed record PlayerRepresentationLocation(
        string OwnerGroup,
        int ElementIndex,
        RuntimeTagEntry Biped,
        RuntimeTagFieldValue Variant);
}
