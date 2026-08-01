using System.Buffers.Binary;
using HaloMeister.App.Localization;
using HaloMeister.App.Models;

namespace HaloMeister.App.Services;

public sealed class PlayerModifierOption
{
    public PlayerModifierOption(string labelKey, int value)
    {
        LabelKey = labelKey;
        Value = value;
    }

    public string LabelKey { get; }
    public int Value { get; }
    public string Label => PlayerModifiersService.LocalizeOptionLabel(LabelKey);
}

public sealed record PlayerModifierDefinition(
    string Name,
    string DisplayNameKey,
    string DescriptionKey,
    string TraitBlock,
    string TraitField,
    bool IsInt32,
    IReadOnlyList<PlayerModifierOption> Options);

public sealed class PlayerModifierItem
{
    public required PlayerModifierDefinition Definition { get; init; }
    public required PlayerModifierOption SelectedOption { get; set; }

    public string Name => Definition.Name;
    public string DisplayName => L.Get(Definition.DisplayNameKey);
    public string Description => L.Get(Definition.DescriptionKey);
    public IReadOnlyList<PlayerModifierOption> Options => Definition.Options;
}

public sealed class PlayerModifiersService
{
    private readonly RuntimeTagMemoryService _memory = RuntimeTagMemoryService.Current;
    private readonly RuntimeTagDefinitionService _definitions = new();
    private readonly Dictionary<string, ModifierSnapshot> _snapshots =
        new(StringComparer.Ordinal);
    private int _processId;

    public static PlayerModifiersService Current { get; } = new();

    public static IReadOnlyList<PlayerModifierDefinition> Catalog { get; } =
    [
        new(
            "health",
            "player_mod.health",
            "player_mod.health_desc",
            "shield traits",
            "damage resistance",
            false,
            Options(
                ("player_mod.opt_default", 0), ("10%", 1), ("50%", 2), ("90%", 3),
                ("100%", 4), ("110%", 5), ("150%", 6), ("200%", 7),
                ("300%", 8), ("500%", 9), ("1000%", 10), ("2000%", 11),
                ("player_mod.opt_invulnerable", 12))),
        new(
            "shield_strength",
            "player_mod.shield_strength",
            "player_mod.shield_strength_desc",
            "shield traits",
            "shield multiplier",
            false,
            Options(
                ("player_mod.opt_default", 0), ("player_mod.opt_no_shields", 1), ("1x", 2), ("1.5x", 3),
                ("2x", 4), ("3x", 5), ("4x", 6))),
        new(
            "shield_recharge",
            "player_mod.shield_recharge",
            "player_mod.shield_recharge_desc",
            "shield traits",
            "shield recharge rate",
            false,
            Options(
                ("player_mod.opt_default", 0), ("-25%", 1), ("-10%", 2), ("-5%", 3),
                ("player_mod.opt_never", 4), ("10%", 5), ("25%", 6), ("50%", 7),
                ("75%", 8), ("90%", 9), ("100%", 10), ("110%", 11),
                ("125%", 12), ("150%", 13), ("200%", 14))),
        new(
            "damage",
            "player_mod.damage",
            "player_mod.damage_desc",
            "weapon traits",
            "damage modifier",
            false,
            DamageOptions()),
        new(
            "melee_damage",
            "player_mod.melee_damage",
            "player_mod.melee_damage_desc",
            "weapon traits",
            "melee damage modifier",
            false,
            DamageOptions()),
        new(
            "speed",
            "player_mod.speed",
            "player_mod.speed_desc",
            "movement traits",
            "speed multiplier",
            false,
            Options(
                ("player_mod.opt_default", 0), ("0%", 1), ("25%", 2), ("50%", 3),
                ("75%", 4), ("90%", 5), ("100%", 6), ("110%", 7),
                ("120%", 8), ("130%", 9), ("140%", 10), ("150%", 11),
                ("160%", 12), ("170%", 13), ("180%", 14), ("190%", 15),
                ("200%", 16), ("300%", 17))),
        new(
            "jump_height",
            "player_mod.jump_height",
            "player_mod.jump_height_desc",
            "movement traits",
            "jump multiplier",
            true,
            Options(
                ("player_mod.opt_default", -1), ("player_mod.opt_disabled", 0), ("50%", 50), ("75%", 75),
                ("100%", 100), ("125%", 125), ("150%", 150), ("200%", 200),
                ("300%", 300), ("500%", 500))),
        new(
            "gravity",
            "player_mod.gravity",
            "player_mod.gravity_desc",
            "movement traits",
            "gravity multiplier",
            false,
            Options(
                ("player_mod.opt_default", 0), ("50%", 1), ("75%", 2), ("100%", 3),
                ("110%", 4), ("120%", 5), ("130%", 6), ("140%", 7),
                ("150%", 8), ("160%", 9), ("170%", 10), ("180%", 11),
                ("190%", 12), ("200%", 13))),
        new(
            "double_jump",
            "player_mod.double_jump",
            "player_mod.double_jump_desc",
            "movement traits",
            "double jump",
            false,
            Options(
                ("player_mod.opt_default", 0), ("player_mod.opt_off", 1), ("player_mod.opt_on", 2),
                ("player_mod.opt_on_lunge", 3))),
        new(
            "vampirism",
            "player_mod.vampirism",
            "player_mod.vampirism_desc",
            "shield traits",
            "vampirism",
            false,
            Options(
                ("player_mod.opt_default", 0), ("player_mod.opt_off", 1), ("10%", 2), ("25%", 3),
                ("50%", 4), ("100%", 5))),
        new(
            "active_camo",
            "player_mod.active_camo",
            "player_mod.active_camo_desc",
            "appearance traits",
            "active camo setting",
            false,
            Options(
                ("player_mod.opt_default", 0), ("player_mod.opt_off", 1), ("player_mod.opt_poor", 2),
                ("player_mod.opt_good", 3), ("player_mod.opt_excellent", 4), ("player_mod.opt_invisible", 5))),
    ];

    public bool HasChanges => _snapshots.Count > 0;

    internal static string LocalizeOptionLabel(string labelKey)
        => labelKey.StartsWith("player_mod.", StringComparison.Ordinal)
            ? L.Get(labelKey)
            : labelKey;

    public IReadOnlyList<PlayerModifierItem> Read()
    {
        IReadOnlyDictionary<string, RuntimeTagFieldValue> fields = ResolveFields();
        return Catalog.Select(definition =>
        {
            RuntimeTagFieldValue field = fields[definition.Name];
            int value = ReadValue(field, definition.IsInt32);
            PlayerModifierOption selected = definition.Options.FirstOrDefault(
                    option => option.Value == value)
                ?? new PlayerModifierOption(
                    L.Format("player_mod.opt_unknown", value),
                    value);
            return new PlayerModifierItem
            {
                Definition = definition,
                SelectedOption = selected,
            };
        }).ToArray();
    }

    public void Set(string name, int value)
    {
        PlayerModifierDefinition definition = Catalog.FirstOrDefault(
                item => item.Name == name)
            ?? throw new ArgumentOutOfRangeException(nameof(name));
        if (!definition.Options.Any(option => option.Value == value))
            throw new ArgumentOutOfRangeException(nameof(value));

        IReadOnlyDictionary<string, RuntimeTagFieldValue> fields = ResolveFields();
        RuntimeTagFieldValue field = fields[name];
        byte[] current = _memory.ReadBytes(
            field.Address,
            definition.IsInt32 ? sizeof(int) : sizeof(byte));
        if (!_snapshots.TryGetValue(name, out ModifierSnapshot? snapshot) ||
            snapshot.Address != field.Address)
            _snapshots[name] = new ModifierSnapshot(field.Address, current, current);

        byte[] replacement = Encode(value, definition.IsInt32);
        _memory.WriteVerified(field.Address, replacement);
        _snapshots[name] = _snapshots[name] with { Applied = replacement };
    }

    public int Restore()
    {
        EnsureConnected();
        if (_processId != _memory.ProcessId)
        {
            _snapshots.Clear();
            _processId = _memory.ProcessId;
            return 0;
        }

        int restored = 0;
        foreach ((string name, ModifierSnapshot snapshot) in _snapshots.ToArray())
        {
            byte[] current;
            try { current = _memory.ReadBytes(snapshot.Address, snapshot.Applied.Length); }
            catch
            {
                _snapshots.Remove(name);
                continue;
            }

            if (!current.AsSpan().SequenceEqual(snapshot.Applied))
            {
                _snapshots.Remove(name);
                continue;
            }
            _memory.WriteVerified(snapshot.Address, snapshot.Original);
            _snapshots.Remove(name);
            restored++;
        }
        return restored;
    }

    private IReadOnlyDictionary<string, RuntimeTagFieldValue> ResolveFields()
    {
        EnsureConnected();
        EnsureDefinitions();
        TrackProcess();

        RuntimeTagEntry globals = _memory.ReadTags()
            .Where(tag =>
                tag.Group.Equals("matg", StringComparison.OrdinalIgnoreCase) &&
                tag.DataAddress > 0)
            .OrderByDescending(tag =>
                tag.Name.Contains("globals", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                L.Get("player_mod.error_globals_tag_missing"));

        RuntimeTagFieldValue defaultTraits = _definitions.ReadRootFields(
                globals.Group,
                globals.DataAddress,
                _memory.ReadBytes,
                ResolveOffset)
            .FirstOrDefault(field =>
                field.Type == "block" &&
                field.Name.Equals(
                    "default player traits",
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                L.Get("player_mod.error_no_default_traits"));
        if (defaultTraits.ChildBlockDefinition is null ||
            defaultTraits.ChildAddress <= 0 ||
            defaultTraits.ChildCount < 1)
        {
            throw new InvalidDataException(
                L.Get("player_mod.error_traits_empty"));
        }

        IReadOnlyList<RuntimeTagFieldValue> traitGroups =
            _definitions.ReadBlockFields(
                globals.Group,
                defaultTraits.ChildBlockDefinition,
                defaultTraits.ChildAddress,
                0,
                _memory.ReadBytes,
                ResolveOffset);
        var result = new Dictionary<string, RuntimeTagFieldValue>(
            StringComparer.Ordinal);
        foreach (IGrouping<string, PlayerModifierDefinition> group in
                 Catalog.GroupBy(item => item.TraitBlock))
        {
            RuntimeTagFieldValue block = traitGroups.FirstOrDefault(field =>
                    field.Type == "block" &&
                    field.Name.Equals(group.Key, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException(
                    L.Format("player_mod.error_missing_block", group.Key));
            if (block.ChildBlockDefinition is null ||
                block.ChildAddress <= 0 ||
                block.ChildCount < 1)
            {
                throw new InvalidDataException(
                    L.Format("player_mod.error_block_empty", group.Key));
            }

            IReadOnlyList<RuntimeTagFieldValue> values =
                _definitions.ReadBlockFields(
                    globals.Group,
                    block.ChildBlockDefinition,
                    block.ChildAddress,
                    0,
                    _memory.ReadBytes,
                    ResolveOffset);
            foreach (PlayerModifierDefinition definition in group)
            {
                result[definition.Name] = values.FirstOrDefault(field =>
                        field.Name.StartsWith(
                            definition.TraitField,
                            StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException(
                        L.Format(
                            "player_mod.error_missing_field",
                            group.Key,
                            definition.TraitField));
            }
        }

        return result;
    }

    private void EnsureConnected()
    {
        if (!_memory.IsConnected)
            _memory.Connect();
    }

    private void EnsureDefinitions()
    {
        if (_definitions.SchemaCount > 0)
            return;
        _definitions.LoadDirectory(
            RuntimeTagDefinitionLocator.ResolveCampaignEvolved());
    }

    private void TrackProcess()
    {
        if (_processId == _memory.ProcessId)
            return;
        _snapshots.Clear();
        _processId = _memory.ProcessId;
    }

    private long? ResolveOffset(uint encodedOffset) =>
        _memory.TryResolveOffset(encodedOffset, out long address)
            ? address
            : null;

    private int ReadValue(RuntimeTagFieldValue field, bool isInt32)
    {
        byte[] bytes = _memory.ReadBytes(
            field.Address,
            isInt32 ? sizeof(int) : sizeof(byte));
        return isInt32 ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : bytes[0];
    }

    private static byte[] Encode(int value, bool isInt32)
    {
        if (!isInt32)
            return [checked((byte)value)];
        byte[] bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static IReadOnlyList<PlayerModifierOption> DamageOptions() =>
        Options(
            ("player_mod.opt_default", 0), ("0%", 1), ("25%", 2), ("50%", 3),
            ("75%", 4), ("90%", 5), ("100%", 6), ("110%", 7),
            ("125%", 8), ("150%", 9), ("200%", 10), ("300%", 11),
            ("player_mod.opt_fatality", 12));

    private static IReadOnlyList<PlayerModifierOption> Options(
        params (string Label, int Value)[] options) =>
        options.Select(option =>
            new PlayerModifierOption(option.Label, option.Value)).ToArray();

    private sealed record ModifierSnapshot(
        long Address,
        byte[] Original,
        byte[] Applied);
}
