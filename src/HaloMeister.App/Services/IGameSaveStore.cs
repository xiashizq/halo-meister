namespace HaloMeister.App.Services;

public interface IGameSaveStore
{
    string PlatformId { get; }
    string LiveRoot { get; }
    string BackupRoot { get; }
    bool IsGameRunning { get; }
    bool LiveRootExists { get; }

    IReadOnlyList<WgsSaveSlot> Discover();
    IReadOnlyList<WgsBackupEntry> DiscoverBackups();
    WgsBackupResult BackupAll(string reason);
    WgsBackupEntry ExportToLibrary(WgsSaveSlot slot);
    WgsBackupEntry ImportToLibrary(string sourcePath);
    WgsReplaceResult RestoreBackup(WgsSaveSlot target, WgsBackupEntry backup);
    Task<bool> LaunchGameAsync();
}
