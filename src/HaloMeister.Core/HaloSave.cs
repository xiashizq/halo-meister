namespace HaloMeister.Core;

/// <summary>
/// A loaded save: the envelope it arrived in, the parsed document, and convenience
/// accessors for the parts a player actually wants to change.
/// </summary>
public sealed class HaloSave
{
    public const string GameplayTagsProperty = "GameplayTags";
    public const string NotifiedTagsProperty = "NotifiedGameplayTags";
    public const string EntitlementsProperty = "OwnedPlayFabEntitlements";

    public SaveEnvelope Envelope { get; }
    public BlamDocument Document { get; }
    public byte[] OriginalPayload { get; }

    private HaloSave(SaveEnvelope envelope, BlamDocument document, byte[] originalPayload)
    {
        Envelope = envelope;
        Document = document;
        OriginalPayload = originalPayload;
    }

    public static HaloSave LoadFile(string path)
    {
        SaveEnvelope envelope = SaveEnvelope.LoadFile(path, out byte[] payload);
        return new HaloSave(envelope, BlamDocument.Parse(payload), payload);
    }

    public static HaloSave LoadBytes(byte[] bytes)
    {
        SaveEnvelope envelope = SaveEnvelope.Load(bytes, out byte[] payload);
        return new HaloSave(envelope, BlamDocument.Parse(payload), payload);
    }

    public static HaloSave LoadText(string text)
        => LoadBytes(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>Confirms the parser can rebuild the file it just read, byte for byte.</summary>
    public bool VerifyRoundTrip(out string detail)
    {
        byte[] rewritten = Document.Serialize();

        if (rewritten.Length != OriginalPayload.Length)
        {
            detail = $"length differs: {rewritten.Length} vs {OriginalPayload.Length}";
            return false;
        }

        for (int i = 0; i < rewritten.Length; i++)
        {
            if (rewritten[i] != OriginalPayload[i])
            {
                detail = $"first difference at payload offset 0x{i:X}";
                return false;
            }
        }

        detail = $"{rewritten.Length} bytes identical";
        return true;
    }

    public BlamProperty? TagsProperty => Document.Root.FirstOrDefault(p => p.Name == GameplayTagsProperty);
    public BlamProperty? NotifiedProperty => Document.Root.FirstOrDefault(p => p.Name == NotifiedTagsProperty);
    public BlamProperty? EntitlementsPropertyNode => Document.Root.FirstOrDefault(p => p.Name == EntitlementsProperty);

    public List<string> Tags => TagsProperty?.Tags ?? new List<string>();
    public List<string> NotifiedTags => NotifiedProperty?.Tags ?? new List<string>();
    public List<string> Entitlements => EntitlementsPropertyNode?.StringArray ?? new List<string>();

    public bool HasTag(string tag) => TagsProperty?.Tags?.Contains(tag) == true;

    /// <summary>
    /// Adds or removes a tag. New tags are appended so the game's own ordering is preserved.
    /// When <paramref name="syncNotified"/> is set the mirror list is kept in step, which
    /// stops the game from showing a burst of "new unlock" notifications.
    /// </summary>
    public bool SetTag(string tag, bool enabled, bool syncNotified = true)
    {
        bool changed = ApplyTag(TagsProperty, tag, enabled);
        if (syncNotified) changed |= ApplyTag(NotifiedProperty, tag, enabled);
        return changed;
    }

    private static bool ApplyTag(BlamProperty? property, string tag, bool enabled)
    {
        if (property?.Tags is not { } list) return false;

        int index = list.IndexOf(tag);
        if (enabled && index < 0)
        {
            list.Add(tag);
            return true;
        }

        if (!enabled && index >= 0)
        {
            list.RemoveAt(index);
            return true;
        }

        return false;
    }

    /// <summary>Every tag known to the editor, whether or not this save has it set.</summary>
    public IReadOnlyList<string> KnownTags()
    {
        var all = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string tag)
        {
            if (seen.Add(tag)) all.Add(tag);
        }

        foreach (string skull in Catalog.Skulls) Add(Catalog.SkullTag(skull));
        foreach (string terminal in Catalog.Terminals) Add(Catalog.TerminalTag(terminal));
        foreach (string insertion in Catalog.InsertionPoints) Add(Catalog.InsertionTag(insertion));
        foreach (string gate in Catalog.UnlockGates) Add(Catalog.UnlockTag(gate));

        foreach (Mission mission in Catalog.Missions)
        {
            foreach (string difficulty in Catalog.Difficulties)
                Add(Catalog.CompletionTag(difficulty, mission.Code));
        }

        // Anything the file already has that the catalog does not know about.
        foreach (string tag in Tags) Add(tag);
        foreach (string tag in NotifiedTags) Add(tag);

        return all;
    }

    /// <summary>Tags present in the save that the built-in catalog does not recognise.</summary>
    public IReadOnlyList<string> UnknownTags()
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (string skull in Catalog.Skulls) known.Add(Catalog.SkullTag(skull));
        foreach (string terminal in Catalog.Terminals) known.Add(Catalog.TerminalTag(terminal));
        foreach (string insertion in Catalog.InsertionPoints) known.Add(Catalog.InsertionTag(insertion));
        foreach (string gate in Catalog.UnlockGates) known.Add(Catalog.UnlockTag(gate));
        foreach (Mission mission in Catalog.Missions)
        {
            foreach (string difficulty in Catalog.Difficulties)
                known.Add(Catalog.CompletionTag(difficulty, mission.Code));
        }

        return Tags.Where(t => !known.Contains(t)).ToList();
    }

    public byte[] BuildFileBytes() => Envelope.Rebuild(Document.Serialize());

    public byte[] BuildContainerBytes() => SaveEnvelope.ToContainer(Document.Serialize());

    public string BuildBase64() => SaveEnvelope.ToBase64(Document.Serialize());

    /// <summary>Writes the save back, taking a timestamped backup of the target first.</summary>
    public void Save(string path, bool backup = true)
    {
        if (backup && File.Exists(path))
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string backupPath = $"{path}.{stamp}.bak";
            File.Copy(path, backupPath, overwrite: false);
        }

        File.WriteAllBytes(path, BuildFileBytes());
        Envelope.SourcePath = path;
    }
}
