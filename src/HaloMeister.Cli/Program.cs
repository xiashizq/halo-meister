using HaloMeister.Core;
using System.Globalization;

if (args.Length == 0)
{
    Usage();
    return 1;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "dump" => Dump(Require(args, 1)),
        "verify" => Verify(Require(args, 1)),
        "tags" => Tags(Require(args, 1), args.Length > 2 ? args[2] : null),
        "base64" => Base64(Require(args, 1)),
        "extract" => Extract(Require(args, 1), Require(args, 2)),
        "set-tag" => SetTag(Require(args, 1), Require(args, 2), Require(args, 3), Require(args, 4)),
        "unlock-all" => UnlockAll(Require(args, 1), Require(args, 2)),
        "film-info" => FilmInfo(Require(args, 1)),
        "film-verify" => FilmVerify(Require(args, 1)),
        "films-scan" => FilmsScan(Require(args, 1), args.Length > 2 ? args[2] : null),
        "films-archive" => FilmsArchive(Require(args, 1), Require(args, 2)),
        "film-extract" => FilmExtract(Require(args, 1), Require(args, 2)),
        "gamesave-info" => GameSaveInfo(Require(args, 1)),
        "gamesaves-scan" => GameSavesScan(Require(args, 1)),
        "gamesave-codec-verify" => GameSaveCodecVerify(Require(args, 1), Require(args, 2)),
        "gamesave-payload" => GameSavePayload(Require(args, 1), Require(args, 2), Require(args, 3)),
        "gamesave-tree" => GameSaveTree(
            Require(args, 1),
            Require(args, 2),
            args.Length > 3 ? int.Parse(args[3]) : 3),
        "gamesave-actors" => GameSaveActors(
            Require(args, 1),
            Require(args, 2),
            args.Length > 3 ? args[3] : null),
        "gamesave-ammo-list" => GameSaveAmmoList(Require(args, 1), Require(args, 2)),
        "gamesave-weapon-map" => GameSaveWeaponMap(Require(args, 1), Require(args, 2)),
        "gamesave-vitality" => GameSaveVitality(Require(args, 1), Require(args, 2)),
        "gamesave-vitality-set" => GameSaveVitalitySet(
            Require(args, 1),
            Require(args, 2),
            Require(args, 3),
            float.Parse(Require(args, 4), CultureInfo.InvariantCulture),
            float.Parse(Require(args, 5), CultureInfo.InvariantCulture)),
        "gamesave-equip" => GameSaveEquip(
            Require(args, 1),
            Require(args, 2),
            Require(args, 3),
            short.Parse(Require(args, 4))),
        "weapons" => Weapons(),
        "gamesave-weapon-set" => GameSaveWeaponSet(
            Require(args, 1),
            Require(args, 2),
            Require(args, 3),
            int.Parse(Require(args, 4)),
            Require(args, 5)),
        "gamesave-ammo-at" => GameSaveAmmoAt(
            Require(args, 1),
            Require(args, 2),
            Require(args, 3),
            Convert.ToInt32(Require(args, 4), 16),
            int.Parse(Require(args, 5)),
            int.Parse(Require(args, 6))),
        "gamesave-settings" => GameSaveSettings(Require(args, 1), Require(args, 2)),
        "gamesave-set" => GameSaveSet(
            Require(args, 1),
            Require(args, 2),
            Require(args, 3),
            Require(args, 4),
            Require(args, 5)),
        "gamesave-diff" => GameSaveDiff(Require(args, 1), Require(args, 2), Require(args, 3)),
        "gamesave-ammo-find" => GameSaveAmmoFind(
            Require(args, 1),
            Require(args, 2),
            int.Parse(Require(args, 3)),
            int.Parse(Require(args, 4))),
        "gamesave-ammo-set" => GameSaveAmmoSet(
            Require(args, 1),
            Require(args, 2),
            Require(args, 3),
            int.Parse(Require(args, 4)),
            int.Parse(Require(args, 5)),
            int.Parse(Require(args, 6)),
            int.Parse(Require(args, 7))),
        _ => Unknown(args[0]),
    };
}
catch (BlamFormatException ex)
{
    Console.Error.WriteLine($"Save format error: {ex.Message}");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 2;
}

static string Require(string[] args, int index)
    => index < args.Length ? args[index] : throw new ArgumentException($"Missing argument #{index}.");

static int Unknown(string verb)
{
    Console.Error.WriteLine($"Unknown command '{verb}'.");
    Usage();
    return 1;
}

static void Usage()
{
    Console.WriteLine("""
        halomeister - Halo campaign progression save tool

          dump       <save>                       print the full property tree
          verify     <save>                       parse and confirm a byte-exact rewrite
          tags       <save> [filter]              list gameplay tags, optionally filtered
          base64     <save>                       print the re-encoded base64 container
          extract    <save> <out.bin>             write the decompressed payload to disk
          set-tag    <in> <out> <tag> <on|off>    toggle one tag
          unlock-all <in> <out>                   set every catalogued tag

          film-info    <film>                      show saved-film metadata and chunks
          film-verify  <film>                      validate a complete BLF saved film
          films-scan   <directory> [index.json]    scan finalized films and optionally index
          films-archive <source-dir> <archive-dir> copy valid films, deduplicated by SHA-256
          film-extract <film> <out.bin>            extract the opaque flmd replay payload

          gamesave-info <wgs-data-file>             inspect one WGS game-save Data stream
          gamesaves-scan <wgs-root>                 find and inspect WGS game-save streams
          gamesave-codec-verify <save> <oodle.dll>  decode/re-encode and require byte identity
          gamesave-payload <save> <oodle.dll> <out.bin>
                                                   write the decompressed native payload
          gamesave-tree <save> <oodle.dll> [depth]  print the structured property tree
          gamesave-actors <save> <oodle.dll> [filter]
                                                   list saved world actors and weapons
          gamesave-ammo-list <save> <oodle.dll>     list every native ammo record
          gamesave-weapon-map <save> <oodle.dll>    map weapon gsid, tag datum and record size
          gamesave-vitality <save> <oodle.dll>      show player health and shields
          gamesave-vitality-set <in> <out> <oodle.dll> <health%> <shields%>
                                                   set player health and shields
          gamesave-equip <in> <out> <oodle.dll> <gsid>
                                                   equip one of the two saved player weapons
          weapons                                   list the player weapon catalog
          gamesave-weapon-set <in> <out> <oodle.dll> <gsid> <weapon>
                                                   repoint one weapon actor (EXPERIMENTAL)
          gamesave-ammo-at <in> <out> <oodle.dll> <hex-offset> <reserve> <loaded>
                                                   patch one record by offset
          gamesave-settings <save> <oodle.dll>      show editable campaign settings
          gamesave-set <in> <out> <oodle.dll> <field> <value>
                                                   set difficulty|insertion|scenario|laso|friendlyfire
          gamesave-diff <a> <b> <oodle.dll>         compare two checkpoints
          gamesave-ammo-find <save> <oodle.dll> <reserve> <loaded>
                                                   locate guarded native ammo records
          gamesave-ammo-set <in> <out> <oodle.dll> <old-reserve> <old-loaded>
                            <new-reserve> <new-loaded>
                                                   patch one unique record and verify

        <save> may be a raw container, a base64 text file, or a PlayFab JSON response.
        """);
}

static int GameSaveVitality(string path, string oodlePath)
{
    using var oodle = new OodleRuntime(oodlePath);
    HaloCevoCheckpoint checkpoint = HaloCevoCheckpoint.Decode(File.ReadAllBytes(path), oodle);
    HaloCevoVitalityState state = checkpoint.FindPlayerVitality()
        ?? throw new InvalidDataException("No guarded player-biped vitality record was found.");
    Console.WriteLine(
        $"gsid={state.GameStateId} bipd=0x{state.BipedTagDatum:X8} " +
        $"native={state.NativeRecordSize} tagOffset=0x{state.TagDatumOffset:X} " +
        $"health={state.BodyVitality * 100:0.###}% shields={state.ShieldVitality * 100:0.###}%");
    return 0;
}

static int GameSaveVitalitySet(
    string input,
    string output,
    string oodlePath,
    float healthPercent,
    float shieldPercent)
{
    if (!float.IsFinite(healthPercent) || healthPercent is < 0 or > 100 ||
        !float.IsFinite(shieldPercent) || shieldPercent is < 0 or > 100)
        throw new ArgumentOutOfRangeException(
            nameof(healthPercent), "Health and shields must be between 0 and 100 percent.");

    using var oodle = new OodleRuntime(oodlePath);
    HaloCevoCheckpoint checkpoint = HaloCevoCheckpoint.Decode(File.ReadAllBytes(input), oodle);
    HaloCevoVitalityState state = checkpoint.FindPlayerVitality()
        ?? throw new InvalidDataException("No guarded player-biped vitality record was found.");
    checkpoint.SetPlayerVitality(state, healthPercent / 100f, shieldPercent / 100f);
    byte[] encoded = checkpoint.Encode(oodle);
    HaloCevoCheckpoint verified = HaloCevoCheckpoint.Decode(encoded, oodle);
    HaloCevoVitalityState verifyState = verified.FindPlayerVitality()
        ?? throw new InvalidDataException("The rebuilt checkpoint lost its player vitality record.");
    if (BitConverter.SingleToInt32Bits(verifyState.BodyVitality) !=
            BitConverter.SingleToInt32Bits(healthPercent / 100f) ||
        BitConverter.SingleToInt32Bits(verifyState.ShieldVitality) !=
            BitConverter.SingleToInt32Bits(shieldPercent / 100f))
        throw new InvalidDataException("The rebuilt checkpoint failed vitality verification.");

    File.WriteAllBytes(output, encoded);
    Console.WriteLine(
        $"Wrote health={healthPercent:0.###}% shields={shieldPercent:0.###}% to {output}.");
    return 0;
}

static int GameSaveCodecVerify(string path, string oodlePath)
{
    byte[] original = File.ReadAllBytes(path);
    using var oodle = new OodleRuntime(oodlePath);
    HaloCevoCheckpoint checkpoint = HaloCevoCheckpoint.Decode(original, oodle);
    byte[] rebuilt = checkpoint.Encode(oodle);
    bool identical = original.AsSpan().SequenceEqual(rebuilt);
    Console.WriteLine(
        identical
            ? $"OK: {checkpoint.ChunkCount} Kraken chunks round-tripped byte-for-byte."
            : $"FAIL: rebuilt wrapper differs ({original.Length:N0} -> {rebuilt.Length:N0} bytes).");
    return identical ? 0 : 2;
}

static int GameSavePayload(string path, string oodlePath, string output)
{
    using var oodle = new OodleRuntime(oodlePath);
    HaloCevoCheckpoint checkpoint = HaloCevoCheckpoint.Decode(File.ReadAllBytes(path), oodle);
    File.WriteAllBytes(output, checkpoint.Payload);
    Console.WriteLine(
        $"Wrote {checkpoint.Payload.Length:N0} payload byte(s) from " +
        $"{checkpoint.ChunkCount} chunk(s) to {output}.");
    return 0;
}

static int GameSaveTree(string path, string oodlePath, int maxDepth)
{
    using var oodle = new OodleRuntime(oodlePath);
    HaloCevoCheckpoint checkpoint = HaloCevoCheckpoint.Decode(File.ReadAllBytes(path), oodle);
    BlamSaveDocument document = BlamSaveDocument.Parse(checkpoint.Payload);

    byte[] rebuilt = document.Serialize();
    bool identical = checkpoint.Payload.AsSpan().SequenceEqual(rebuilt);
    Console.WriteLine(identical
        ? $"Property tree round-trips byte-for-byte ({rebuilt.Length:N0} bytes)."
        : $"WARNING: rebuilt payload differs ({checkpoint.Payload.Length:N0} -> {rebuilt.Length:N0} bytes).");
    Console.WriteLine();

    foreach (BlamPropertyNode node in document.Root) Print(node, 0);
    return identical ? 0 : 3;

    void Print(BlamPropertyNode node, int depth)
    {
        string pad = new(' ', depth * 2);
        string detail = Describe(node);
        Console.WriteLine(
            $"{pad}0x{node.ValueOffset:X7} {node.Name} : {node.Type}  " +
            $"size={node.ValueSize:N0} flags=0x{node.Flags:X2}{detail}");

        if (depth >= maxDepth) return;
        foreach (BlamPropertyNode child in node.Children ?? []) Print(child, depth + 1);
    }

    static string Describe(BlamPropertyNode node)
    {
        if (node.ObjectClassPath is { } cls) return $"  = {cls}";
        if (node.Type.Name == "BoolProperty") return $"  = {node.AsBool()}";
        if (node.AsInt32() is { } number) return $"  = {number}";
        if (node.AsInt16() is { } small) return $"  = {small}";
        if (node.AsString() is { Length: > 0 } text) return $"  = {text}";
        return string.Empty;
    }
}

static int GameSaveActors(string path, string oodlePath, string? filter)
{
    using var oodle = new OodleRuntime(oodlePath);
    HaloCevoCheckpoint checkpoint = HaloCevoCheckpoint.Decode(File.ReadAllBytes(path), oodle);
    BlamSaveDocument document = BlamSaveDocument.Parse(checkpoint.Payload);
    if (!BlamActorTable.TryParse(document, out BlamActorTable? table) || table is null)
    {
        Console.Error.WriteLine("This checkpoint has no readable saved actor table.");
        return 2;
    }

    // Re-serialising here proves the table can be written back unchanged,
    // which is the precondition for editing anything in it.
    table.Apply();
    byte[] rebuilt = document.Serialize();
    bool identical = checkpoint.Payload.AsSpan().SequenceEqual(rebuilt);

    IEnumerable<BlamActorRecord> records = table.Records;
    if (!string.IsNullOrWhiteSpace(filter))
        records = records.Where(record =>
            record.ClassName?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true ||
            record.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase));

    var listed = records.ToArray();
    foreach (BlamActorRecord record in listed)
    {
        string kind = record.IsWeapon ? "weapon" : record.IsEquipment ? "item" : "actor";
        Console.WriteLine(
            $"#{record.Index,-4} 0x{record.Offset:X7}  gsid={record.GameStateId,-5} {kind,-6} " +
            $"{record.DisplayName}");
        Console.WriteLine($"       {record.ClassPath}");
    }

    Console.WriteLine();
    Console.WriteLine(
        $"{listed.Length} of {table.Records.Count} actor(s); " +
        $"{table.Records.Count(r => r.IsWeapon)} weapon(s), " +
        $"{table.Records.Count(r => r.IsEquipment)} item(s).");
    Console.WriteLine(identical
        ? "Actor table re-serialises byte-for-byte."
        : "WARNING: actor table did not re-serialise cleanly; editing is unsafe.");
    return identical ? 0 : 3;
}

static int Weapons()
{
    foreach (BlamWeapon weapon in BlamWeaponCatalog.All)
        Console.WriteLine($"{weapon.DisplayName,-20} {weapon.AssetPath}");
    Console.WriteLine();
    Console.WriteLine($"{BlamWeaponCatalog.All.Count} player weapon blueprint(s).");
    return 0;
}

static int GameSaveWeaponSet(string input, string output, string oodlePath, int gsid, string weaponName)
{
    BlamWeapon weapon = BlamWeaponCatalog.Find(weaponName)
        ?? throw new ArgumentException(
            $"Unknown weapon '{weaponName}'. Run 'weapons' for the catalog.");

    using var oodle = new OodleRuntime(oodlePath);
    HaloCevoCheckpoint checkpoint = HaloCevoCheckpoint.Decode(File.ReadAllBytes(input), oodle);
    BlamSaveDocument document = BlamSaveDocument.Parse(checkpoint.Payload);
    if (!BlamActorTable.TryParse(document, out BlamActorTable? table) || table is null)
        throw new InvalidDataException("This checkpoint has no readable saved actor table.");

    BlamActorRecord record = table.Records.FirstOrDefault(item => item.GameStateId == gsid)
        ?? throw new InvalidDataException($"No saved actor has game-state id {gsid}.");
    if (!record.IsWeapon)
        throw new InvalidDataException($"Actor {gsid} is {record.ClassName}, which is not a weapon.");

    string before = record.ClassName ?? "?";
    record.SetClass(weapon.AssetPath, weapon.ClassName);
    table.Apply();
    checkpoint.ReplacePayload(document.Serialize());

    byte[] rebuilt = WriteVerified(checkpoint, oodle, output);
    Console.WriteLine($"Wrote {rebuilt.Length:N0} bytes. gsid {gsid}: {before} -> {weapon.ClassName}");
    Console.WriteLine(
        "EXPERIMENTAL: the native simulation record for this object keeps its original " +
        "weapon's layout and size. Load it in game before trusting the result.");
    return 0;
}

static int GameSaveAmmoList(string path, string oodlePath)
{
    using var oodle = new OodleRuntime(oodlePath);
    HaloCevoCheckpoint checkpoint = HaloCevoCheckpoint.Decode(File.ReadAllBytes(path), oodle);
    IReadOnlyList<HaloCevoAmmoState> records = checkpoint.EnumerateAmmoRecords();

    // Name each record through its owning actor, so the listing is readable
    // without a live capture.
    var names = new Dictionary<int, string>();
    BlamSaveDocument document = BlamSaveDocument.Parse(checkpoint.Payload);
    if (BlamActorTable.TryParse(document, out BlamActorTable? table) && table is not null)
    {
        foreach (BlamActorRecord actor in table.Records)
            if (actor.GameStateId is { } id) names[id] = actor.DisplayName;
    }

    var interesting = records.Where(record => record.LooksLikeMagazine).ToArray();
    foreach (HaloCevoAmmoState record in interesting)
    {
        int duplicates = interesting.Count(other =>
            other.ReserveAmmo == record.ReserveAmmo && other.LoadedAmmo == record.LoadedAmmo);
        string unique = duplicates == 1 ? "unique" : $"{duplicates} alike";
        string owner = record.GameStateId is { } id && names.TryGetValue(id, out string? name)
            ? $"{name} (gsid {id})"
            : record.GameStateId is { } raw ? $"gsid {raw}" : "unresolved";
        Console.WriteLine(
            $"0x{record.PayloadOffset:X7}  {owner,-28} reserve={record.ReserveAmmo,-6} " +
            $"loaded={record.LoadedAmmo,-5} tag={FormatDatum(record.WeaponTagDatum),10} " +
            $"native={record.NativeRecordSize?.ToString() ?? "?",-5} ({unique})");
    }

    Console.WriteLine();
    Console.WriteLine(
        $"{interesting.Length} loaded record(s) of {records.Count} framed; " +
        "edit any with gamesave-ammo-at using its offset.");
    return interesting.Length == 0 ? 2 : 0;

    static string FormatDatum(uint? datum)
        => datum is { } value ? $"0x{value:X8}" : "?";
}

static int GameSaveWeaponMap(string path, string oodlePath)
{
    using var oodle = new OodleRuntime(oodlePath);
    HaloCevoCheckpoint checkpoint = HaloCevoCheckpoint.Decode(File.ReadAllBytes(path), oodle);
    BlamSaveDocument document = BlamSaveDocument.Parse(checkpoint.Payload);
    if (!BlamActorTable.TryParse(document, out BlamActorTable? table) || table is null)
        throw new InvalidDataException("This checkpoint has no readable saved actor table.");

    var actors = table.Records
        .Where(actor => actor.GameStateId is not null)
        .ToDictionary(actor => (int)actor.GameStateId!.Value);
    HaloCevoAmmoState[] weapons = checkpoint.EnumerateAmmoRecords()
        .Where(record =>
            record.LooksLikeMagazine &&
            record.GameStateId is not null &&
            record.WeaponTagDatum is not null)
        .ToArray();

    foreach (HaloCevoAmmoState weapon in weapons)
    {
        actors.TryGetValue(weapon.GameStateId!.Value, out BlamActorRecord? actor);
        Console.WriteLine(
            $"gsid={weapon.GameStateId,-5} ammo=0x{weapon.PayloadOffset:X7} " +
            $"tag=0x{weapon.WeaponTagDatum:X8} native={weapon.NativeRecordSize?.ToString() ?? "?",-5} " +
            $"{actor?.DisplayName ?? "(unresolved)"}");
    }
    Console.WriteLine();
    Console.WriteLine(
        $"Mapped {weapons.Length} native weapon definition datum(s); " +
        "the datum is stored at ammo offset - 698.");
    return weapons.Length == 0 ? 2 : 0;
}

static int GameSaveEquip(
    string input,
    string output,
    string oodlePath,
    short gameStateId)
{
    using var oodle = new OodleRuntime(oodlePath);
    HaloCevoCheckpoint checkpoint = HaloCevoCheckpoint.Decode(File.ReadAllBytes(input), oodle);
    BlamSaveDocument document = BlamSaveDocument.Parse(checkpoint.Payload);
    if (!BlamActorTable.TryParse(document, out BlamActorTable? table) || table is null)
        throw new InvalidDataException("This checkpoint has no readable saved actor table.");

    BlamActorRecord[] playerWeapons = table.Records
        .Where(record => record.IsWeapon && record.GameStateId is not null)
        .Take(2)
        .ToArray();
    if (playerWeapons.Length != 2)
        throw new InvalidDataException(
            "The checkpoint does not expose exactly two leading saved weapon actors.");

    BlamActorRecord target = playerWeapons
        .FirstOrDefault(record => record.GameStateId == gameStateId)
        ?? throw new InvalidDataException(
            $"Actor {gameStateId} is not one of the two leading player weapon records " +
            $"({playerWeapons[0].GameStateId}, {playerWeapons[1].GameStateId}).");
    BlamActorRecord equipped = playerWeapons[1];
    if (target.GameStateId != equipped.GameStateId)
        table.SwapRecordsByGameStateId(target.GameStateId!.Value, equipped.GameStateId!.Value);

    table.Apply();
    checkpoint.ReplacePayload(document.Serialize());
    byte[] rebuilt = WriteVerified(checkpoint, oodle, output);
    Console.WriteLine(
        $"Wrote {rebuilt.Length:N0} bytes. Equipped {target.DisplayName} " +
        $"(gsid {target.GameStateId}); player weapon actor order is now " +
        $"{playerWeapons.First(record => record.GameStateId != target.GameStateId).GameStateId}, " +
        $"{target.GameStateId}.");
    Console.WriteLine(
        "EXPERIMENTAL: actor order is confirmed to track equipped/backpack changes in " +
        "controlled A30 saves, but this output still requires an in-game resume test.");
    return 0;
}

static int GameSaveAmmoAt(
    string input,
    string output,
    string oodlePath,
    int offset,
    int reserve,
    int loaded)
{
    using var oodle = new OodleRuntime(oodlePath);
    HaloCevoCheckpoint checkpoint = HaloCevoCheckpoint.Decode(File.ReadAllBytes(input), oodle);
    HaloCevoAmmoState record = checkpoint.EnumerateAmmoRecords()
        .FirstOrDefault(candidate => candidate.PayloadOffset == offset)
        ?? throw new InvalidDataException(
            $"0x{offset:X} is not a guarded native ammo record. Run gamesave-ammo-list first.");

    checkpoint.SetAmmo(record, reserve, loaded);
    byte[] rebuilt = WriteVerified(checkpoint, oodle, output);
    Console.WriteLine(
        $"Wrote {rebuilt.Length:N0} bytes. 0x{offset:X}: " +
        $"{record.ReserveAmmo}/{record.LoadedAmmo} -> {reserve}/{loaded}");
    return 0;
}

static int GameSaveSettings(string path, string oodlePath)
{
    using var oodle = new OodleRuntime(oodlePath);
    HaloCevoCheckpoint checkpoint = HaloCevoCheckpoint.Decode(File.ReadAllBytes(path), oodle);
    BlamSaveDocument document = BlamSaveDocument.Parse(checkpoint.Payload);

    foreach (string name in (string[])[
        "CurrentScenarioIndex", "CampaignDifficultyLevel", "InsertionPoint",
        "bFriendlyFireEnabled", "bIsLASO", "SavedFilmName", "CurrentCampaignDataAssetPtr"])
    {
        BlamPropertyNode? node = document.Find(name);
        if (node is null)
        {
            Console.WriteLine($"{name,-28} (not present)");
            continue;
        }

        object? value = node.Type.Name switch
        {
            "BoolProperty" => node.AsBool(),
            "IntProperty" => node.AsInt32(),
            "SoftObjectProperty" => node.AsSoftObject()?.Path,
            _ => node.AsString(),
        };
        Console.WriteLine($"{name,-28} {value}");
    }

    if (BlamActorTable.TryParse(document, out BlamActorTable? table) && table is not null)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"{table.Records.Count} saved actor(s): " +
            $"{table.Records.Count(r => r.IsWeapon)} weapon(s), " +
            $"{table.Records.Count(r => r.IsEquipment)} item(s).");
    }
    return 0;
}

static int GameSaveSet(string input, string output, string oodlePath, string field, string value)
{
    using var oodle = new OodleRuntime(oodlePath);
    HaloCevoCheckpoint checkpoint = HaloCevoCheckpoint.Decode(File.ReadAllBytes(input), oodle);
    BlamSaveDocument document = BlamSaveDocument.Parse(checkpoint.Payload);

    string before;
    switch (field.ToLowerInvariant())
    {
        case "difficulty":
        {
            BlamPropertyNode node = Required(document, "CampaignDifficultyLevel");
            before = node.AsString() ?? "?";
            string[] allowed = ["Easy", "Normal", "Heroic", "Legendary"];
            string? match = allowed.FirstOrDefault(
                name => name.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                throw new ArgumentException($"Difficulty must be one of {string.Join(", ", allowed)}.");
            node.SetString($"EBlamCampaignDifficultyLevel::{match}");
            break;
        }
        case "insertion":
        {
            BlamPropertyNode node = Required(document, "InsertionPoint");
            before = $"{node.AsInt32()}";
            node.SetInt32(int.Parse(value));
            break;
        }
        case "scenario":
        {
            BlamPropertyNode node = Required(document, "CurrentScenarioIndex");
            before = $"{node.AsInt32()}";
            node.SetInt32(int.Parse(value));
            break;
        }
        case "laso":
        case "friendlyfire":
        {
            BlamPropertyNode node = Required(
                document, field.Equals("laso", StringComparison.OrdinalIgnoreCase)
                    ? "bIsLASO"
                    : "bFriendlyFireEnabled");
            before = $"{node.AsBool()}";
            node.SetBool(value is "on" or "true" or "1" or "yes");
            break;
        }
        default:
            throw new ArgumentException(
                $"Unknown field '{field}'. Use difficulty, insertion, scenario, laso or friendlyfire.");
    }

    checkpoint.ReplacePayload(document.Serialize());
    byte[] rebuilt = WriteVerified(checkpoint, oodle, output);
    Console.WriteLine($"Wrote {rebuilt.Length:N0} bytes. {field}: {before} -> {value}");
    return 0;

    static BlamPropertyNode Required(BlamSaveDocument document, string name)
        => document.Find(name) ?? throw new InvalidDataException($"'{name}' is not in this checkpoint.");
}

static int GameSaveDiff(string leftPath, string rightPath, string oodlePath)
{
    using var oodle = new OodleRuntime(oodlePath);
    HaloCevoCheckpoint left = HaloCevoCheckpoint.Decode(File.ReadAllBytes(leftPath), oodle);
    HaloCevoCheckpoint right = HaloCevoCheckpoint.Decode(File.ReadAllBytes(rightPath), oodle);

    Console.WriteLine($"left  {Path.GetFileName(leftPath)}  {left.Payload.Length:N0} payload bytes");
    Console.WriteLine($"right {Path.GetFileName(rightPath)}  {right.Payload.Length:N0} payload bytes");
    Console.WriteLine();

    BlamSaveDocument leftDocument = BlamSaveDocument.Parse(left.Payload);
    BlamSaveDocument rightDocument = BlamSaveDocument.Parse(right.Payload);

    foreach (string name in (string[])[
        "CurrentScenarioIndex", "CampaignDifficultyLevel", "InsertionPoint", "bIsLASO"])
    {
        string? a = Describe(leftDocument.Find(name));
        string? b = Describe(rightDocument.Find(name));
        if (a != b) Console.WriteLine($"  {name,-26} {a} -> {b}");
    }

    if (BlamActorTable.TryParse(leftDocument, out BlamActorTable? leftTable) &&
        BlamActorTable.TryParse(rightDocument, out BlamActorTable? rightTable) &&
        leftTable is not null && rightTable is not null)
    {
        Console.WriteLine();
        Console.WriteLine($"actors {leftTable.Records.Count} -> {rightTable.Records.Count}");

        var leftByGsid = leftTable.Records
            .Where(record => record.GameStateId is not null)
            .ToLookup(record => record.GameStateId!.Value);
        var rightByGsid = rightTable.Records
            .Where(record => record.GameStateId is not null)
            .ToLookup(record => record.GameStateId!.Value);

        foreach (short gsid in leftByGsid.Select(g => g.Key).Union(rightByGsid.Select(g => g.Key)).Order())
        {
            BlamActorRecord? a = leftByGsid[gsid].FirstOrDefault();
            BlamActorRecord? b = rightByGsid[gsid].FirstOrDefault();
            if (a?.ClassName != b?.ClassName)
            {
                Console.WriteLine(
                    $"  gsid {gsid,-5} class {a?.ClassName ?? "(absent)"} -> {b?.ClassName ?? "(absent)"}");
            }
            else if (a is not null && b is not null && a.Index != b.Index)
            {
                // Position in this array tracks inventory order, so a move is
                // how an equipped-weapon change shows up.
                Console.WriteLine(
                    $"  gsid {gsid,-5} moved #{a.Index} -> #{b.Index}  ({a.DisplayName})");
            }
        }
    }

    if (left.Payload.Length == right.Payload.Length)
    {
        int differing = 0;
        for (int index = 0; index < left.Payload.Length; index++)
            if (left.Payload[index] != right.Payload[index]) differing++;
        Console.WriteLine();
        Console.WriteLine($"{differing:N0} payload byte(s) differ.");
    }
    return 0;

    static string? Describe(BlamPropertyNode? node)
        => node is null ? null
            : node.Type.Name == "BoolProperty" ? $"{node.AsBool()}"
            : node.AsInt32() is { } number ? $"{number}"
            : node.AsString();
}

/// <summary>Re-encodes, re-decodes and requires the payload to survive intact.</summary>
static byte[] WriteVerified(HaloCevoCheckpoint checkpoint, OodleRuntime oodle, string output)
{
    byte[] rebuilt = checkpoint.Encode(oodle);
    HaloCevoCheckpoint verified = HaloCevoCheckpoint.Decode(rebuilt, oodle);
    if (!checkpoint.Payload.AsSpan().SequenceEqual(verified.Payload))
        throw new InvalidDataException("The rebuilt checkpoint failed payload verification.");
    File.WriteAllBytes(output, rebuilt);
    return rebuilt;
}

static int GameSaveAmmoFind(string path, string oodlePath, int reserve, int loaded)
{
    using var oodle = new OodleRuntime(oodlePath);
    HaloCevoCheckpoint checkpoint = HaloCevoCheckpoint.Decode(File.ReadAllBytes(path), oodle);
    IReadOnlyList<HaloCevoAmmoState> matches = checkpoint.FindAmmoStates(reserve, loaded);
    foreach (HaloCevoAmmoState match in matches)
        Console.WriteLine($"0x{match.PayloadOffset:X}: reserve={match.ReserveAmmo}, loaded={match.LoadedAmmo}");
    Console.WriteLine($"{matches.Count} guarded native ammo record(s).");
    return matches.Count == 1 ? 0 : 2;
}

static int GameSaveAmmoSet(
    string input,
    string output,
    string oodlePath,
    int oldReserve,
    int oldLoaded,
    int newReserve,
    int newLoaded)
{
    using var oodle = new OodleRuntime(oodlePath);
    HaloCevoCheckpoint checkpoint = HaloCevoCheckpoint.Decode(File.ReadAllBytes(input), oodle);
    IReadOnlyList<HaloCevoAmmoState> matches = checkpoint.FindAmmoStates(oldReserve, oldLoaded);
    if (matches.Count != 1)
        throw new InvalidDataException(
            $"Expected one guarded native ammo record, but found {matches.Count}.");

    checkpoint.SetAmmo(matches[0], newReserve, newLoaded);
    byte[] rebuilt = checkpoint.Encode(oodle);
    HaloCevoCheckpoint verified = HaloCevoCheckpoint.Decode(rebuilt, oodle);
    if (!checkpoint.Payload.AsSpan().SequenceEqual(verified.Payload))
        throw new InvalidDataException("The rebuilt checkpoint failed payload verification.");

    File.WriteAllBytes(output, rebuilt);
    Console.WriteLine(
        $"Wrote {rebuilt.Length:N0} bytes. 0x{matches[0].PayloadOffset:X}: " +
        $"{oldReserve}/{oldLoaded} -> {newReserve}/{newLoaded}");
    return 0;
}

static int GameSaveInfo(string path)
{
    WgsGameSaveInfo info = WgsGameSave.Inspect(path);
    PrintGameSave(info);
    return info.Kind == WgsGameSaveKind.Unknown ? 2 : 0;
}

static int GameSavesScan(string root)
{
    if (!Directory.Exists(root))
        throw new DirectoryNotFoundException(root);

    int found = 0;
    foreach (string metadata in Directory.EnumerateFiles(root, "container.*", SearchOption.AllDirectories))
    {
        string directory = Path.GetDirectoryName(metadata)!;
        string? data = Directory.EnumerateFiles(directory)
            .Where(path => !Path.GetFileName(path).StartsWith("container.", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (data is null) continue;

        WgsGameSaveInfo info = WgsGameSave.Inspect(data);
        PrintGameSave(info);
        Console.WriteLine();
        found++;
    }

    Console.WriteLine($"{found} WGS save stream(s).");
    return found == 0 ? 2 : 0;
}

static void PrintGameSave(WgsGameSaveInfo info)
{
    Console.WriteLine($"Path          : {info.Path}");
    Console.WriteLine($"Kind          : {info.KindLabel}");
    Console.WriteLine($"Format        : {info.FormatDetail}");
    Console.WriteLine($"Size          : {info.Size:N0} bytes");
    Console.WriteLine($"Updated UTC   : {info.LastWriteTimeUtc:O}");
    Console.WriteLine($"SHA-256       : {info.Sha256}");
    Console.WriteLine($"GVAS offset   : {(info.GvasOffset is { } offset ? $"0x{offset:X}" : "not found")}");
    Console.WriteLine($"Build         : {info.Build ?? "not detected"}");
    Console.WriteLine($"Scenario      : {info.ScenarioDisplay}");
    Console.WriteLine($"Difficulty    : {info.Difficulty ?? "not detected"}");
    Console.WriteLine($"Checkpoint    : {info.InternalCheckpoint ?? "not detected"}");
    Console.WriteLine($"Active skulls : {(info.ActiveSkulls.Count == 0 ? "none detected" : string.Join(", ", info.ActiveSkulls))}");
    Console.WriteLine($"Compression   : {(info.CompressedChunkCount is { } chunks
        ? $"{chunks} chunk(s), {info.UncompressedSimulationSize:N0} uncompressed bytes, data at 0x{info.CompressedDataOffset:X}"
        : "not detected")}");
}

static int Dump(string path)
{
    HaloSave save = HaloSave.LoadFile(path);
    Console.WriteLine($"Source        : {path}");
    Console.WriteLine($"Envelope      : {save.Envelope.Description}");
    Console.WriteLine($"Payload bytes : {save.OriginalPayload.Length}");
    Console.WriteLine($"Version byte  : 0x{save.Document.Version:X2}");
    Console.WriteLine();

    foreach (BlamProperty property in save.Document.Root)
        Print(property, 0);

    return 0;

    static void Print(BlamProperty p, int depth)
    {
        string pad = new(' ', depth * 2);
        string type = p.StructTypeName is { } s ? $"{p.TypeName}<{s}>" : p.TypeName;
        Console.WriteLine($"{pad}{p.DisplayName} : {type}  flags=0x{p.Flags:X2}  {p.ValuePreview}");

        if (p.Children is { } children)
        {
            foreach (BlamProperty child in children) Print(child, depth + 1);
        }
    }
}

static int Verify(string path)
{
    HaloSave save = HaloSave.LoadFile(path);
    bool ok = save.VerifyRoundTrip(out string detail);
    Console.WriteLine(ok
        ? $"OK  round trip is byte-exact ({detail})."
        : $"FAIL round trip differs ({detail}).");

    Console.WriteLine($"    {save.Tags.Count} gameplay tag(s), {save.NotifiedTags.Count} notified, " +
                      $"{save.Entitlements.Count} entitlement(s).");

    IReadOnlyList<string> unknown = save.UnknownTags();
    if (unknown.Count > 0)
    {
        Console.WriteLine($"    {unknown.Count} tag(s) not in the built-in catalog:");
        foreach (string tag in unknown) Console.WriteLine($"      {tag}");
    }

    return ok ? 0 : 3;
}

static int Tags(string path, string? filter)
{
    HaloSave save = HaloSave.LoadFile(path);
    IEnumerable<string> tags = save.Tags;

    if (!string.IsNullOrWhiteSpace(filter))
        tags = tags.Where(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase));

    foreach (string tag in tags.OrderBy(t => t, StringComparer.Ordinal))
        Console.WriteLine(tag);

    return 0;
}

static int Base64(string path)
{
    Console.WriteLine(HaloSave.LoadFile(path).BuildBase64());
    return 0;
}

static int Extract(string path, string output)
{
    HaloSave save = HaloSave.LoadFile(path);
    File.WriteAllBytes(output, save.OriginalPayload);
    Console.WriteLine($"Wrote {save.OriginalPayload.Length} byte(s) to {output}.");
    return 0;
}

static int SetTag(string input, string output, string tag, string state)
{
    bool on = state.Equals("on", StringComparison.OrdinalIgnoreCase)
              || state.Equals("true", StringComparison.OrdinalIgnoreCase)
              || state == "1";

    HaloSave save = HaloSave.LoadFile(input);
    bool changed = save.SetTag(tag, on);
    File.WriteAllBytes(output, save.BuildFileBytes());

    Console.WriteLine(changed
        ? $"{(on ? "Added" : "Removed")} {tag}; wrote {output}."
        : $"No change needed for {tag}; wrote {output}.");

    return 0;
}

static int UnlockAll(string input, string output)
{
    HaloSave save = HaloSave.LoadFile(input);
    int changed = 0;

    foreach (string tag in save.KnownTags())
    {
        if (save.SetTag(tag, true)) changed++;
    }

    File.WriteAllBytes(output, save.BuildFileBytes());
    Console.WriteLine($"Added {changed} tag(s); {save.Tags.Count} total. Wrote {output}.");
    return 0;
}

static int FilmInfo(string path)
{
    BlfFilm film = BlfFilm.Load(path);
    Console.WriteLine($"Source          : {film.SourcePath}");
    Console.WriteLine($"Container       : {film.ContainerDescription}");
    Console.WriteLine($"Title / map     : {film.Title}");
    Console.WriteLine($"Description     : {film.Description}");
    Console.WriteLine($"Difficulty      : {Display(film.Difficulty)}");
    Console.WriteLine($"Author          : {Display(film.Author)}");
    Console.WriteLine($"Created         : {film.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
    Console.WriteLine($"Modified        : {film.ModifiedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
    Console.WriteLine($"Scenario        : {Display(film.ScenarioPath)}");
    Console.WriteLine($"Rally point     : {Display(film.RallyPoint)}");
    Console.WriteLine($"Build / session : {Display(film.BuildSession)}");
    Console.WriteLine($"File bytes      : {film.FileLength:N0}");
    Console.WriteLine($"Replay bytes    : {film.FilmDataLength:N0}");
    Console.WriteLine($"Signature data  : {(film.HasNonZeroSignature ? "present" : "zero-filled")}");
    Console.WriteLine($"SHA-256         : {film.Sha256}");
    Console.WriteLine();
    Console.WriteLine("Chunks:");

    foreach (BlfFilmChunk chunk in film.Chunks)
    {
        string lengthNote = chunk.LittleEndianLength ? " (LE stored length)" : "";
        Console.WriteLine(
            $"  {chunk.Tag,-4}  offset=0x{chunk.Offset:X8}  bytes={chunk.ActualLength,10:N0}  " +
            $"version={chunk.MajorVersion}.{chunk.MinorVersion}{lengthNote}");
    }

    Console.WriteLine($"  pad   bytes={film.PaddingLength}");
    return 0;
}

static int FilmVerify(string path)
{
    BlfFilm film = BlfFilm.Load(path);
    Console.WriteLine(
        $"OK  {Path.GetFileName(path)} is a valid saved film " +
        $"({film.Title}, {Display(film.Difficulty)}, {film.FileLength:N0} bytes, " +
        $"SHA-256 {film.Sha256[..12]}…).");
    return 0;
}

static int FilmsScan(string directory, string? jsonOutput)
{
    string fullDirectory = Path.GetFullPath(directory);
    if (!Directory.Exists(fullDirectory))
        throw new DirectoryNotFoundException($"Directory does not exist: {fullDirectory}");

    string[] paths = Directory.GetFiles(fullDirectory, "*.film", SearchOption.TopDirectoryOnly);
    Array.Sort(paths, StringComparer.OrdinalIgnoreCase);

    var entries = new List<object>();
    int valid = 0;
    int invalid = 0;
    long totalBytes = 0;
    var seenHashes = new HashSet<string>(StringComparer.Ordinal);

    Console.WriteLine($"Scanning {fullDirectory}");
    Console.WriteLine();
    Console.WriteLine($"{"Status",-7} {"Map",-5} {"Difficulty",-11} {"Created",-19} {"MiB",8}  File");

    foreach (string path in paths)
    {
        try
        {
            BlfFilm film = BlfFilm.Load(path);
            valid++;
            totalBytes += film.FileLength;
            bool duplicate = !seenHashes.Add(film.Sha256);
            string status = duplicate ? "DUP" : "OK";

            Console.WriteLine(
                $"{status,-7} {film.Title,-5} {Display(film.Difficulty),-11} " +
                $"{film.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} " +
                $"{film.FileLength / 1024d / 1024d,8:F2}  {Path.GetFileName(path)}");

            entries.Add(new
            {
                status = duplicate ? "duplicate" : "valid",
                file = Path.GetFileName(path),
                path = film.SourcePath,
                bytes = film.FileLength,
                replayBytes = film.FilmDataLength,
                sha256 = film.Sha256,
                map = film.Title,
                difficulty = film.Difficulty,
                description = film.Description,
                author = film.Author,
                createdUtc = film.CreatedAtUtc,
                modifiedUtc = film.ModifiedAtUtc,
                scenarioPath = film.ScenarioPath,
                rallyPoint = film.RallyPoint,
                buildSession = film.BuildSession,
                signatureDataPresent = film.HasNonZeroSignature,
            });
        }
        catch (Exception ex) when (ex is BlamFormatException or IOException or UnauthorizedAccessException)
        {
            invalid++;
            Console.WriteLine($"FAIL    {"-",5} {"-",11} {"-",19} {"-",8}  {Path.GetFileName(path)}");
            Console.Error.WriteLine($"  {ex.Message}");
            entries.Add(new
            {
                status = "invalid",
                file = Path.GetFileName(path),
                path = Path.GetFullPath(path),
                error = ex.Message,
            });
        }
    }

    Console.WriteLine();
    Console.WriteLine(
        $"{valid} valid, {invalid} invalid, {seenHashes.Count} unique, " +
        $"{totalBytes / 1024d / 1024d:F2} MiB.");

    if (!string.IsNullOrWhiteSpace(jsonOutput))
    {
        string jsonPath = Path.GetFullPath(jsonOutput);
        string json = System.Text.Json.JsonSerializer.Serialize(
            entries,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(jsonPath, json);
        Console.WriteLine($"Wrote index to {jsonPath}.");
    }

    return invalid == 0 ? 0 : 3;
}

static int FilmExtract(string input, string output)
{
    BlfFilm film = BlfFilm.Load(input);
    film.ExtractFilmData(output);
    Console.WriteLine(
        $"Wrote {film.FilmDataLength:N0} replay byte(s) from {Path.GetFileName(input)} to {output}.");
    return 0;
}

static int FilmsArchive(string sourceDirectory, string archiveDirectory)
{
    string source = Path.GetFullPath(sourceDirectory);
    string archive = Path.GetFullPath(archiveDirectory);
    if (!Directory.Exists(source))
        throw new DirectoryNotFoundException($"Source directory does not exist: {source}");

    Directory.CreateDirectory(archive);

    var existingHashes = new HashSet<string>(StringComparer.Ordinal);
    foreach (string existingPath in Directory.GetFiles(archive, "*.film", SearchOption.TopDirectoryOnly))
    {
        try
        {
            existingHashes.Add(BlfFilm.Load(existingPath).Sha256);
        }
        catch (Exception ex) when (ex is BlamFormatException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"Warning: existing archive file {Path.GetFileName(existingPath)} is invalid: {ex.Message}");
        }
    }

    int copied = 0;
    int duplicate = 0;
    int failed = 0;

    foreach (string sourcePath in Directory.GetFiles(source, "*.film", SearchOption.TopDirectoryOnly))
    {
        try
        {
            BlfFilm film = BlfFilm.Load(sourcePath);
            if (!existingHashes.Add(film.Sha256))
            {
                duplicate++;
                continue;
            }

            string safeMap = string.Concat(
                film.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            if (string.IsNullOrWhiteSpace(safeMap)) safeMap = "unknown";

            string destinationName =
                $"{film.CreatedAtUtc:yyyyMMdd-HHmmss}_{safeMap}_{film.Sha256[..12]}.film";
            string destinationPath = Path.Combine(archive, destinationName);
            File.Copy(sourcePath, destinationPath, overwrite: false);
            copied++;
            Console.WriteLine($"Archived {Path.GetFileName(sourcePath)} -> {destinationName}");
        }
        catch (Exception ex) when (ex is BlamFormatException or IOException or UnauthorizedAccessException)
        {
            failed++;
            Console.Error.WriteLine($"Failed to archive {Path.GetFileName(sourcePath)}: {ex.Message}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"{copied} copied, {duplicate} already archived, {failed} failed.");

    string indexPath = Path.Combine(archive, "index.json");
    int scanResult = FilmsScan(archive, indexPath);
    return failed == 0 && scanResult == 0 ? 0 : 3;
}

static string Display(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
