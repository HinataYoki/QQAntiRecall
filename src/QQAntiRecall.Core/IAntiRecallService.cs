namespace QQAntiRecall.Core;

/// <summary>
/// Describes the aggregate patch state of one QQ version target.
/// </summary>
public enum TargetPatchState
{
    Missing,
    ReadyToInstall,
    Installed,
    Inconsistent,
    Unsupported,
}

/// <summary>
/// Reports original and patched signature counts for one required patch.
/// </summary>
public sealed record PatchSignatureStatus(
    string Name,
    int OriginalMatchCount,
    int PatchedMatchCount);

/// <summary>
/// Reports the verified state of one versioned wrapper.node target.
/// </summary>
public sealed record TargetScanResult(
    string Version,
    string FilePath,
    TargetPatchState State,
    string Sha256,
    IReadOnlyList<PatchSignatureStatus> Signatures,
    string Detail);

/// <summary>
/// Reports the complete state required by the desktop workflow.
/// </summary>
public sealed record AntiRecallScanResult(
    string InstallRoot,
    bool IsPlatformSupported,
    bool IsQqRunning,
    IReadOnlyList<TargetScanResult> Targets,
    string? LatestBackupId,
    bool CanInstall,
    bool CanRestore,
    string Summary);

/// <summary>
/// Reports the result of an install or restore operation and its refreshed scan.
/// </summary>
public sealed record PatchOperationResult(
    AntiRecallScanResult Scan,
    string Message,
    bool Succeeded = false);

/// <summary>
/// Describes managed backups that are safe to remove because their QQ target set is obsolete or duplicated.
/// </summary>
public sealed record BackupCleanupPreview(
    IReadOnlyList<string> BackupIds,
    long ReclaimableBytes,
    int RetainedBackupCount,
    int UnrecognizedDirectoryCount)
{
    /// <summary>
    /// Gets the number of exact backup directories included in this preview.
    /// </summary>
    public int BackupCount => BackupIds.Count;
}

/// <summary>
/// Reports the outcome of deleting only the approved obsolete backup directories.
/// </summary>
public sealed record BackupCleanupResult(
    int DeletedBackupCount,
    long ReclaimedBytes,
    int SkippedBackupCount,
    string Message,
    bool Succeeded);

/// <summary>
/// Provides QQ discovery and transactional anti-recall patch operations.
/// </summary>
public interface IAntiRecallService
{
    /// <summary>
    /// Gets the application-owned directory that stores verified QQ backups.
    /// </summary>
    string BackupDirectoryPath { get; }

    /// <summary>
    /// Finds the most likely QQ installation root without modifying it.
    /// </summary>
    string? FindInstallRoot();

    /// <summary>
    /// Scans the configured current and ready QQ versions without modifying files.
    /// </summary>
    Task<AntiRecallScanResult> ScanAsync(string installRoot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs the complete three-signature patch set as one transactional operation.
    /// </summary>
    Task<PatchOperationResult> InstallAsync(string installRoot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes a running QQ instance, installs the complete patch set, and restarts QQ from the verified installation root.
    /// </summary>
    Task<PatchOperationResult> CloseQqInstallAndRestartAsync(
        string installRoot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores the newest compatible verified backup as one transactional operation.
    /// </summary>
    Task<PatchOperationResult> RestoreAsync(string installRoot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds obsolete or exactly duplicated managed backups without deleting anything.
    /// </summary>
    Task<BackupCleanupPreview> PreviewBackupCleanupAsync(
        string installRoot,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revalidates and deletes only obsolete backups whose identifiers were explicitly approved.
    /// </summary>
    Task<BackupCleanupResult> CleanupObsoleteBackupsAsync(
        string installRoot,
        IReadOnlyCollection<string> approvedBackupIds,
        CancellationToken cancellationToken = default);
}
