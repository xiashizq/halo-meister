using System.Text.RegularExpressions;

namespace HaloMeister.App.Services;

public sealed record HaloScriptReference(
    string Name,
    string Signature,
    string ReturnType,
    bool IsGlobal)
{
    public string Kind => IsGlobal ? "GLOBAL" : "FUNCTION";

    public override string ToString() => Signature;
}

public static partial class HaloScriptCatalog
{
    private static readonly string[] StarterNames =
    [
        "chud_show",
        "unit_kill",
        "fade_out",
        "fade_in",
        "print",
        "sleep",
        "sleep_until",
        "player_get",
        "object_create",
        "object_destroy",
        "ai_place",
        "ai_erase",
    ];

    private static IReadOnlyList<HaloScriptReference>? _cached;

    public static IReadOnlyList<HaloScriptReference> Load()
        => _cached ??= LoadCore();

    public static IReadOnlyList<HaloScriptReference> Search(
        string? query,
        int maximum = 80)
    {
        IReadOnlyList<HaloScriptReference> catalog = Load();
        if (string.IsNullOrWhiteSpace(query))
        {
            var starters = new List<HaloScriptReference>();
            foreach (string name in StarterNames)
                starters.AddRange(catalog.Where(item => item.Name == name));
            return starters.Take(maximum).ToArray();
        }

        string term = query.Trim();
        return catalog
            .Where(item =>
                item.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Signature.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => Score(item, term))
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maximum)
            .ToArray();
    }

    public static string CreateInsertion(HaloScriptReference item)
        => item.IsGlobal ? item.Name : $"({item.Name} )";

    private static IReadOnlyList<HaloScriptReference> LoadCore()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "HaloScript",
            "hs_doc.txt");
        if (!File.Exists(path))
            return Array.Empty<HaloScriptReference>();

        var items = new List<HaloScriptReference>();
        bool globals = false;
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith("; AVAILABLE EXTERNAL GLOBALS:", StringComparison.Ordinal))
            {
                globals = true;
                continue;
            }

            Match match = SignatureLine().Match(line);
            if (!match.Success)
                continue;
            items.Add(new HaloScriptReference(
                match.Groups["name"].Value,
                line.Trim(),
                match.Groups["return"].Value,
                globals));
        }
        return items;
    }

    private static int Score(HaloScriptReference item, string term)
    {
        if (item.Name.Equals(term, StringComparison.OrdinalIgnoreCase)) return 0;
        if (item.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return 1;
        if (item.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    [GeneratedRegex(
        @"^\(<(?<return>[^>]+)>\s+(?<name>[^\s\)]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SignatureLine();
}
