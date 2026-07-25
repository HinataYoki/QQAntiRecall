using System.Threading;
using System.Threading.Tasks;

namespace QQAntiRecall.App.Services;

/// <summary>
/// Abstracts native folder selection and destructive-operation confirmation from ViewModels.
/// </summary>
public interface IUserInteractionService
{
    /// <summary>
    /// Opens an application-owned local directory in the platform file manager, creating it when absent.
    /// </summary>
    /// <param name="directoryPath">Absolute local directory to open.</param>
    /// <param name="cancellationToken">Cancellation requested by the application lifetime.</param>
    Task OpenDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a platform-native folder picker for the QQ installation root.
    /// </summary>
    /// <param name="currentPath">Current path used as context when available.</param>
    /// <param name="cancellationToken">Cancellation requested by the application lifetime.</param>
    /// <returns>The selected local directory, or <see langword="null"/> when the picker is dismissed.</returns>
    Task<string?> PickInstallDirectoryAsync(
        string? currentPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms installation of the complete patch set for all scanned targets.
    /// </summary>
    /// <param name="installPath">QQ installation root shown to the user.</param>
    /// <param name="targetCount">Number of version targets that may be modified.</param>
    /// <param name="cancellationToken">Cancellation requested by the application lifetime.</param>
    /// <returns><see langword="true"/> only when the user explicitly approves installation.</returns>
    Task<bool> ConfirmInstallAsync(
        string installPath,
        int targetCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms that QQ may be closed before installation and restarted when the operation finishes.
    /// </summary>
    /// <param name="installPath">Verified QQ installation root shown to the user.</param>
    /// <param name="targetCount">Number of version targets that may be modified.</param>
    /// <param name="cancellationToken">Cancellation requested by the application lifetime.</param>
    /// <returns><see langword="true"/> only when the user explicitly approves closing QQ.</returns>
    Task<bool> ConfirmCloseQqInstallAndRestartAsync(
        string installPath,
        int targetCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms restoration of the newest compatible verified backup.
    /// </summary>
    /// <param name="installPath">QQ installation root whose backup will be restored.</param>
    /// <param name="cancellationToken">Cancellation requested by the application lifetime.</param>
    /// <returns><see langword="true"/> only when the user explicitly approves restoration.</returns>
    Task<bool> ConfirmRestoreAsync(
        string installPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms permanent deletion of the exact obsolete backup set shown in a cleanup preview.
    /// </summary>
    /// <param name="backupCount">Number of backup directories approved for deletion.</param>
    /// <param name="reclaimableBytes">Verified file bytes represented by those directories.</param>
    /// <param name="cancellationToken">Cancellation requested by the application lifetime.</param>
    /// <returns><see langword="true"/> only when the user explicitly approves permanent cleanup.</returns>
    Task<bool> ConfirmBackupCleanupAsync(
        int backupCount,
        long reclaimableBytes,
        CancellationToken cancellationToken = default);
}
