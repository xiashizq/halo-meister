using System.Collections.ObjectModel;
using HaloMeister.Core;

namespace HaloMeister.App.Models;

/// <summary>
/// Single shared state object. Pages are created by the navigation frame with no
/// constructor arguments, so they read everything from here.
/// </summary>
public sealed class AppState : ObservableObject
{
    public static AppState Current { get; } = new();

    private HaloSave? _save;
    private bool _isDirty;
    private bool _syncNotifiedTags = true;

    public event Action? SaveLoaded;
    public event Action? DirtyChanged;

    public HaloSave? Save
    {
        get => _save;
        private set
        {
            _save = value;
            Raise();
            Raise(nameof(IsLoaded));
            Raise(nameof(SourceLabel));
        }
    }

    public bool IsLoaded => _save is not null;

    public string SourceLabel => _save is null
        ? "No save loaded"
        : $"{System.IO.Path.GetFileName(_save.Envelope.SourcePath) ?? "(pasted)"}  \u2022  {_save.Envelope.Description}  \u2022  " +
          $"{_save.Tags.Count} tags";

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (Set(ref _isDirty, value)) DirtyChanged?.Invoke();
        }
    }

    /// <summary>
    /// Keeps NotifiedGameplayTags in step with GameplayTags. Leaving this on avoids a
    /// wall of "new unlock" toasts the next time the campaign menu opens.
    /// </summary>
    public bool SyncNotifiedTags
    {
        get => _syncNotifiedTags;
        set => Set(ref _syncNotifiedTags, value);
    }

    public ObservableCollection<MissionRow> Missions { get; } = new();
    public ObservableCollection<TagToggle> Skulls { get; } = new();
    public ObservableCollection<TagToggle> Terminals { get; } = new();
    public ObservableCollection<TagToggle> InsertionPoints { get; } = new();
    public ObservableCollection<TagToggle> UnlockGates { get; } = new();
    public ObservableCollection<TagToggle> ExtraTags { get; } = new();
    public ObservableCollection<FlagRow> Flags { get; } = new();
    public ObservableCollection<NumberRow> Numbers { get; } = new();
    public ObservableCollection<string> Entitlements { get; } = new();
    public ObservableCollection<EntitlementRow> EntitlementRows { get; } = new();
    public ObservableCollection<RawRow> RawRows { get; } = new();

    private AppState()
    {
        BuildEntitlementRows(null, null);
    }

    public void MarkDirty()
    {
        bool wasAlreadyDirty = IsDirty;
        IsDirty = true;
        // IsDirty's setter raises DirtyChanged for the clean -> dirty transition.
        // Subsequent edits must raise it explicitly too: MainWindow uses this event to
        // refresh the immutable payload snapshot consumed by proxy/test patch handlers.
        if (wasAlreadyDirty) DirtyChanged?.Invoke();
        Raise(nameof(SourceLabel));
    }

    public void MarkClean() => IsDirty = false;

    public void Load(HaloSave save)
    {
        Save = save;
        Rebuild();
        IsDirty = false;
        SaveLoaded?.Invoke();
    }

    public void Unload()
    {
        Save = null;
        ClearAll();
        BuildEntitlementRows(null, null);
        IsDirty = false;
        SaveLoaded?.Invoke();
    }

    private void ClearAll()
    {
        Missions.Clear();
        Skulls.Clear();
        Terminals.Clear();
        InsertionPoints.Clear();
        UnlockGates.Clear();
        ExtraTags.Clear();
        Flags.Clear();
        Numbers.Clear();
        Entitlements.Clear();
        EntitlementRows.Clear();
        RawRows.Clear();
    }

    private void Rebuild()
    {
        ClearAll();
        if (_save is not { } save) return;

        void Changed() => MarkDirty();

        foreach (Mission mission in Catalog.Missions)
            Missions.Add(new MissionRow(save, mission, Changed));

        foreach (string skull in Catalog.Skulls.OrderBy(Catalog.Humanize, StringComparer.OrdinalIgnoreCase))
            Skulls.Add(new TagToggle(save, Catalog.SkullTag(skull), Catalog.Humanize(skull), Catalog.SkullTag(skull), Changed));

        foreach (string terminal in Catalog.Terminals)
            Terminals.Add(new TagToggle(
                save,
                Catalog.TerminalTag(terminal),
                TerminalDisplay(terminal),
                Catalog.TerminalTag(terminal),
                Changed));

        foreach (string insertion in Catalog.InsertionPoints)
            InsertionPoints.Add(new TagToggle(
                save,
                Catalog.InsertionTag(insertion),
                InsertionDisplay(insertion),
                Catalog.InsertionTag(insertion),
                Changed));

        foreach (string gate in Catalog.UnlockGates)
            UnlockGates.Add(new TagToggle(
                save,
                Catalog.UnlockTag(gate),
                GateDisplay(gate),
                Catalog.UnlockTag(gate),
                Changed));

        foreach (string tag in save.UnknownTags())
            ExtraTags.Add(new TagToggle(save, tag, tag, "not in the built-in catalog", Changed));

        foreach (string entitlement in save.Entitlements)
            Entitlements.Add(entitlement);

        // Keep the shipped catalogue visible even if OwnedPlayFabEntitlements is absent.
        IList<string>? ownedEntitlements = save.EntitlementsPropertyNode?.StringArray;
        Action? entitlementChanged = null;
        if (ownedEntitlements is not null)
        {
            entitlementChanged = () =>
            {
                Entitlements.Clear();
                foreach (string entitlement in ownedEntitlements)
                    Entitlements.Add(entitlement);
                Changed();
            };
        }

        BuildEntitlementRows(ownedEntitlements, entitlementChanged);

        foreach ((BlamProperty property, string path) in Walk(save.Document.Root, string.Empty))
        {
            if (property.IsBool) Flags.Add(new FlagRow(property, path, Changed));
            else if (property.IsInt) Numbers.Add(new NumberRow(property, path, Changed));
        }

        foreach (RawRow row in Flatten(save.Document.Root, 0, string.Empty))
            RawRows.Add(row);
    }

    private void BuildEntitlementRows(IList<string>? ownedEntitlements, Action? changed)
    {
        foreach (EntitlementDefinition definition in Catalog.Entitlements)
        {
            EntitlementRows.Add(new EntitlementRow(
                ownedEntitlements,
                definition.Id,
                definition.Display,
                definition.Category,
                true,
                changed));
        }

        if (ownedEntitlements is null) return;

        foreach (string entitlement in ownedEntitlements.Where(value =>
                     !Catalog.Entitlements.Any(known =>
                         known.Id.Equals(value, StringComparison.OrdinalIgnoreCase))))
        {
            EntitlementRows.Add(new EntitlementRow(
                ownedEntitlements,
                entitlement,
                Catalog.Humanize(entitlement),
                "Other",
                false,
                changed));
        }
    }

    private static string TerminalDisplay(string terminal)
    {
        string code = terminal.StartsWith("terminal_", StringComparison.OrdinalIgnoreCase)
            ? terminal["terminal_".Length..]
            : terminal;
        Mission? mission = Catalog.Missions.FirstOrDefault(candidate =>
            candidate.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        return mission is null ? Catalog.Humanize(terminal) : $"{mission.Title} terminal";
    }

    private static string InsertionDisplay(string insertion)
    {
        string value = insertion.StartsWith("ins_", StringComparison.OrdinalIgnoreCase)
            ? insertion["ins_".Length..]
            : insertion;
        int separator = value.IndexOf('_');
        string code = separator < 0 ? value : value[..separator];
        string checkpoint = separator < 0 ? string.Empty : value[(separator + 1)..];
        Mission? mission = Catalog.Missions.FirstOrDefault(candidate =>
            candidate.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        string missionName = mission?.Title ?? code.ToUpperInvariant();
        return checkpoint.Length == 0
            ? missionName
            : $"{missionName} · {Catalog.Humanize(checkpoint)}";
    }

    private static string GateDisplay(string gate)
    {
        string code = gate.StartsWith("unlock_", StringComparison.OrdinalIgnoreCase)
            ? gate["unlock_".Length..]
            : gate;
        Mission? mission = Catalog.Missions.FirstOrDefault(candidate =>
            candidate.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        return mission is null
            ? $"Unlock {Catalog.Humanize(code)}"
            : $"Unlock {mission.Title}";
    }

    /// <summary>Re-reads every toggle from the document after a bulk change.</summary>
    public void RefreshToggles()
    {
        foreach (MissionRow row in Missions) row.Refresh();
        foreach (TagToggle toggle in Skulls) toggle.Refresh();
        foreach (TagToggle toggle in Terminals) toggle.Refresh();
        foreach (TagToggle toggle in InsertionPoints) toggle.Refresh();
        foreach (TagToggle toggle in UnlockGates) toggle.Refresh();
        foreach (TagToggle toggle in ExtraTags) toggle.Refresh();
        Raise(nameof(SourceLabel));
    }

    public void AddEntitlement(string value)
    {
        if (BuildPolicy.IsRetail) return;
        if (_save?.EntitlementsPropertyNode?.StringArray is not { } list) return;

        string trimmed = value.Trim();
        if (trimmed.Length == 0 ||
            list.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) return;

        list.Add(trimmed);
        Entitlements.Add(trimmed);
        EntitlementRow? row = EntitlementRows.FirstOrDefault(item =>
            item.Id.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            EntitlementRows.Add(new EntitlementRow(
                list, trimmed, Catalog.Humanize(trimmed), "Other", false, MarkDirty));
        }
        else
        {
            row.Refresh();
        }
        MarkDirty();
    }

    public int AddEntitlements(IEnumerable<string> values)
    {
        if (BuildPolicy.IsRetail) return 0;
        if (_save?.EntitlementsPropertyNode?.StringArray is not { } list) return 0;

        int added = 0;
        foreach (string value in values)
        {
            string trimmed = value.Trim();
            if (trimmed.Length == 0 ||
                list.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) continue;

            list.Add(trimmed);
            Entitlements.Add(trimmed);
            EntitlementRow? row = EntitlementRows.FirstOrDefault(item =>
                item.Id.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                EntitlementRows.Add(new EntitlementRow(
                    list, trimmed, Catalog.Humanize(trimmed), "Other", false, MarkDirty));
            }
            else
            {
                row.Refresh();
            }
            added++;
        }

        if (added > 0) MarkDirty();
        return added;
    }

    public void RemoveEntitlement(string value)
    {
        if (BuildPolicy.IsRetail) return;
        if (_save?.EntitlementsPropertyNode?.StringArray is not { } list) return;

        if (list.Remove(value))
        {
            Entitlements.Remove(value);
            EntitlementRow? row = EntitlementRows.FirstOrDefault(item =>
                item.Id.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (row is { IsCatalogued: true }) row.Refresh();
            else if (row is not null) EntitlementRows.Remove(row);
            MarkDirty();
        }
    }

    public int SetAllCataloguedEntitlements(bool unlocked)
    {
        if (BuildPolicy.IsRetail) return 0;
        int changed = 0;
        foreach (EntitlementRow row in EntitlementRows.Where(item => item.IsCatalogued))
        {
            if (row.IsUnlocked == unlocked) continue;
            row.IsUnlocked = unlocked;
            changed++;
        }

        return changed;
    }

    /// <summary>Sets every catalogued tag at once.</summary>
    public int SetAllTags(bool enabled, Func<string, bool>? predicate = null)
    {
        if (_save is not { } save) return 0;

        int changed = 0;
        foreach (string tag in save.KnownTags())
        {
            if (predicate is not null && !predicate(tag)) continue;
            if (save.SetTag(tag, enabled, SyncNotifiedTags)) changed++;
        }

        if (changed > 0) MarkDirty();
        RefreshToggles();
        return changed;
    }

    private static IEnumerable<(BlamProperty Property, string Path)> Walk(
        IEnumerable<BlamProperty> properties, string prefix)
    {
        foreach (BlamProperty property in properties)
        {
            string path = prefix.Length == 0 ? property.DisplayName : $"{prefix}/{property.DisplayName}";
            yield return (property, path);

            if (property.Children is { } children)
            {
                foreach (var item in Walk(children, path)) yield return item;
            }
        }
    }

    private static IEnumerable<RawRow> Flatten(IEnumerable<BlamProperty> properties, int depth, string prefix)
    {
        foreach (BlamProperty property in properties)
        {
            string path = prefix.Length == 0 ? property.DisplayName : $"{prefix}/{property.DisplayName}";
            yield return new RawRow(property, depth, path);

            if (property.Children is { } children)
            {
                foreach (RawRow row in Flatten(children, depth + 1, path)) yield return row;
            }
        }
    }
}
