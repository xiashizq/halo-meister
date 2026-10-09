using System.Globalization;
using System.Reflection;

namespace HaloMeister.App.Services;

/// <summary>
/// Scripting-bridge version. New builds use the application SemVer from
/// <c>Directory.Build.props</c> (<c>major.minor.patch</c>). Heartbeats that still
/// report the retired integer scheme stay readable and sort before every SemVer.
/// </summary>
public readonly record struct BridgeVersion : IComparable<BridgeVersion>
{
    public static BridgeVersion Application { get; } = FromAssembly();

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public int? Legacy { get; }

    public bool IsLegacy => Legacy is not null;

    public BridgeVersion(int major, int minor, int patch)
    {
        if (major < 0 || minor < 0 || patch < 0)
            throw new ArgumentOutOfRangeException(nameof(major));
        Major = major;
        Minor = minor;
        Patch = patch;
    }

    private BridgeVersion(int legacy)
    {
        Legacy = legacy;
    }

    public static bool TryParse(string? text, out BridgeVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim();
        if (text.Length > 1 && (text[0] == 'v' || text[0] == 'V'))
            text = text[1..];
        if (text.Contains('.', StringComparison.Ordinal))
        {
            string[] parts = text.Split('.');
            if (parts.Length is < 2 or > 4)
                return false;
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minor))
                return false;
            int patch = 0;
            if (parts.Length >= 3 &&
                !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out patch))
                return false;
            if (major < 0 || minor < 0 || patch < 0)
                return false;
            version = new BridgeVersion(major, minor, patch);
            return true;
        }

        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int legacy) &&
            legacy >= 0)
        {
            version = new BridgeVersion(legacy);
            return true;
        }

        return false;
    }

    public int CompareTo(BridgeVersion other)
    {
        if (IsLegacy != other.IsLegacy)
            return IsLegacy ? -1 : 1;
        if (IsLegacy)
            return Legacy!.Value.CompareTo(other.Legacy!.Value);
        int major = Major.CompareTo(other.Major);
        if (major != 0)
            return major;
        int minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(BridgeVersion left, BridgeVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(BridgeVersion left, BridgeVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(BridgeVersion left, BridgeVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(BridgeVersion left, BridgeVersion right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// SemVer bridges include every feature that used to be gated on an integer build.
    /// A legacy heartbeat must itself be at least <paramref name="minimumLegacyBuild"/>.
    /// </summary>
    public bool IncludesLegacyFeature(int minimumLegacyBuild) =>
        !IsLegacy || Legacy!.Value >= minimumLegacyBuild;

    public override string ToString() =>
        IsLegacy
            ? Legacy!.Value.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    private static BridgeVersion FromAssembly()
    {
        Version version = typeof(BridgeVersion).Assembly.GetName().Version
            ?? throw new InvalidOperationException("Cartographer Toolkit has no assembly version.");
        int patch = version.Build < 0 ? 0 : version.Build;
        return new BridgeVersion(version.Major, version.Minor, patch);
    }
}

public static class BridgeVersionExtensions
{
    public static bool Supports(this BridgeVersion? version, BridgeVersion minimum) =>
        version is BridgeVersion value && value >= minimum;

    public static bool SupportsLegacyFeature(this BridgeVersion? version, int minimumLegacyBuild) =>
        version is BridgeVersion value && value.IncludesLegacyFeature(minimumLegacyBuild);
}
