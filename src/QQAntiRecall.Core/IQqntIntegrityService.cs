namespace QQAntiRecall.Core;

/// <summary>
/// Describes the verified state of one QQNT.dll integrity-check target.
/// </summary>
public enum QqntIntegrityState
{
    /// <summary>The delayed integrity check is intact, so QQ still exits about 15 minutes after start.</summary>
    ReadyToPatch,

    /// <summary>The failure branch is disabled, so a failed check no longer terminates QQ.</summary>
    Patched,

    /// <summary>The delayed verification call was removed by the earlier command-line tool.</summary>
    SkipPatched,

    /// <summary>The image is readable but no recognised patch site was found, so it is left untouched.</summary>
    Unsupported,

    /// <summary>The image could not be read, so nothing is known about it.</summary>
    Unreadable,
}

/// <summary>
/// Reports the verified state of one versioned QQNT.dll target.
/// </summary>
public sealed record QqntIntegrityTargetResult(
    string Version,
    string FilePath,
    QqntIntegrityState State,
    long FileSize,
    string Sha256,
    string BackupPath,
    bool HasBackup,
    string Detail);

/// <summary>
/// Reports the complete QQNT.dll integrity-check state of one installation root.
/// </summary>
public sealed record QqntIntegrityScanResult(
    string InstallRoot,
    bool IsPlatformSupported,
    bool IsQqRunning,
    IReadOnlyList<QqntIntegrityTargetResult> Targets,
    bool CanApply,
    bool CanRestore,
    string Summary);

/// <summary>
/// Reports the result of an apply or restore operation and its refreshed scan.
/// </summary>
public sealed record QqntIntegrityOperationResult(
    QqntIntegrityScanResult Scan,
    string Message,
    bool Succeeded = false);

/// <summary>
/// Detects and neutralises the QQNT.dll delayed integrity check that terminates patched QQ clients.
/// </summary>
public interface IQqntIntegrityService
{
    /// <summary>
    /// Scans every installed QQNT.dll without modifying files.
    /// </summary>
    /// <param name="installRoot">QQ installation root that contains QQ.exe and versions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verified integrity state of every discovered DLL.</returns>
    Task<QqntIntegrityScanResult> ScanAsync(string installRoot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disables the delayed integrity check in every patchable QQNT.dll, saving an original backup first.
    /// </summary>
    /// <param name="installRoot">QQ installation root that contains QQ.exe and versions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The refreshed scan and the operation message.</returns>
    Task<QqntIntegrityOperationResult> ApplyAsync(string installRoot, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores every patched QQNT.dll from the original bytes saved next to it.
    /// </summary>
    /// <param name="installRoot">QQ installation root that contains QQ.exe and versions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The refreshed scan and the operation message.</returns>
    Task<QqntIntegrityOperationResult> RestoreAsync(string installRoot, CancellationToken cancellationToken = default);
}
