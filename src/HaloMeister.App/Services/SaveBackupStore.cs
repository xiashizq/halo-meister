using System.Security.Cryptography;

namespace HaloMeister.App.Services;

/// <summary>
/// Keeps privacy-conscious raw-container snapshots. PlayFab envelopes can contain account
/// metadata, so backups intentionally contain only the save itself.
/// </summary>
public sealed class SaveBackupStore
{
    public SaveBackupStore()
    {
        DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HaloMeister",
            "Backups");
    }

    public string DirectoryPath { get; }

    public string Save(byte[] container, string reason)
    {
        Directory.CreateDirectory(DirectoryPath);

        string hash = Convert.ToHexString(SHA256.HashData(container))[..12];
        string? existing = Directory.EnumerateFiles(DirectoryPath, $"*-{hash}.sav")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (existing is not null)
            return existing;

        string safeReason = string.Concat(reason.Select(c =>
            char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-')).Trim('-');
        if (safeReason.Length == 0) safeReason = "snapshot";

        string path = Path.Combine(
            DirectoryPath,
            $"{DateTime.Now:yyyyMMdd-HHmmssfff}-{safeReason}-{hash}.sav");
        File.WriteAllBytes(path, container);
        return path;
    }

    public string? Latest()
    {
        if (!Directory.Exists(DirectoryPath))
            return null;

        return Directory.EnumerateFiles(DirectoryPath, "*.sav")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}