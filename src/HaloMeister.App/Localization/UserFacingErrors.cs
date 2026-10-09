using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Controls;

namespace HaloMeister.App.Localization;

/// <summary>
/// Turns exceptions and raw bridge/native messages into text that is safe to show to players.
/// Anything that exposes game-resource internals (tag paths, tag groups, datum handles,
/// scenario/schema jargon, memory addresses, engine names, ...) is replaced by a plain-language
/// message; the original text is written to a local log instead.
/// </summary>
public static class UserFacingErrors
{
    // Words that only make sense to someone who knows the game's internal data model.
    // Identifier-style matching so that neighbouring CJK text does not defeat \b.
    private static readonly Regex InternalTerms = new(
        @"(?<![A-Za-z0-9_])(?:" +
        @"scenarios?|scnr|tags?|tagmods?|datums?|palettes?|schemas?|string-?ids?|" +
        @"blam\w*|unreal|uobject|kismet|uehelpers|gameplaystatics|ue4ss|lua|" +
        @"wwise|paks?|hlmt|matg|weap|vehi|bipd|" +
        @"haloscript|halosimulation\w*|halo_campaign_evolved_root|sha-?256|pe\s+(?:header|image)|" +
        @"utoc|ucas|bnk|wem" +
        @")(?![A-Za-z0-9_])" +
        @"|\[[A-Za-z][A-Za-z0-9_ ]{3}\]" +                  // [matg], [vehi], [weap]...
        @"|(?<![A-Za-z0-9_])0x[0-9A-Fa-f]{2,}(?![A-Za-z0-9_])" + // memory addresses / handles
        @"|(?<![A-Za-z0-9_])(?:objects|globals|levels|sound|sounds|scenarios|characters|vehicles|weapons|effects|fx|ui|camera|cinematics)[\\/][\w\.\\/\-]+" +
        @"|\.(?:utoc|ucas|pak)\b" +
        @"|标签|句柄|调色板|场景架构|数据块",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MissionData = new(
        @"mission|scenario|not loaded|no longer loaded|are loaded|were found|loaded|任务|关卡|未加载|没有加载|已不再加载",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex OutdatedBridge = new(
        @"unsupported|not supported|outdated|unknown (?:script|operation|request) kind|bridge version",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BridgeMissionState = new(
        @"mission|campaign|world|pawn|player|level|unavailable|not loaded|not found|could not find|no longer",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly object LogGate = new();

    /// <summary>
    /// Known developer-written messages (English) mapped to clear, localized player text.
    /// First match wins, so the more specific rules come first.
    /// </summary>
    private static readonly (Regex Pattern, string Key)[] Rules =
    [
        Rule(@"being used by another process|正由另一进程使用", "common.error_file_in_use"),
        Rule(@"connect (?:to|halo meister to) (?:the )?(?:running|game|a loaded)|not connected|no longer connected|game process (?:exited|identity)|from the header first|no runtime identity|game is not running", "common.error_not_connected"),
        Rule(@"\[bipd\].*not published|no authored unit reference", "common.error_character_unavailable"),
        Rule(@"ai donor|friendly donor", "common.error_no_donor"),
        Rule(@"close (?:the game|halo: campaign evolved)|campaign evolved is running", "common.error_close_game"),
        Rule(@"could not find haloc\w*\.exe", "common.error_game_folder"),
        Rule(@"ue4ss installation|ue4ss\\mods", "common.error_live_tools_missing"),
        Rule(@"sha-?256 verification|download .*(?:large|exceeded)|unsafe path|escapes the|unexpected layout|invalid installation markers", "common.error_download_verify"),
        Rule(@"packaged .*(?:missing|invalid)|bundled .*missing|release zip|build[- ]profile (?:has no|catalog|address|'|invalid)|invalid hexadecimal|signatures are missing|ue4ss\.dll|mods\.txt|loader layout|no assembly version", "common.error_install_broken"),
        Rule(@"no supported game build|for build '|build profile .* not enabled", "build_profile.unsupported_dll_short"),
        Rule(@"playfab (?:authentication|rejected)|authenticate first", "common.error_playfab_auth"),
        Rule(@"github returned|release metadata", "common.error_update_check"),
        Rule(@"tcp port|already in use", "common.error_port_in_use"),
        Rule(@"firewall|powershell|administrator approval|executable path", "common.error_firewall"),
        Rule(@"internet settings", "common.error_windows_proxy"),
        Rule(@"selected archive|archive does not contain|\.halo-wgs|target slot|data entry", "common.error_archive_invalid"),
        Rule(@"config changes|changed on disk|no longer exists\. reload|backup is not available|was not found\. run campaign|is missing from .*customization|reload the customization|backup entry", "common.error_file_changed"),
        Rule(@"reload the checkpoint|checkpoint failed|rebuilt checkpoint|writable saved actor|vitality record|record at 0x", "common.error_save_changed"),
        Rule(@"belongs to a different mission", "common.error_wrong_mission"),
        Rule(@"location names|saved (?:machinima |player )?locations?|saved location", "common.error_location_invalid"),
        Rule(@"must be between|must be a whole number|comma-separated|printable characters|must be shorter", "common.error_value_range"),
        Rule(@"squad member", "common.error_no_squad_members"),
        Rule(@"usable spawn points", "common.error_no_spawn_points"),
        Rule(@"stanchion|sniper-rifle|cooked-tag", "common.error_weapon_unavailable"),
        Rule(@"did not finish the|already pending|timed out|mailbox", "common.error_timeout"),
        Rule(@"no longer (?:loaded|available|exists|part of)|not loaded|changed after the (?:scan|preview)|rescan|refresh and|refresh the|scan the .* (?:again|first)|scan first", "common.error_item_gone"),
        Rule(@"no loaded|loaded in this mission|in this mission|(?:load|resume) (?:or resume )?an? (?:offline )?campaign|resume a campaign|offline campaign mission|has no readable|no .* (?:were|was) found|no projectile|no represented|no compatible live|are loaded|player representation|no loaded", "common.error_mission_not_ready"),
        Rule(@"definitions do not provide|schema|did not resolve the requested|tag definition|inheritance cycle", "common.error_need_update"),
        Rule(@"does not expose|exposes no|no default model variant|no (?:usable )?authored|no writable|no resolvable|has no .*(?:reference|variant)|no variants|cannot be written|read-only|no staged", "common.error_item_unusable"),
        Rule(@"memory|stale|string-id|arena|runtime tag table|datum|layout may have changed|reconnect|rollback|not writable|did not retain|physical-wall|did not confirm|segmented", "common.error_game_changed"),
        Rule(@"returned|invalid|unexpected|implausible|did not spawn|no (?:player weapon|magazine)", "common.error_game_response"),
    ];

    private static (Regex, string) Rule(string pattern, string key)
        => (new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled), key);

    private static bool TryRule(string text, out string message)
    {
        // Rules are written against the original English developer text only
        // (plus the localized Windows "file in use" wording).
        if (text.Contains("正由另一进程使用", StringComparison.Ordinal))
        {
            message = L.Get("common.error_file_in_use");
            return true;
        }

        if (!ContainsCjk(text))
        {
            foreach ((Regex pattern, string key) in Rules)
            {
                if (pattern.IsMatch(text))
                {
                    message = L.Get(key);
                    return true;
                }
            }
        }

        message = string.Empty;
        return false;
    }

    /// <summary>
    /// Message for an InfoBar / status line. Only errors and warnings are filtered.
    /// </summary>
    public static string ForDisplay(string? message, InfoBarSeverity severity)
        => severity is InfoBarSeverity.Error or InfoBarSeverity.Warning
            ? Sanitize(message)
            : message ?? string.Empty;

    /// <summary>Player-safe text for an exception.</summary>
    public static string Format(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is IOException io && (io.HResult & 0xFFFF) is 32 or 33)
                return L.Get("common.error_file_in_use");
            if (current is TimeoutException)
                return L.Get("common.error_timeout");
            if (current is BridgeFailureException)
                return FromBridge(current.Message);
        }

        return Sanitize(ex.Message);
    }

    /// <summary>
    /// Player-safe text for a failure message that came straight from the in-game bridge.
    /// Those messages are written for developers (engine objects, handles, Lua/Unreal errors),
    /// so anything that is not already localized app text is replaced by a plain-language hint.
    /// The original text is logged.
    /// </summary>
    public static string FromBridge(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return L.Get("common.error_generic");

        string text = message.Trim();
        if (ContainsCjk(text))
            return Sanitize(text);

        Log(text);
        if (TryRule(text, out string known))
            return known;
        if (OutdatedBridge.IsMatch(text))
            return L.Get("common.error_bridge_outdated");
        if (BridgeMissionState.IsMatch(text))
            return L.Get("common.error_mission_not_ready");
        return L.Get("common.error_generic");
    }

    private static bool LooksLikeEnglishSentence(string text)
    {
        int words = 0;
        foreach (string part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length >= 2 && char.IsLetter(part[0]))
                words++;
        }

        return words >= 3;
    }

    private static bool ContainsCjk(string text)
    {
        foreach (char c in text)
        {
            if (c is >= '\u3040' and <= '\u30FF' or >= '\u3400' and <= '\u9FFF' or >= '\uAC00' and <= '\uD7AF')
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns <paramref name="message"/> unchanged when it is already player-friendly,
    /// otherwise a plain-language replacement.
    /// </summary>
    public static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return L.Get("common.error_generic");

        string text = message.Trim();

        if (text.Contains("正由另一进程使用", StringComparison.Ordinal))
            return L.Get("common.error_file_in_use");

        if (TryRule(text, out string known))
        {
            Log(text);
            return known;
        }

        bool internals = InternalTerms.IsMatch(text);
        // Untranslated developer English must never reach a non-English UI.
        bool untranslated = !ContainsCjk(text)
            && LocalizationService.Current.Language != LocalizationService.English
            && LooksLikeEnglishSentence(text);
        if (!internals && !untranslated)
            return message;

        Log(text);
        return MissionData.IsMatch(text)
            ? L.Get("common.error_mission_not_ready")
            : L.Get("common.error_generic");
    }

    /// <summary>True when the text would be replaced by <see cref="Sanitize"/>.</summary>
    public static bool ExposesInternals(string? message)
        => !string.IsNullOrWhiteSpace(message) && InternalTerms.IsMatch(message);

    /// <summary>
    /// Records technical details for support without showing them in the UI.
    /// Written to %LocalAppData%\HaloMeister\errors.log.
    /// </summary>
    public static void Log(string detail)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppPaths.DataFolderName);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "errors.log");
            lock (LogGate)
            {
                // Keep the log small: start over once it grows past 512 KiB.
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
                    File.Delete(path);
                File.AppendAllText(
                    path,
                    $"[{DateTime.Now:O}] {detail}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Best-effort logging only.
        }
    }
}

/// <summary>
/// A failure reported by the in-game bridge. <see cref="Exception.Message"/> keeps the raw
/// text for code that inspects it; <see cref="UserFacingErrors.Format"/> never shows it as-is.
/// </summary>
public sealed class BridgeFailureException : InvalidOperationException
{
    public BridgeFailureException(string? message)
        : base(message ?? string.Empty)
    {
    }
}
