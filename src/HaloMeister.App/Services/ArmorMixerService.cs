using System.Buffers.Binary;
using HaloMeister.App.Models;

namespace HaloMeister.App.Services;

public sealed record ArmorMixerRegion(
    uint NameStringId,
    int RuntimeIndex,
    long PermutationsDescriptorAddress,
    byte[] PermutationsDescriptor,
    int PermutationCount,
    uint FirstPermutationStringId,
    byte[] PermutationData)
{
    public string DisplayName => RuntimeIndex switch
    {
        0 => "Body",
        1 => "Helmet",
        >= 0 => $"Model region {RuntimeIndex + 1}",
        _ => $"Model region 0x{NameStringId:X8}",
    };

    public string Detail =>
        $"region 0x{NameStringId:X8} · permutation 0x{FirstPermutationStringId:X8}";
}

public sealed record ArmorMixerVariant(
    int Index,
    uint NameStringId,
    string DisplayName,
    string Detail,
    IReadOnlyList<ArmorMixerRegion> Regions,
    string SourceModelTag,
    long SourceModelNameAddress);

// Kept private to this file while the unsupported experimental readers are
// removed from the service. They are no longer surfaced by Armor Mixer.
internal sealed record ArmorMixerSkeletonRegion(
    long PermutationsDescriptorAddress,
    byte[] PermutationsDescriptor);

internal sealed record ArmorMixerColorEntry(
    uint VariantNameStringId,
    long VariantNameAddress,
    byte[] VariantName,
    long LowerColorAddress,
    byte[] LowerColor,
    long UpperColorAddress,
    byte[] UpperColor);

internal sealed record ArmorMixerColorChannel(
    int Index,
    string DisplayName,
    IReadOnlyList<ArmorMixerColorEntry> Entries);

public sealed record ArmorMixerSession(
    string ModelTag,
    IReadOnlyList<ArmorMixerVariant> Variants,
    long ModelNameAddress);

public sealed record ArmorMixerSelection(
    ArmorMixerRegion BaseRegion,
    ArmorMixerVariant DonorVariant);

public sealed record ArmorMixerApplyResult(
    int MixedRegionCount,
    string BaseVariant,
    string RuntimeMessage);

/// <summary>
/// Composes one controlled Spartan from compatible regions already authored in
/// the loaded Spartan [hlmt]. The selected base variant's nested permutation
/// descriptors are redirected only while object_set_variant runs, and are
/// restored immediately afterward.
/// </summary>
public sealed class ArmorMixerService
{
    private static readonly IReadOnlyDictionary<int, string> KnownVariantNames =
        new Dictionary<int, string>
        {
            [0] = "Default",
            [1] = "Mark IV Chief",
            [2] = "Mark V Flawless Cowboy",
            [3] = "Splintered Warden",
            [4] = "Gestalt",
            [5] = "Timberwolf",
            [6] = "Mobile Armor Type 117",
            [7] = "Lochagos",
            [9] = "Gilded Onyx",
            [10] = "Purple",
            [11] = "Orange",
            [12] = "Blue",
            [17] = "Promotional armor 01",
            [18] = "Promotional armor 02",
            [19] = "Promotional armor 03",
            [20] = "Promotional armor 04",
            [21] = "Promotional armor 05",
            [22] = "Promotional armor 06",
            [23] = "Promotional armor 07",
            [24] = "Promotional armor 08",
            [25] = "Promotional armor 09",
            [26] = "Promotional armor 10",
            [27] = "Promotional armor 11",
            [28] = "Promotional armor 12",
            [29] = "Promotional armor 13",
            [30] = "Promotional armor 14",
            [31] = "Promotional armor 15",
            [32] = "Promotional armor 16",
        };

    private readonly RuntimeTagMemoryService _memory = RuntimeTagMemoryService.Current;
    private readonly RuntimeTagDefinitionService _definitions = new();
    private readonly ScriptingBridgeService _bridge = ScriptingBridgeService.Current;
    private ArmorMixerSession? _session;

    public ScriptingBridgeStatus BridgeStatus => _bridge.GetStatus();

    public string ConnectionSummary =>
        _memory.IsConnected
            ? $"Connected to PID {_memory.ProcessId} · {_memory.BuildProfileId}"
            : "Game connection is not ready.";

    public ArmorMixerSession Scan()
    {
        EnsureReady();
        IReadOnlyList<RuntimeTagEntry> tags = _memory.ReadTags();
        RuntimeTagEntry model = FindSpartanModel(tags);
        IReadOnlyList<RuntimeTagFieldValue> root = ReadRoot(model);
        RuntimeTagFieldValue variantsBlock = root.FirstOrDefault(field =>
                field.CanOpenBlock &&
                string.Equals(
                    field.ChildBlockDefinition,
                    "model_variant_block",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"The Spartan model {model.Name} does not expose model variants.");

        var variants = new List<ArmorMixerVariant>();
        for (int index = 0; index < variantsBlock.ChildCount; index++)
        {
            IReadOnlyList<RuntimeTagFieldValue> fields =
                ReadBlock(model, variantsBlock, index);
            RuntimeTagFieldValue? name = FindField(fields, "name", "string_id");
            RuntimeTagFieldValue? regionsBlock = fields.FirstOrDefault(field =>
                string.Equals(
                    field.ChildBlockDefinition,
                    "model_variant_region_block",
                    StringComparison.OrdinalIgnoreCase));
            if (name?.Size != sizeof(uint) ||
                regionsBlock is null ||
                regionsBlock.ChildCount <= 0 ||
                regionsBlock.ChildAddress <= 0)
                continue;

            var regions = new List<ArmorMixerRegion>();
            for (int regionIndex = 0;
                 regionIndex < regionsBlock.ChildCount;
                 regionIndex++)
            {
                IReadOnlyList<RuntimeTagFieldValue> regionFields =
                    ReadBlock(model, regionsBlock, regionIndex);
                RuntimeTagFieldValue? regionName =
                    FindField(regionFields, "region name", "string_id");
                RuntimeTagFieldValue? runtimeIndex =
                    FindField(regionFields, "runtime region index", "char_integer");
                RuntimeTagFieldValue? permutations =
                    regionFields.FirstOrDefault(field =>
                        string.Equals(
                            field.ChildBlockDefinition,
                            "model_variant_permutation_block",
                            StringComparison.OrdinalIgnoreCase));
                if (regionName?.Size != sizeof(uint) ||
                    permutations?.Size != 12 ||
                    permutations.ChildCount <= 0 ||
                    permutations.ChildAddress <= 0)
                    continue;

                uint regionStringId = BinaryPrimitives.ReadUInt32LittleEndian(
                    _memory.ReadBytes(regionName.Address, sizeof(uint)));
                int runtimeRegionIndex = runtimeIndex?.Size == 1
                    ? _memory.ReadBytes(runtimeIndex.Address, 1)[0]
                    : -1;
                IReadOnlyList<RuntimeTagFieldValue> permutationFields =
                    ReadBlock(model, permutations, 0);
                RuntimeTagFieldValue? permutationName =
                    FindField(
                        permutationFields,
                        "permutation name",
                        "string_id");
                uint permutationStringId = permutationName?.Size == sizeof(uint)
                    ? BinaryPrimitives.ReadUInt32LittleEndian(
                        _memory.ReadBytes(
                            permutationName.Address,
                            sizeof(uint)))
                    : 0;
                regions.Add(new ArmorMixerRegion(
                    regionStringId,
                    runtimeRegionIndex,
                    permutations.Address,
                    _memory.ReadBytes(permutations.Address, permutations.Size),
                    permutations.ChildCount,
                    permutationStringId,
                    _memory.ReadBytes(
                        permutations.ChildAddress,
                        checked(
                            permutations.ChildCount *
                            permutations.ChildElementSize))));
            }

            if (regions.Count == 0)
                continue;

            uint nameStringId = BinaryPrimitives.ReadUInt32LittleEndian(
                _memory.ReadBytes(name.Address, sizeof(uint)));
            string displayName = KnownVariantNames.TryGetValue(index, out string? known)
                ? known
                : $"Variant {index + 1:00}";
            variants.Add(new ArmorMixerVariant(
                index,
                nameStringId,
                displayName,
                $"Variant {index + 1:00} · string-id 0x{nameStringId:X8} · {regions.Count} mixable region(s)",
                regions,
                model.Name,
                model.NameAddress));
        }

        if (variants.Count == 0)
            throw new InvalidDataException(
                "The loaded Spartan model has no variants with mixable region permutations.");

        HashSet<uint> variableRegions = variants
            .SelectMany(variant => variant.Regions)
            .GroupBy(region => region.NameStringId)
            .Where(group =>
                group.Select(region => Convert.ToHexString(region.PermutationData))
                    .Distinct(StringComparer.Ordinal)
                    .Skip(1)
                    .Any())
            .Select(group => group.Key)
            .ToHashSet();
        variants = variants
            .Select(variant => variant with
            {
                Regions = variant.Regions
                    .Where(region => variableRegions.Contains(region.NameStringId))
                    .ToArray(),
            })
            .Where(variant => variant.Regions.Count > 0)
            .ToList();
        if (variants.Count == 0)
            throw new InvalidDataException(
                "The loaded Spartan variants do not contain any regions whose permutations differ.");

        _session = new ArmorMixerSession(
            model.Name,
            variants,
            model.NameAddress);
        return _session;
    }

    public async Task<ArmorMixerApplyResult> ApplyAsync(
        ArmorMixerVariant baseVariant,
        IReadOnlyList<ArmorMixerSelection> selections,
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        EnsureBridgeReady();
        ArmorMixerSession session = ValidateSession();
        ArmorMixerVariant liveBase = session.Variants.FirstOrDefault(
                variant =>
                    variant.Index == baseVariant.Index &&
                    variant.NameStringId == baseVariant.NameStringId &&
                    variant.SourceModelNameAddress ==
                        baseVariant.SourceModelNameAddress)
            ?? throw new InvalidOperationException(
                "The selected base variant is no longer part of this armor-mixer session.");

        var patches = new List<DescriptorPatch>();
        foreach (ArmorMixerSelection selection in selections)
        {
            ArmorMixerRegion? baseRegion = liveBase.Regions.FirstOrDefault(region =>
                region.NameStringId == selection.BaseRegion.NameStringId);
            ArmorMixerVariant? donor = session.Variants.FirstOrDefault(variant =>
                    variant.Index == selection.DonorVariant.Index &&
                    variant.NameStringId ==
                        selection.DonorVariant.NameStringId &&
                    variant.SourceModelNameAddress ==
                        selection.DonorVariant.SourceModelNameAddress);
            ArmorMixerRegion? donorRegion = donor?.Regions.FirstOrDefault(region =>
                region.NameStringId == selection.BaseRegion.NameStringId);
            if (baseRegion is null || donor is null || donorRegion is null)
                throw new InvalidOperationException(
                    "A selected region or donor variant is no longer available.");
            RuntimeTagEntry? donorModel = _memory.ReadTags().FirstOrDefault(tag =>
                tag.NameAddress == donor.SourceModelNameAddress &&
                string.Equals(
                    tag.Name,
                    donor.SourceModelTag,
                    StringComparison.OrdinalIgnoreCase));
            if (donorModel is null)
                throw new InvalidOperationException(
                    $"The donor model {donor.SourceModelTag} is no longer loaded.");

            byte[] current = _memory.ReadBytes(
                baseRegion.PermutationsDescriptorAddress,
                baseRegion.PermutationsDescriptor.Length);
            if (!current.AsSpan().SequenceEqual(baseRegion.PermutationsDescriptor))
                throw new InvalidOperationException(
                    $"{baseRegion.DisplayName} changed after the scan. Scan the model again before applying.");
            if (current.AsSpan().SequenceEqual(donorRegion.PermutationsDescriptor))
                continue;

            patches.Add(new DescriptorPatch(
                baseRegion.PermutationsDescriptorAddress,
                current,
                donorRegion.PermutationsDescriptor));
        }

        var completed = new List<DescriptorPatch>();
        ScriptExecutionResult? runtime = null;
        Exception? operationError = null;
        try
        {
            foreach (DescriptorPatch patch in patches)
            {
                completed.Add(patch);
                _memory.WriteVerified(patch.Address, patch.Replacement);
            }

            runtime = await _bridge.ExecuteAsync(
                ScriptLanguage.BlamObjectVariant,
                liveBase.NameStringId.ToString("X8"),
                TimeSpan.FromSeconds(15),
                cancellationToken);
            if (runtime.Outcome != ScriptOutcome.Confirmed)
                throw new InvalidOperationException(runtime.Message);

        }
        catch (Exception ex)
        {
            operationError = ex;
        }

        Exception? restoreError = null;
        foreach (DescriptorPatch patch in completed.AsEnumerable().Reverse())
        {
            try { _memory.WriteVerified(patch.Address, patch.Original); }
            catch (Exception ex) { restoreError ??= ex; }
        }

        if (restoreError is not null)
            throw new InvalidOperationException(
                "One or more temporary model descriptors could not be restored. " +
                "Leave the mission before using the mixer again.",
                restoreError);
        if (operationError is not null)
            throw operationError;

        return new ArmorMixerApplyResult(
            selections.Count(selection =>
                selection.DonorVariant.SourceModelNameAddress !=
                    liveBase.SourceModelNameAddress ||
                selection.DonorVariant.Index != liveBase.Index),
            liveBase.DisplayName,
            runtime!.Message);
    }

    private ArmorMixerSession ValidateSession()
    {
        ArmorMixerSession session = _session
            ?? throw new InvalidOperationException("Scan the Spartan model first.");
        IReadOnlyList<RuntimeTagEntry> tags = _memory.ReadTags();
        RuntimeTagEntry? model = tags.FirstOrDefault(tag =>
            tag.NameAddress == session.ModelNameAddress &&
            string.Equals(tag.Group, "hlmt", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(tag.Name, session.ModelTag, StringComparison.OrdinalIgnoreCase));
        return model is not null
            ? session
            : throw new InvalidOperationException(
                "The mission tag table changed after the scan. Scan the Spartan model again.");
    }

    private void EnsureBridgeReady()
    {
        ScriptingBridgeStatus status = _bridge.GetStatus();
        if (!status.IsRuntimeReady || status.IsStale)
            throw new InvalidOperationException(
                "The in-game bridge is not ready. Repair/update it, restart the game, and load an offline mission.");
    }

    private void EnsureReady()
    {
        if (_definitions.SchemaCount == 0)
            _definitions.LoadDirectory(
                RuntimeTagDefinitionLocator.ResolveCampaignEvolved());
        if (!_definitions.HasSchema("hlmt"))
            throw new InvalidDataException(
                "The loaded definitions do not provide the [hlmt] schema.");
        if (!_memory.IsConnected)
            throw new InvalidOperationException(
                "Connect to a loaded offline campaign mission first.");
    }

    private static RuntimeTagEntry FindSpartanModel(
        IReadOnlyList<RuntimeTagEntry> tags)
    {
        RuntimeTagEntry? exact = tags.FirstOrDefault(tag =>
            IsUsableModel(tag) &&
            string.Equals(
                tag.Name.Replace('/', '\\'),
                @"objects\characters\spartans\spartans",
                StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        return tags
            .Where(IsUsableModel)
            .Where(tag =>
                tag.Name.Contains("spartan", StringComparison.OrdinalIgnoreCase) ||
                tag.Name.Contains("masterchief", StringComparison.OrdinalIgnoreCase) ||
                tag.Name.Contains("master_chief", StringComparison.OrdinalIgnoreCase))
            .OrderBy(tag =>
                tag.Name.Contains(
                    @"characters\spartans",
                    StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(tag => tag.Name.Length)
            .FirstOrDefault()
            ?? throw new InvalidDataException(
                "The shared Spartan [hlmt] model is not loaded in this mission.");
    }

    private RuntimeTagEntry FindRepresentedSpartanBiped(
        IReadOnlyList<RuntimeTagEntry> tags,
        RuntimeTagEntry spartanModel)
    {
        var candidates = new List<(RuntimeTagEntry Biped, int Score)>();
        foreach (RuntimeTagEntry owner in tags.Where(tag =>
                     tag.DataAddress > 0 &&
                     (string.Equals(
                          tag.Group,
                          "matg",
                          StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(
                          tag.Group,
                          "scnr",
                          StringComparison.OrdinalIgnoreCase))))
        {
            IReadOnlyList<RuntimeTagFieldValue> root;
            try { root = ReadRoot(owner); }
            catch { continue; }
            foreach (RuntimeTagFieldValue representations in root.Where(field =>
                         string.Equals(
                             field.ChildBlockDefinition,
                             "player_representation_block",
                             StringComparison.OrdinalIgnoreCase) &&
                         field.ChildAddress > 0 &&
                         field.ChildCount > 0))
            {
                for (int index = 0;
                     index < representations.ChildCount;
                     index++)
                {
                    IReadOnlyList<RuntimeTagFieldValue> fields;
                    try
                    {
                        fields = ReadBlock(owner, representations, index);
                    }
                    catch { continue; }
                    RuntimeTagFieldValue? unit = fields.FirstOrDefault(field =>
                        field.IsTagReference &&
                        string.Equals(
                            CleanFieldName(field.Name),
                            "third person unit",
                            StringComparison.OrdinalIgnoreCase));
                    RuntimeTagEntry? biped = unit is null
                        ? null
                        : tags.FirstOrDefault(tag =>
                            tag.Index == unit.ReferencedTagIndex &&
                            IsUsableBiped(tag));
                    if (biped is null)
                        continue;

                    int score = string.Equals(
                        owner.Group,
                        "matg",
                        StringComparison.OrdinalIgnoreCase) ? 20 : 0;
                    string path = biped.Name.ToLowerInvariant();
                    if (path.Contains("masterchief") ||
                        path.Contains("master_chief") ||
                        path.Contains("chief"))
                        score += 100;
                    if (path.Contains("spartan"))
                        score += 40;
                    try
                    {
                        RuntimeTagFieldValue? model = ReadRoot(biped)
                            .FirstOrDefault(field =>
                                field.IsTagReference &&
                                string.Equals(
                                    CleanFieldName(field.Name),
                                    "model",
                                    StringComparison.OrdinalIgnoreCase));
                        if (model?.ReferencedTagIndex == spartanModel.Index)
                            score += 500;
                    }
                    catch { }
                    candidates.Add((biped, score));
                }
            }
        }

        RuntimeTagEntry? represented = candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Biped.Name.Length)
            .Select(candidate => candidate.Biped)
            .FirstOrDefault();
        if (represented is not null)
            return represented;

        return tags.FirstOrDefault(tag =>
                   IsUsableBiped(tag) &&
                   string.Equals(
                       tag.Name.Replace('/', '\\'),
                       @"objects\characters\spartans\spartans",
                       StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidDataException(
                   "No represented Spartan [bipd] is loaded in this mission.");
    }

    private IReadOnlyList<ArmorMixerColorChannel> ReadColorChannels(
        RuntimeTagEntry biped)
    {
        IReadOnlyList<RuntimeTagFieldValue> root = ReadRoot(biped);
        RuntimeTagFieldValue colors = root.FirstOrDefault(field =>
                string.Equals(
                    field.ChildBlockDefinition,
                    "object_change_colors",
                    StringComparison.OrdinalIgnoreCase) &&
                field.ChildAddress > 0 &&
                field.ChildCount > 0)
            ?? throw new InvalidDataException(
                $"The Spartan biped {biped.Name} does not expose object change colors.");

        string[] names = ["Primary", "Secondary", "Tertiary", "Quaternary"];
        var result = new List<ArmorMixerColorChannel>();
        int count = Math.Min(colors.ChildCount, names.Length);
        for (int channelIndex = 0; channelIndex < count; channelIndex++)
        {
            IReadOnlyList<RuntimeTagFieldValue> channelFields =
                ReadBlock(biped, colors, channelIndex);
            RuntimeTagFieldValue? initial = channelFields.FirstOrDefault(field =>
                string.Equals(
                    field.ChildBlockDefinition,
                    "object_change_color_initial_permutation",
                    StringComparison.OrdinalIgnoreCase));
            if (initial is null ||
                initial.ChildAddress <= 0 ||
                initial.ChildCount <= 0)
                continue;

            var entries = new List<ArmorMixerColorEntry>();
            for (int entryIndex = 0;
                 entryIndex < initial.ChildCount;
                 entryIndex++)
            {
                IReadOnlyList<RuntimeTagFieldValue> fields =
                    ReadBlock(biped, initial, entryIndex);
                RuntimeTagFieldValue? lower =
                    FindField(fields, "color lower bound", "real_rgb_color");
                RuntimeTagFieldValue? upper =
                    FindField(fields, "color upper bound", "real_rgb_color");
                RuntimeTagFieldValue? variantName =
                    FindField(fields, "variant name", "string_id");
                if (lower?.Size != 12 ||
                    upper?.Size != 12 ||
                    variantName?.Size != sizeof(uint))
                    continue;

                entries.Add(new ArmorMixerColorEntry(
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        _memory.ReadBytes(
                            variantName.Address,
                            sizeof(uint))),
                    variantName.Address,
                    _memory.ReadBytes(
                        variantName.Address,
                        sizeof(uint)),
                    lower.Address,
                    _memory.ReadBytes(lower.Address, lower.Size),
                    upper.Address,
                    _memory.ReadBytes(upper.Address, upper.Size)));
            }

            result.Add(new ArmorMixerColorChannel(
                channelIndex,
                names[channelIndex],
                entries));
        }

        if (result.Count != names.Length)
            throw new InvalidDataException(
                $"Expected four Spartan color channels, but found {result.Count}.");
        return result;
    }

    private IReadOnlyList<ArmorMixerVariant> ReadExperimentalHeadDonors(
        IReadOnlyList<RuntimeTagEntry> tags,
        RuntimeTagEntry spartanModel,
        uint helmetRegionId)
    {
        var donors = new List<ArmorMixerVariant>();
        var bipedModelIndices = new HashSet<int>();
        foreach (RuntimeTagEntry biped in tags.Where(IsUsableBiped))
        {
            try
            {
                RuntimeTagFieldValue? modelReference = ReadRoot(biped)
                    .FirstOrDefault(field =>
                        field.IsTagReference &&
                        string.Equals(
                            CleanFieldName(field.Name),
                            "model",
                            StringComparison.OrdinalIgnoreCase));
                if (modelReference?.ReferencedTagIndex is int modelIndex &&
                    modelIndex >= 0)
                    bipedModelIndices.Add(modelIndex);
            }
            catch
            {
                // A partially loaded biped is irrelevant to the optional list.
            }
        }
        IEnumerable<RuntimeTagEntry> models = tags
            .Where(IsUsableModel)
            .Where(model =>
                model.NameAddress != spartanModel.NameAddress &&
                bipedModelIndices.Contains(model.Index));
        foreach (RuntimeTagEntry model in models)
        {
            try
            {
                ArmorMixerSkeletonRegion? skeletonHelmet =
                    ReadSkeletonHelmetRegion(tags, model, helmetRegionId);
                if (skeletonHelmet is null)
                    continue;
                RuntimeTagFieldValue? variantsBlock = ReadRoot(model)
                    .FirstOrDefault(field =>
                        field.CanOpenBlock &&
                        string.Equals(
                            field.ChildBlockDefinition,
                            "model_variant_block",
                            StringComparison.OrdinalIgnoreCase));
                if (variantsBlock is null ||
                    variantsBlock.ChildAddress <= 0 ||
                    variantsBlock.ChildCount <= 0)
                    continue;

                for (int variantIndex = 0;
                     variantIndex < variantsBlock.ChildCount;
                     variantIndex++)
                {
                    IReadOnlyList<RuntimeTagFieldValue> fields =
                        ReadBlock(model, variantsBlock, variantIndex);
                    RuntimeTagFieldValue? name =
                        FindField(fields, "name", "string_id");
                    RuntimeTagFieldValue? regionsBlock =
                        fields.FirstOrDefault(field =>
                            string.Equals(
                                field.ChildBlockDefinition,
                                "model_variant_region_block",
                                StringComparison.OrdinalIgnoreCase));
                    if (name?.Size != sizeof(uint) ||
                        regionsBlock is null ||
                        regionsBlock.ChildAddress <= 0 ||
                        regionsBlock.ChildCount <= 0)
                        continue;

                    ArmorMixerRegion? helmet = null;
                    for (int regionIndex = 0;
                         regionIndex < regionsBlock.ChildCount;
                         regionIndex++)
                    {
                        IReadOnlyList<RuntimeTagFieldValue> regionFields =
                            ReadBlock(model, regionsBlock, regionIndex);
                        RuntimeTagFieldValue? regionName =
                            FindField(
                                regionFields,
                                "region name",
                                "string_id");
                        RuntimeTagFieldValue? permutations =
                            regionFields.FirstOrDefault(field =>
                                string.Equals(
                                    field.ChildBlockDefinition,
                                    "model_variant_permutation_block",
                                    StringComparison.OrdinalIgnoreCase));
                        if (regionName?.Size != sizeof(uint) ||
                            permutations?.Size != 12 ||
                            permutations.ChildAddress <= 0 ||
                            permutations.ChildCount <= 0)
                            continue;
                        uint regionId =
                            BinaryPrimitives.ReadUInt32LittleEndian(
                                _memory.ReadBytes(
                                    regionName.Address,
                                    sizeof(uint)));
                        if (regionId != helmetRegionId)
                            continue;

                        IReadOnlyList<RuntimeTagFieldValue> permutationFields =
                            ReadBlock(model, permutations, 0);
                        RuntimeTagFieldValue? permutationName =
                            FindField(
                                permutationFields,
                                "permutation name",
                                "string_id");
                        uint permutationId =
                            permutationName?.Size == sizeof(uint)
                                ? BinaryPrimitives.ReadUInt32LittleEndian(
                                    _memory.ReadBytes(
                                        permutationName.Address,
                                        sizeof(uint)))
                                : 0;
                        helmet = new ArmorMixerRegion(
                            regionId,
                            1,
                            permutations.Address,
                            _memory.ReadBytes(
                                permutations.Address,
                                permutations.Size),
                            permutations.ChildCount,
                            permutationId,
                            _memory.ReadBytes(
                                permutations.ChildAddress,
                                checked(
                                    permutations.ChildCount *
                                    permutations.ChildElementSize)));
                        break;
                    }
                    if (helmet is null)
                        continue;

                    uint variantName =
                        BinaryPrimitives.ReadUInt32LittleEndian(
                            _memory.ReadBytes(name.Address, sizeof(uint)));
                    string species = FriendlyModelName(model.Name);
                    donors.Add(new ArmorMixerVariant(
                        variantIndex,
                        variantName,
                        $"YOLO · {species} · variant {variantIndex + 1:00}",
                        $"{model.Name} · variant 0x{variantName:X8} · foreign skeleton",
                        [helmet],
                        model.Name,
                        model.NameAddress));
                }
            }
            catch
            {
                // Experimental donors are best-effort. A malformed or partially
                // loaded character model must not prevent normal Spartan mixing.
            }
        }
        return donors
            .OrderBy(donor => donor.SourceModelTag, StringComparer.OrdinalIgnoreCase)
            .ThenBy(donor => donor.Index)
            .ToArray();
    }

    private ArmorMixerSkeletonRegion? ReadSkeletonHelmetRegion(
        IReadOnlyList<RuntimeTagEntry> tags,
        RuntimeTagEntry model,
        uint helmetRegionId)
    {
        RuntimeTagFieldValue? skeletonReference = ReadRoot(model)
            .FirstOrDefault(field =>
                field.IsTagReference &&
                string.Equals(
                    CleanFieldName(field.Name),
                    "skeleton model",
                    StringComparison.OrdinalIgnoreCase));
        RuntimeTagEntry? skeleton = skeletonReference is null
            ? null
            : tags.FirstOrDefault(tag =>
                tag.Index == skeletonReference.ReferencedTagIndex &&
                string.Equals(
                    tag.Group,
                    "skel",
                    StringComparison.OrdinalIgnoreCase));
        if (skeleton is null)
            return null;

        RuntimeTagFieldValue? regions = ReadRoot(skeleton)
            .FirstOrDefault(field =>
                string.Equals(
                    field.ChildBlockDefinition,
                    "skeleton_model_region_block",
                    StringComparison.OrdinalIgnoreCase) &&
                field.ChildAddress > 0 &&
                field.ChildCount > 0);
        if (regions is null)
            return null;
        for (int index = 0; index < regions.ChildCount; index++)
        {
            IReadOnlyList<RuntimeTagFieldValue> fields =
                ReadBlock(skeleton, regions, index);
            RuntimeTagFieldValue? name =
                FindField(fields, "name", "string_id");
            RuntimeTagFieldValue? permutations = fields.FirstOrDefault(field =>
                string.Equals(
                    field.ChildBlockDefinition,
                    "skeleton_model_permutation_block",
                    StringComparison.OrdinalIgnoreCase));
            if (name?.Size != sizeof(uint) ||
                permutations?.Size != 12 ||
                permutations.ChildAddress <= 0 ||
                permutations.ChildCount <= 0)
                continue;
            uint nameId = BinaryPrimitives.ReadUInt32LittleEndian(
                _memory.ReadBytes(name.Address, sizeof(uint)));
            if (nameId != helmetRegionId)
                continue;
            return new ArmorMixerSkeletonRegion(
                permutations.Address,
                _memory.ReadBytes(
                    permutations.Address,
                    permutations.Size));
        }
        return null;
    }

    private static string FriendlyModelName(string tagPath)
    {
        string value = tagPath.Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault() ?? tagPath;
        return string.Join(
            ' ',
            value.Split(
                    ['_', '-'],
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(part =>
                    part.Length == 0
                        ? part
                        : char.ToUpperInvariant(part[0]) + part[1..]));
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

    private static RuntimeTagFieldValue? FindField(
        IEnumerable<RuntimeTagFieldValue> fields,
        string name,
        string type) =>
        fields.FirstOrDefault(field =>
            string.Equals(field.Type, type, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                CleanFieldName(field.Name),
                name,
                StringComparison.OrdinalIgnoreCase));

    private static bool IsUsableModel(RuntimeTagEntry tag) =>
        string.Equals(tag.Group, "hlmt", StringComparison.OrdinalIgnoreCase) &&
        tag.DataAddress > 0 &&
        tag.RootCount > 0;

    private static bool IsUsableBiped(RuntimeTagEntry tag) =>
        string.Equals(tag.Group, "bipd", StringComparison.OrdinalIgnoreCase) &&
        tag.DataAddress > 0 &&
        tag.RootCount > 0;

    private static string CleanFieldName(string name)
    {
        int description = name.IndexOfAny(['#', '{', ':', '^', '*', '!', '~']);
        string value = description >= 0 ? name[..description] : name;
        int path = value.LastIndexOf('/');
        return (path >= 0 ? value[(path + 1)..] : value).Trim();
    }

    private sealed record DescriptorPatch(
        long Address,
        byte[] Original,
        byte[] Replacement);
}
