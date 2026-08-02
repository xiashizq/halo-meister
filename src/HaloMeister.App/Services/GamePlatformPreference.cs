using Windows.System;

namespace HaloMeister.App.Services;

public enum GamePlatformKind
{
    Steam,
    MicrosoftStore,
}

/// <summary>
/// Shared Steam vs Microsoft Store preference for launch and save tooling.
/// </summary>
public sealed class GamePlatformPreference
{
    private readonly string _path;
    private GamePlatformKind _platform;
    private bool _loaded;

    private GamePlatformPreference()
    {
        _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HaloMeister",
            "game-platform.txt");
        _platform = GamePlatformKind.Steam;
    }

    public static GamePlatformPreference Current { get; } = new();

    public event EventHandler? Changed;

    public GamePlatformKind Platform
    {
        get
        {
            EnsureLoaded();
            return _platform;
        }
        set
        {
            EnsureLoaded();
            if (_platform == value) return;
            _platform = value;
            Persist();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsSteam => Platform == GamePlatformKind.Steam;

    public string PlatformId => IsSteam
        ? SteamGameSaveStore.Platform
        : WgsGameSaveStore.Platform;

    public Uri LaunchUri => IsSteam
        ? new Uri($"steam://rungameid/{SteamGameSaveStore.SteamAppId}")
        : new Uri("ms-xbl-7c27bae7:");

    public Task<bool> LaunchGameAsync() => Launcher.LaunchUriAsync(LaunchUri).AsTask();

    public static GamePlatformKind Parse(string? value) =>
        string.Equals(value, WgsGameSaveStore.Platform, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "microsoftstore", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "store", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "msstore", StringComparison.OrdinalIgnoreCase)
            ? GamePlatformKind.MicrosoftStore
            : GamePlatformKind.Steam;

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(_path))
            {
                _platform = Parse(File.ReadAllText(_path).Trim());
                return;
            }
        }
        catch
        {
            // Fall through to heuristic default.
        }

        _platform = DetectDefault();
        Persist();
    }

    private static GamePlatformKind DetectDefault()
    {
        try
        {
            var steam = new SteamGameSaveStore();
            var store = new WgsGameSaveStore();
            bool steamHasSaves = steam.LiveRootExists && steam.Discover().Count > 0;
            bool storeHasSaves = store.LiveRootExists && store.Discover().Count > 0;
            if (!steamHasSaves && storeHasSaves)
                return GamePlatformKind.MicrosoftStore;
        }
        catch
        {
            // Prefer Steam when discovery fails.
        }

        return GamePlatformKind.Steam;
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(
                _path,
                IsSteam ? SteamGameSaveStore.Platform : WgsGameSaveStore.Platform);
        }
        catch
        {
            // Preference is best-effort; launch still works for the in-memory value.
        }
    }
}
