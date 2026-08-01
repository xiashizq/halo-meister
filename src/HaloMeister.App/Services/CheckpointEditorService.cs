using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using HaloMeister.Core;

namespace HaloMeister.App.Services;

/// <summary>
/// Offline checkpoint editing. Nothing here needs Campaign Evolved to be
/// running: the whole editable surface is read from the save itself. A live
/// capture only adds friendlier weapon names to the ammunition rows.
/// </summary>
public sealed class CheckpointEditSession
{
    private readonly byte[] _originalData;

    private CheckpointEditSession(
        string dataPath,
        byte[] originalData,
        CampaignSettingsEdit settings,
        IReadOnlyList<InventoryEntry> inventory,
        IReadOnlyList<AmmoRecordEdit> ammo,
        VitalityEdit? vitality)
    {
        DataPath = dataPath;
        _originalData = originalData;
        Settings = settings;
        Inventory = inventory;
        Ammo = ammo;
        Vitality = vitality;
        WeaponSlotEdit[] playerWeapons = inventory.OfType<WeaponSlotEdit>().Take(2).ToArray();
        PlayerWeapons = playerWeapons.Length == 2 ? playerWeapons : [];
        OriginalEquippedWeaponGameStateId =
            PlayerWeapons.Count == 2 ? PlayerWeapons[1].GameStateId : null;
        EquippedWeaponGameStateId = OriginalEquippedWeaponGameStateId;
    }

    public string DataPath { get; }
    public CampaignSettingsEdit Settings { get; }
    public IReadOnlyList<InventoryEntry> Inventory { get; }
    public IReadOnlyList<AmmoRecordEdit> Ammo { get; }
    public VitalityEdit? Vitality { get; }
    public IReadOnlyList<WeaponSlotEdit> PlayerWeapons { get; }
    public short? OriginalEquippedWeaponGameStateId { get; }
    public short? EquippedWeaponGameStateId { get; set; }
    public bool IsEquippedWeaponChanged
        => EquippedWeaponGameStateId != OriginalEquippedWeaponGameStateId;

    public bool HasChanges
        => Settings.IsChanged
        || Vitality?.IsChanged == true
        || Ammo.Any(record => record.IsChanged)
        || IsEquippedWeaponChanged;

    public static CheckpointEditSession Load(string dataPath, string oodlePath)
    {
        byte[] data = File.ReadAllBytes(dataPath);
        using var oodle = new OodleRuntime(oodlePath);
        HaloCevoCheckpoint checkpoint = HaloCevoCheckpoint.Decode(data, oodle);
        BlamSaveDocument document = BlamSaveDocument.Parse(checkpoint.Payload);

        var settings = CampaignSettingsEdit.From(document);

        var inventory = new List<InventoryEntry>();
        var namesByGsid = new Dictionary<int, string>();
        if (BlamActorTable.TryParse(document, out BlamActorTable? table) && table is not null)
        {
            foreach (BlamActorRecord record in table.Records)
            {
                if (record.GameStateId is { } id) namesByGsid[id] = record.DisplayName;
                inventory.Add(record.IsWeapon && record.GameStateId is not null
                    ? new WeaponSlotEdit(
                        record.Index,
                        record.DisplayName,
                        record.ClassPath ?? string.Empty,
                        record.GameStateId,
                        record.ClassName)
                    : new InventoryEntry(
                        record.Index,
                        record.DisplayName,
                        record.ClassPath ?? string.Empty,
                        record.GameStateId,
                        record.IsEquipment
                            ? "Item"
                            : record.IsWeapon
                                ? "Weapon (unmapped)"
                                : "Actor"));
            }
        }

        var ammo = checkpoint.EnumerateAmmoRecords()
            .Where(record => record.LooksLikeMagazine)
            .Select(record => new AmmoRecordEdit(record))
            .ToArray();
        VitalityEdit? vitality = checkpoint.FindPlayerVitality() is { } vitalityState
            ? new VitalityEdit(vitalityState)
            : null;

        // Each magazine names itself through the game-state identifier stored
        // just ahead of it, so no live capture is needed to tell them apart.
        foreach (AmmoRecordEdit record in ammo)
        {
            int alike = ammo.Count(other =>
                other.OriginalReserve == record.OriginalReserve &&
                other.OriginalLoaded == record.OriginalLoaded);
            record.IsUnique = alike == 1;
            if (record.GameStateId is { } id && namesByGsid.TryGetValue(id, out string? name))
                record.Label = $"{name}  (gsid {id})";
        }

        return new CheckpointEditSession(dataPath, data, settings, inventory, ammo, vitality);
    }

    /// <summary>Applies live weapon names onto matching ammunition records.</summary>
    public void ApplyLiveNames(IReadOnlyList<LiveWeaponAmmo> loadout)
    {
        foreach (LiveWeaponAmmo weapon in loadout)
        {
            AmmoRecordEdit[] matches = Ammo
                .Where(record =>
                    record.OriginalReserve == weapon.ReserveAmmo &&
                    record.OriginalLoaded == weapon.LoadedAmmo)
                .ToArray();
            if (matches.Length != 1) continue;
            matches[0].Label = $"{weapon.WeaponName} — {weapon.SlotName}";
            matches[0].ReserveMaximum = Math.Max(weapon.ReserveMaximum, weapon.ReserveAmmo);
            matches[0].LoadedMaximum = Math.Max(weapon.LoadedMaximum, weapon.LoadedAmmo);
            if (matches[0].GameStateId is { } id)
            {
                Inventory
                    .OfType<WeaponSlotEdit>()
                    .FirstOrDefault(slot => slot.GameStateId == id)
                    ?.MarkPlayerSlot(weapon.SlotName);
            }
        }
    }

    /// <summary>
    /// Rebuilds the checkpoint with every pending change.
    ///
    /// Ammunition is written first, directly into the freshly decoded payload,
    /// so the offsets captured at load time are still valid. Structural edits
    /// come afterwards: they resize the payload, and settings live in MetaData
    /// ahead of the native simulation while weapon classes live in the actor
    /// table behind it, so applying them first would shift ammunition offsets
    /// by different amounts depending on which changed.
    /// </summary>
    public byte[] BuildReplacement(string oodlePath)
    {
        using var oodle = new OodleRuntime(oodlePath);
        HaloCevoCheckpoint checkpoint = HaloCevoCheckpoint.Decode(_originalData, oodle);

        if (Vitality?.IsChanged == true)
        {
            HaloCevoVitalityState current = checkpoint.FindPlayerVitality()
                ?? throw new InvalidDataException(
                    "The guarded player-biped vitality record disappeared. Reload the checkpoint.");
            if (current.TagDatumOffset != Vitality.TagDatumOffset)
                throw new InvalidDataException(
                    "The player-biped vitality record moved. Reload the checkpoint; nothing was written.");
            checkpoint.SetPlayerVitality(
                current,
                Vitality.ValidatedBodyVitality(),
                Vitality.ValidatedShieldVitality());
        }

        var changedAmmo = Ammo.Where(record => record.IsChanged).ToArray();
        if (changedAmmo.Length > 0)
        {
            IReadOnlyList<HaloCevoAmmoState> current = checkpoint.EnumerateAmmoRecords();
            foreach (AmmoRecordEdit record in changedAmmo)
            {
                HaloCevoAmmoState target = current.FirstOrDefault(candidate =>
                    candidate.PayloadOffset == record.PayloadOffset &&
                    candidate.ReserveAmmo == record.OriginalReserve &&
                    candidate.LoadedAmmo == record.OriginalLoaded)
                    ?? throw new InvalidDataException(
                        $"The record at 0x{record.PayloadOffset:X} no longer holds " +
                        $"{record.OriginalReserve}/{record.OriginalLoaded}. Reload the checkpoint " +
                        "and try again; nothing was written.");

                checkpoint.SetAmmo(target, record.ValidatedReserve(), record.ValidatedLoaded());
            }
        }

        if (Settings.IsChanged || IsEquippedWeaponChanged)
        {
            BlamSaveDocument document = BlamSaveDocument.Parse(checkpoint.Payload);
            Settings.ApplyTo(document);

            BlamActorTable? table = null;
            if (IsEquippedWeaponChanged)
            {
                if (!BlamActorTable.TryParse(document, out table) || table is null)
                    throw new InvalidDataException("This checkpoint has no writable saved actor table.");
            }

            if (IsEquippedWeaponChanged &&
                OriginalEquippedWeaponGameStateId is { } original &&
                EquippedWeaponGameStateId is { } selected)
                table!.SwapRecordsByGameStateId(original, selected);

            if (IsEquippedWeaponChanged)
                table!.Apply();
            checkpoint.ReplacePayload(document.Serialize());
        }

        byte[] encoded = checkpoint.Encode(oodle);
        HaloCevoCheckpoint verified = HaloCevoCheckpoint.Decode(encoded, oodle);
        if (!checkpoint.Payload.AsSpan().SequenceEqual(verified.Payload))
            throw new InvalidDataException("The rebuilt checkpoint failed its final payload verification.");
        return encoded;
    }

    public string DescribeChanges()
    {
        var lines = new List<string>();
        lines.AddRange(Settings.DescribeChanges());
        if (Vitality?.IsChanged == true)
        {
            if (Vitality.ValidatedBodyPercent() != Vitality.OriginalBodyPercent)
                lines.Add(
                    $"Health: {Vitality.OriginalBodyPercent:0.#}% -> " +
                    $"{Vitality.ValidatedBodyPercent():0.#}%");
            if (Vitality.ValidatedShieldPercent() != Vitality.OriginalShieldPercent)
                lines.Add(
                    $"Shields: {Vitality.OriginalShieldPercent:0.#}% -> " +
                    $"{Vitality.ValidatedShieldPercent():0.#}%");
        }

        if (IsEquippedWeaponChanged)
        {
            string original = PlayerWeapons
                .FirstOrDefault(slot =>
                    slot.GameStateId == OriginalEquippedWeaponGameStateId)?.DisplayName
                ?? $"{OriginalEquippedWeaponGameStateId}";
            string selected = PlayerWeapons
                .FirstOrDefault(slot =>
                    slot.GameStateId == EquippedWeaponGameStateId)?.DisplayName
                ?? $"{EquippedWeaponGameStateId}";
            lines.Add($"Equipped weapon: {original} -> {selected}  [EXPERIMENTAL]");
        }

        foreach (AmmoRecordEdit record in Ammo.Where(item => item.IsChanged))
        {
            lines.Add(
                $"{record.Label}: {record.OriginalReserve}/{record.OriginalLoaded} -> " +
                $"{record.ValidatedReserve()}/{record.ValidatedLoaded()}");
        }
        return lines.Count == 0 ? "No changes." : string.Join("\n", lines);
    }
}

public sealed class VitalityEdit : EditModel
{
    private double _bodyPercent;
    private double _shieldPercent;

    public VitalityEdit(HaloCevoVitalityState state)
    {
        TagDatumOffset = state.TagDatumOffset;
        OriginalBodyPercent = state.BodyVitality * 100d;
        OriginalShieldPercent = state.ShieldVitality * 100d;
        _bodyPercent = OriginalBodyPercent;
        _shieldPercent = OriginalShieldPercent;
    }

    public int TagDatumOffset { get; }
    public double OriginalBodyPercent { get; }
    public double OriginalShieldPercent { get; }

    public double BodyPercent
    {
        get => _bodyPercent;
        set { if (Set(ref _bodyPercent, value)) OnPropertyChanged(nameof(IsChanged)); }
    }

    public double ShieldPercent
    {
        get => _shieldPercent;
        set { if (Set(ref _shieldPercent, value)) OnPropertyChanged(nameof(IsChanged)); }
    }

    public bool IsChanged
        => ValidatedBodyPercent() != OriginalBodyPercent ||
           ValidatedShieldPercent() != OriginalShieldPercent;

    public double ValidatedBodyPercent() => ValidatePercent(BodyPercent, "Health");
    public double ValidatedShieldPercent() => ValidatePercent(ShieldPercent, "Shields");
    public float ValidatedBodyVitality() => (float)(ValidatedBodyPercent() / 100d);
    public float ValidatedShieldVitality() => (float)(ValidatedShieldPercent() / 100d);

    private static double ValidatePercent(double value, string label)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value is < 0 or > 100)
            throw new InvalidOperationException($"{label} must be between 0 and 100 percent.");
        return value;
    }
}

public class InventoryEntry(
    int index,
    string displayName,
    string classPath,
    short? gameStateId,
    string kind) : EditModel
{
    public int Index { get; } = index;
    public string DisplayName { get; } = displayName;
    public string ClassPath { get; } = classPath;
    public short? GameStateId { get; } = gameStateId;
    public string Kind { get; } = kind;

    public string Detail => $"#{Index}  gsid {GameStateId?.ToString() ?? "-"}  {Kind}";
    public virtual bool IsSwappable => false;
}

/// <summary>
/// A weapon actor whose blueprint class can be repointed at a different
/// weapon. The native simulation record for the object keeps its original
/// weapon's layout, so a swap is not equivalent to the game spawning that
/// weapon itself and has to be treated as unverified until loaded in game.
/// </summary>
public sealed class WeaponSlotEdit : InventoryEntry
{
    private int _selectedIndex;
    private string? _playerSlotName;

    public WeaponSlotEdit(
        int index,
        string displayName,
        string classPath,
        short? gameStateId,
        string? className)
        : base(index, displayName, classPath, gameStateId, "Weapon")
    {
        var options = BlamWeaponCatalog.All.ToList();
        int originalIndex = options.FindIndex(weapon =>
            weapon.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase));
        if (originalIndex < 0)
        {
            string originalClass = string.IsNullOrWhiteSpace(className)
                ? Path.GetFileName(classPath)
                : className;
            options.Insert(
                0,
                new BlamWeapon($"{displayName} (current)", classPath, originalClass));
            originalIndex = 0;
        }

        Options = options;
        OptionNames = options.Select(weapon => weapon.DisplayName).ToArray();
        OriginalIndex = originalIndex;
        _selectedIndex = OriginalIndex;
    }

    public IReadOnlyList<BlamWeapon> Options { get; }
    public IReadOnlyList<string> OptionNames { get; }
    public string DisplayLabel => _playerSlotName is null
        ? DisplayName
        : $"{DisplayName} — {_playerSlotName}";

    public int OriginalIndex { get; }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set { if (Set(ref _selectedIndex, value)) OnPropertyChanged(nameof(IsChanged)); }
    }

    public BlamWeapon? SelectedWeapon
        => SelectedIndex >= 0 && SelectedIndex < Options.Count ? Options[SelectedIndex] : null;

    public bool IsChanged => SelectedIndex != OriginalIndex;
    public override bool IsSwappable => true;

    public void MarkPlayerSlot(string slotName)
    {
        if (Set(ref _playerSlotName, slotName))
            OnPropertyChanged(nameof(DisplayLabel));
    }
}

public sealed class AmmoRecordEdit : EditModel
{
    private double _reserve;
    private double _loaded;
    private string _label;

    public AmmoRecordEdit(HaloCevoAmmoState state)
    {
        PayloadOffset = state.PayloadOffset;
        GameStateId = state.GameStateId;
        OriginalReserve = state.ReserveAmmo;
        OriginalLoaded = state.LoadedAmmo;
        _reserve = state.ReserveAmmo;
        _loaded = state.LoadedAmmo;
        _label = $"Record 0x{state.PayloadOffset:X}";
        ReserveMaximum = Math.Max(9999, state.ReserveAmmo);
        LoadedMaximum = Math.Max(999, state.LoadedAmmo);
    }

    public int PayloadOffset { get; }
    public int? GameStateId { get; }
    public int OriginalReserve { get; }
    public int OriginalLoaded { get; }
    public int ReserveMaximum { get; set; }
    public int LoadedMaximum { get; set; }
    public bool IsUnique { get; set; }

    public string Label
    {
        get => _label;
        set => Set(ref _label, value);
    }

    public string CurrentDisplay
        => $"{OriginalLoaded} loaded · {OriginalReserve} reserve" + (IsUnique ? "" : "  (not unique)");

    public double ReserveAmmo
    {
        get => _reserve;
        set { if (Set(ref _reserve, value)) OnPropertyChanged(nameof(IsChanged)); }
    }

    public double LoadedAmmo
    {
        get => _loaded;
        set { if (Set(ref _loaded, value)) OnPropertyChanged(nameof(IsChanged)); }
    }

    public bool IsChanged
        => ValidatedReserve() != OriginalReserve || ValidatedLoaded() != OriginalLoaded;

    public int ValidatedReserve() => Validate(ReserveAmmo, ReserveMaximum, "Reserve ammo");

    public int ValidatedLoaded() => Validate(LoadedAmmo, LoadedMaximum, "Loaded ammo");

    private static int Validate(double value, int maximum, string label)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value != Math.Truncate(value))
            throw new InvalidOperationException($"{label} must be a whole number.");
        int result = checked((int)value);
        if (result < 0 || result > maximum)
            throw new InvalidOperationException($"{label} must be between 0 and {maximum}.");
        return result;
    }
}

public sealed class CampaignSettingsEdit : EditModel
{
    private static readonly string[] Difficulties = ["Easy", "Normal", "Heroic", "Legendary"];

    private int _difficultyIndex;
    private double _insertionPoint;
    private bool _laso;
    private bool _friendlyFire;

    private CampaignSettingsEdit() { }

    public IReadOnlyList<string> DifficultyOptions => Difficulties;

    public int OriginalDifficultyIndex { get; private init; }
    public int OriginalInsertionPoint { get; private init; }
    public bool OriginalLaso { get; private init; }
    public bool OriginalFriendlyFire { get; private init; }
    public int ScenarioIndex { get; private init; }
    public bool HasDifficulty { get; private init; }
    public bool HasInsertionPoint { get; private init; }
    public bool HasLaso { get; private init; }
    public bool HasFriendlyFire { get; private init; }

    public int DifficultyIndex
    {
        get => _difficultyIndex;
        set { if (Set(ref _difficultyIndex, value)) OnPropertyChanged(nameof(IsChanged)); }
    }

    public double InsertionPoint
    {
        get => _insertionPoint;
        set { if (Set(ref _insertionPoint, value)) OnPropertyChanged(nameof(IsChanged)); }
    }

    public bool IsLaso
    {
        get => _laso;
        set { if (Set(ref _laso, value)) OnPropertyChanged(nameof(IsChanged)); }
    }

    public bool IsFriendlyFire
    {
        get => _friendlyFire;
        set { if (Set(ref _friendlyFire, value)) OnPropertyChanged(nameof(IsChanged)); }
    }

    public bool IsChanged
        => (HasDifficulty && DifficultyIndex != OriginalDifficultyIndex)
        || (HasInsertionPoint && (int)InsertionPoint != OriginalInsertionPoint)
        || (HasLaso && IsLaso != OriginalLaso)
        || (HasFriendlyFire && IsFriendlyFire != OriginalFriendlyFire);

    public static CampaignSettingsEdit From(BlamSaveDocument document)
    {
        string? difficulty = document.Find("CampaignDifficultyLevel")?.AsString();
        int difficultyIndex = Array.FindIndex(
            Difficulties,
            name => difficulty?.EndsWith($"::{name}", StringComparison.Ordinal) == true);

        BlamPropertyNode? insertion = document.Find("InsertionPoint");
        BlamPropertyNode? laso = document.Find("bIsLASO");
        BlamPropertyNode? friendly = document.Find("bFriendlyFireEnabled");

        var settings = new CampaignSettingsEdit
        {
            HasDifficulty = difficultyIndex >= 0,
            OriginalDifficultyIndex = Math.Max(difficultyIndex, 0),
            HasInsertionPoint = insertion?.AsInt32() is not null,
            OriginalInsertionPoint = insertion?.AsInt32() ?? 0,
            HasLaso = laso?.AsBool() is not null,
            OriginalLaso = laso?.AsBool() ?? false,
            HasFriendlyFire = friendly?.AsBool() is not null,
            OriginalFriendlyFire = friendly?.AsBool() ?? false,
            ScenarioIndex = document.Find("CurrentScenarioIndex")?.AsInt32() ?? -1,
        };

        settings._difficultyIndex = settings.OriginalDifficultyIndex;
        settings._insertionPoint = settings.OriginalInsertionPoint;
        settings._laso = settings.OriginalLaso;
        settings._friendlyFire = settings.OriginalFriendlyFire;
        return settings;
    }

    public void ApplyTo(BlamSaveDocument document)
    {
        if (HasDifficulty && DifficultyIndex != OriginalDifficultyIndex)
        {
            document.Find("CampaignDifficultyLevel")!
                .SetString($"EBlamCampaignDifficultyLevel::{Difficulties[DifficultyIndex]}");
        }
        if (HasInsertionPoint && (int)InsertionPoint != OriginalInsertionPoint)
            document.Find("InsertionPoint")!.SetInt32((int)InsertionPoint);
        if (HasLaso && IsLaso != OriginalLaso)
            document.Find("bIsLASO")!.SetBool(IsLaso);
        if (HasFriendlyFire && IsFriendlyFire != OriginalFriendlyFire)
            document.Find("bFriendlyFireEnabled")!.SetBool(IsFriendlyFire);
    }

    public IEnumerable<string> DescribeChanges()
    {
        if (HasDifficulty && DifficultyIndex != OriginalDifficultyIndex)
            yield return $"Difficulty: {Difficulties[OriginalDifficultyIndex]} -> {Difficulties[DifficultyIndex]}";
        if (HasInsertionPoint && (int)InsertionPoint != OriginalInsertionPoint)
            yield return $"Insertion point: {OriginalInsertionPoint} -> {(int)InsertionPoint}";
        if (HasLaso && IsLaso != OriginalLaso)
            yield return $"LASO: {OriginalLaso} -> {IsLaso}";
        if (HasFriendlyFire && IsFriendlyFire != OriginalFriendlyFire)
            yield return $"Friendly fire: {OriginalFriendlyFire} -> {IsFriendlyFire}";
    }
}

public abstract class EditModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
