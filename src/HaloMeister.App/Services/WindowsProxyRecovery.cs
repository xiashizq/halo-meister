using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace HaloMeister.App.Services;

/// <summary>
/// Persists the user's WinINet proxy settings before capture. Titanium restores settings
/// during a clean stop; this snapshot also repairs an orphaned Halo Meister proxy on the
/// next launch after a crash or debugger termination.
/// </summary>
internal sealed class WindowsProxyRecovery
{
    private const string InternetSettingsPath =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private readonly string _recoveryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HaloMeister",
        "proxy-recovery.json");

    public void RecoverStaleProxy()
    {
        if (File.Exists(_recoveryPath))
            Restore();
        else
            DisableKnownOrphan();
    }

    public void Capture()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_recoveryPath)!);
        using RegistryKey key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: false)
            ?? throw new InvalidOperationException("Windows Internet Settings are unavailable.");

        var snapshot = new ProxySnapshot(
            ReadValue(key, "ProxyEnable"),
            ReadValue(key, "ProxyServer"),
            ReadValue(key, "ProxyOverride"),
            ReadValue(key, "AutoConfigURL"));

        File.WriteAllText(_recoveryPath, JsonSerializer.Serialize(snapshot));
    }

    public void ForceIpv4Loopback(int port)
    {
        using RegistryKey key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true)
            ?? throw new InvalidOperationException("Windows Internet Settings are unavailable.");

        // Titanium writes "localhost". Some clients resolve that to ::1 while the proxy
        // endpoint is intentionally IPv4-only. Pinning 127.0.0.1 avoids that black hole.
        key.SetValue("ProxyServer", $"http=127.0.0.1:{port};https=127.0.0.1:{port}", RegistryValueKind.String);
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        NotifyWindows();
    }

    public void Restore()
    {
        if (!File.Exists(_recoveryPath)) return;

        ProxySnapshot? snapshot = JsonSerializer.Deserialize<ProxySnapshot>(
            File.ReadAllText(_recoveryPath));
        if (snapshot is null) return;

        using RegistryKey key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true)
            ?? throw new InvalidOperationException("Windows Internet Settings are unavailable.");

        RestoreValue(key, "ProxyEnable", snapshot.ProxyEnable);
        RestoreValue(key, "ProxyServer", snapshot.ProxyServer);
        RestoreValue(key, "ProxyOverride", snapshot.ProxyOverride);
        RestoreValue(key, "AutoConfigURL", snapshot.AutoConfigUrl);
        File.Delete(_recoveryPath);
        NotifyWindows();
    }

    private static void DisableKnownOrphan()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true);
        if (key?.GetValue("ProxyServer") is not string server
            || !server.Contains("localhost:8877", StringComparison.OrdinalIgnoreCase)
               && !server.Contains("127.0.0.1:8877", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        NotifyWindows();
    }

    private static RegistryValue ReadValue(RegistryKey key, string name)
    {
        object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return new RegistryValue(
            value is not null,
            value?.ToString(),
            value is null ? RegistryValueKind.None : key.GetValueKind(name));
    }

    private static void RestoreValue(RegistryKey key, string name, RegistryValue value)
    {
        if (!value.Exists)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
            return;
        }

        object restored = value.Kind == RegistryValueKind.DWord
            ? int.Parse(value.Value ?? "0")
            : value.Value ?? string.Empty;
        key.SetValue(name, restored, value.Kind);
    }

    private static void NotifyWindows()
    {
        InternetSetOption(nint.Zero, 39, nint.Zero, 0); // INTERNET_OPTION_SETTINGS_CHANGED
        InternetSetOption(nint.Zero, 37, nint.Zero, 0); // INTERNET_OPTION_REFRESH
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(nint internet, int option, nint buffer, int length);

    private sealed record ProxySnapshot(
        RegistryValue ProxyEnable,
        RegistryValue ProxyServer,
        RegistryValue ProxyOverride,
        RegistryValue AutoConfigUrl);

    private sealed record RegistryValue(bool Exists, string? Value, RegistryValueKind Kind);
}
