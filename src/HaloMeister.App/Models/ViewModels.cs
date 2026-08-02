using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using HaloMeister.Core;

namespace HaloMeister.App.Models;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>A single gameplay tag rendered as a checkbox or toggle.</summary>
public sealed class TagToggle : ObservableObject
{
    private readonly HaloSave _save;
    private readonly Action? _onChanged;

    public TagToggle(HaloSave save, string tag, string display, string? detail = null, Action? onChanged = null)
    {
        _save = save;
        _onChanged = onChanged;
        Tag = tag;
        Display = display;
        Detail = detail ?? tag;
    }

    public string Tag { get; }
    public string Display { get; }
    public string Detail { get; }
    public string? IconUri => Tag.StartsWith(Catalog.SkullPrefix, StringComparison.Ordinal)
        ? $"ms-appx:///Assets/SkullIcons/{LiveSkullsService.IconFile(ToRuntimeSkullName(Tag))}"
        : null;

    /// <summary>False when the loaded save has no tag container at all.</summary>
    public bool IsEditable => _save.TagsProperty?.Tags is not null;

    public bool IsSet
    {
        get => _save.HasTag(Tag);
        set
        {
            if (value == _save.HasTag(Tag)) return;
            _save.SetTag(Tag, value, AppState.Current.SyncNotifiedTags);
            Raise();
            _onChanged?.Invoke();
        }
    }

    /// <summary>Re-reads from the document, e.g. after a bulk operation.</summary>
    public void Refresh() => Raise(nameof(IsSet));

    private static string ToRuntimeSkullName(string tag)
    {
        string name = tag[Catalog.SkullPrefix.Length..];
        string snake = Regex.Replace(name, "([a-z0-9])([A-Z])", "$1_$2")
            .ToLowerInvariant();
        snake = snake switch
        {
            "anger" => "angry",
            "bandana" => "bandanna",
            _ => snake,
        };
        return "skull_" + snake;
    }
}

/// <summary>One mission row: a checkbox per difficulty, plus the unlock gate.</summary>
public sealed class MissionRow : ObservableObject
{
    private const int CampaignDifficultyCount = 5;
    private bool _normalizingDifficulties;
    private readonly Action? _onChanged;

    public MissionRow(HaloSave save, Mission mission, Action? onChanged)
    {
        Mission = mission;
        _onChanged = onChanged;

        var toggles = new List<TagToggle>(Catalog.Difficulties.Count);
        for (int index = 0; index < Catalog.Difficulties.Count; index++)
        {
            string difficulty = Catalog.Difficulties[index];
            int capturedIndex = index;
            toggles.Add(new TagToggle(
                save,
                Catalog.CompletionTag(difficulty, mission.Code),
                difficulty,
                null,
                () => OnToggleChanged(capturedIndex)));
        }

        Toggles = toggles;
    }

    public Mission Mission { get; }
    public string Code => Mission.Code;
    public string Title => Mission.Title;
    public IReadOnlyList<TagToggle> Toggles { get; }

    public TagToggle Easy => Toggles[0];
    public TagToggle Normal => Toggles[1];
    public TagToggle Heroic => Toggles[2];
    public TagToggle Legendary => Toggles[3];
    public TagToggle Laso => Toggles[4];
    public TagToggle Remix => Toggles[5];
    public TagToggle Deathless => Toggles[6];

    private void OnToggleChanged(int changedIndex)
    {
        if (_normalizingDifficulties) return;

        // Remix and Remix.Deathless are separate completion modes, not members of
        // the Easy -> LASO campaign difficulty progression.
        if (changedIndex >= CampaignDifficultyCount)
        {
            _onChanged?.Invoke();
            return;
        }

        _normalizingDifficulties = true;
        try
        {
            if (Toggles[changedIndex].IsSet)
            {
                // Completing a higher difficulty also completes every lower one.
                for (int index = 0; index < changedIndex; index++)
                    Toggles[index].IsSet = true;
            }
            else
            {
                // A higher completion cannot remain set when a required lower tier
                // is cleared.
                for (int index = changedIndex + 1; index < CampaignDifficultyCount; index++)
                    Toggles[index].IsSet = false;
            }
        }
        finally
        {
            _normalizingDifficulties = false;
        }

        _onChanged?.Invoke();
    }

    public void Refresh()
    {
        foreach (TagToggle toggle in Toggles) toggle.Refresh();
    }
}

/// <summary>A boolean flag property somewhere in the document.</summary>
public sealed class FlagRow : ObservableObject
{
    private readonly BlamProperty _property;
    private readonly Action? _onChanged;

    public FlagRow(BlamProperty property, string path, Action? onChanged)
    {
        _property = property;
        Path = path;
        _onChanged = onChanged;
    }

    public string Path { get; }
    public string Display => Catalog.Humanize(_property.Name.TrimStart('b'));

    public bool Value
    {
        get => _property.BoolValue;
        set
        {
            if (_property.BoolValue == value) return;
            _property.BoolValue = value;
            Raise();
            _onChanged?.Invoke();
        }
    }
}

/// <summary>An int32 property, editable in decimal or hex.</summary>
public sealed class NumberRow : ObservableObject
{
    private readonly BlamProperty _property;
    private readonly Action? _onChanged;
    private string? _error;

    public NumberRow(BlamProperty property, string path, Action? onChanged)
    {
        _property = property;
        Path = path;
        _onChanged = onChanged;
    }

    public string Path { get; }
    public string Display => _property.DisplayName;

    public string Text
    {
        get => _property.IntValue.ToString();
        set
        {
            string trimmed = (value ?? string.Empty).Trim();
            bool parsed;

            if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                // Parse as unsigned so 0x80000000 and above are accepted, then reinterpret.
                parsed = uint.TryParse(trimmed[2..], System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out uint hv);
                if (parsed) Assign(unchecked((int)hv));
            }
            else if (long.TryParse(trimmed, out long dv) && dv >= int.MinValue && dv <= uint.MaxValue)
            {
                parsed = true;
                Assign(dv > int.MaxValue ? unchecked((int)(uint)dv) : (int)dv);
            }
            else
            {
                parsed = false;
            }

            Error = parsed ? null : "Enter a whole number, or 0x-prefixed hex.";
            Raise(nameof(Hex));
            Raise(nameof(Binary));
        }
    }

    private void Assign(int value)
    {
        if (_property.IntValue == value) return;
        _property.IntValue = value;
        _onChanged?.Invoke();
    }

    public string Hex => $"0x{_property.IntValue:X8}";

    public string Binary => Convert.ToString((uint)_property.IntValue, 2).PadLeft(32, '0');

    public string? Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value)) Raise(nameof(HasError));
        }
    }

    public bool HasError => _error is not null;
}

/// <summary>A known or save-discovered PlayFab entitlement.</summary>
public sealed class EntitlementRow : ObservableObject
{
    private readonly IList<string>? _owned;
    private readonly Action? _onChanged;

    public EntitlementRow(
        IList<string>? owned,
        string id,
        string display,
        string category,
        bool isCatalogued,
        Action? onChanged)
    {
        _owned = owned;
        _onChanged = onChanged;
        Id = id;
        Display = display;
        Category = category;
        IsCatalogued = isCatalogued;
    }

    public string Id { get; }
    public string Display { get; }
    public string Category { get; }
    public bool IsCatalogued { get; }
    public bool IsEditable => _owned is not null && !BuildPolicy.IsRetail;
    public string SourceLabel => IsCatalogued ? "Shipped entitlement" : "Custom / future entitlement";
    public string SearchText => $"{Display} {Category} {Id}";

    public bool IsUnlocked
    {
        get => _owned?.Contains(Id, StringComparer.OrdinalIgnoreCase) == true;
        set
        {
            if (!IsEditable || _owned is null) return;
            bool current = IsUnlocked;
            if (current == value) return;

            if (value)
            {
                _owned.Add(Id);
            }
            else
            {
                string? existing = _owned.FirstOrDefault(value =>
                    value.Equals(Id, StringComparison.OrdinalIgnoreCase));
                if (existing is not null) _owned.Remove(existing);
            }

            Raise();
            Raise(nameof(Status));
            _onChanged?.Invoke();
        }
    }

    public string Status => _owned is null ? "Load to check" : IsUnlocked ? "Unlocked" : "Locked";

    public void Refresh()
    {
        Raise(nameof(IsUnlocked));
        Raise(nameof(Status));
    }
}

/// <summary>A flattened node of the raw property tree.</summary>
public sealed class RawRow
{
    public RawRow(BlamProperty property, int depth, string path)
    {
        Property = property;
        Depth = depth;
        Path = path;
        NameIndent = new Thickness(depth * 16, 0, 0, 0);
    }

    public BlamProperty Property { get; }
    public int Depth { get; }
    public string Path { get; }
    public Thickness NameIndent { get; }
    public Visibility GuideVisibility => Depth > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string Name => Property.DisplayName;
    public string TypeLabel => Property.StructTypeName is { } s ? $"{Property.TypeName}<{s}>" : Property.TypeName;
    public string TypeBadge
    {
        get
        {
            string type = Property.TypeName;
            return type.EndsWith("Property", StringComparison.Ordinal)
                ? type[..^"Property".Length]
                : type;
        }
    }
    public string? TypeDetail => Property.StructTypeName;
    public string FlagsLabel => $"0x{Property.Flags:X2}";
    public string Value => Property.ValuePreview;

    public string SearchText => $"{Path} {TypeLabel} {Value}";
}
