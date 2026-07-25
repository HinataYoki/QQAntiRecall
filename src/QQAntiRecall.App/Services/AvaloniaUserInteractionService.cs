using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace QQAntiRecall.App.Services;

/// <summary>
/// Provides native Avalonia folder selection and owner-bound confirmation dialogs.
/// </summary>
public sealed class AvaloniaUserInteractionService : IUserInteractionService
{
    /// <summary>
    /// Opens an application-owned directory in the platform file manager, creating it when absent.
    /// </summary>
    /// <param name="directoryPath">Absolute local directory to open.</param>
    /// <param name="cancellationToken">Cancellation checked around directory creation and launch.</param>
    public async Task OpenDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directoryPath);

        Window owner = GetMainWindow();
        bool launched = await owner.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(directoryPath));
        cancellationToken.ThrowIfCancellationRequested();
        if (!launched)
        {
            throw new InvalidOperationException("系统无法打开备份目录。");
        }
    }

    /// <summary>
    /// Opens the desktop platform's folder picker and returns a local filesystem path.
    /// </summary>
    /// <param name="currentPath">Current path used as the preferred starting directory when valid.</param>
    /// <param name="cancellationToken">Cancellation checked before and after native picker interaction.</param>
    /// <returns>The selected local directory, or <see langword="null"/> when dismissed.</returns>
    public async Task<string?> PickInstallDirectoryAsync(
        string? currentPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Window owner = GetMainWindow();
        IStorageFolder? suggestedFolder = await TryGetSuggestedFolderAsync(
            owner.StorageProvider,
            currentPath);

        IReadOnlyList<IStorageFolder> folders = await owner.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "选择 QQ 安装目录",
                AllowMultiple = false,
                SuggestedStartLocation = suggestedFolder,
            });

        cancellationToken.ThrowIfCancellationRequested();
        return folders.Count == 0 ? null : folders[0].Path.LocalPath;
    }

    /// <summary>
    /// Shows the install confirmation with the number of files that may be modified.
    /// </summary>
    /// <param name="installPath">QQ installation root displayed in the confirmation.</param>
    /// <param name="targetCount">Number of version targets included in the operation.</param>
    /// <param name="cancellationToken">Cancellation checked around modal interaction.</param>
    /// <returns><see langword="true"/> only when installation is explicitly approved.</returns>
    public Task<bool> ConfirmInstallAsync(
        string installPath,
        int targetCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string targetText = targetCount == 1 ? "1 个 QQ 版本" : $"{targetCount} 个 QQ 版本";
        return ShowConfirmationAsync(
            "安装防撤回",
            $"将先创建校验备份，再修改 {targetText} 的 wrapper.node。请确认 QQ 已完全退出。\n\n目录：{installPath}",
            "安装",
            cancellationToken);
    }

    /// <summary>
    /// Shows the explicit process-close confirmation before the automated install and restart workflow.
    /// </summary>
    /// <param name="installPath">Verified QQ installation root displayed in the confirmation.</param>
    /// <param name="targetCount">Number of version targets included in the operation.</param>
    /// <param name="cancellationToken">Cancellation checked around modal interaction.</param>
    /// <returns><see langword="true"/> only when closing QQ is explicitly approved.</returns>
    public Task<bool> ConfirmCloseQqInstallAndRestartAsync(
        string installPath,
        int targetCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string targetText = targetCount == 1 ? "1 个 QQ 版本" : $"{targetCount} 个 QQ 版本";
        return ShowConfirmationAsync(
            "关闭 QQ 并安装",
            $"将关闭正在运行的 QQ，创建校验备份并修改 {targetText} 的 wrapper.node。完成后会自动重新启动 QQ。\n\n目录：{installPath}",
            "关闭并安装",
            cancellationToken);
    }

    /// <summary>
    /// Shows the restore confirmation for the selected QQ installation.
    /// </summary>
    /// <param name="installPath">QQ installation root displayed in the confirmation.</param>
    /// <param name="cancellationToken">Cancellation checked around modal interaction.</param>
    /// <returns><see langword="true"/> only when restoration is explicitly approved.</returns>
    public Task<bool> ConfirmRestoreAsync(
        string installPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ShowConfirmationAsync(
            "恢复 QQ 文件",
            $"将验证并恢复最新的兼容备份。请确认 QQ 已完全退出。\n\n目录：{installPath}",
            "恢复",
            cancellationToken);
    }

    /// <summary>
    /// Shows the permanent cleanup confirmation with the exact directory count and verified byte size.
    /// </summary>
    /// <param name="backupCount">Number of backup directories in the approved preview.</param>
    /// <param name="reclaimableBytes">Verified file bytes represented by the preview.</param>
    /// <param name="cancellationToken">Cancellation checked around modal interaction.</param>
    /// <returns><see langword="true"/> only when cleanup is explicitly approved.</returns>
    public Task<bool> ConfirmBackupCleanupAsync(
        int backupCount,
        long reclaimableBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ShowConfirmationAsync(
            "清理旧备份",
            $"将永久删除 {backupCount} 个旧版本或重复备份，预计释放 {FormatByteSize(reclaimableBytes)}。\n\n" +
            "当前版本可恢复备份、其他 QQ 目录的备份和无法识别的目录会被保留。此操作无法撤销。",
            "清理",
            cancellationToken);
    }

    /// <summary>
    /// Resolves the classic desktop main window required to own native UI.
    /// </summary>
    /// <returns>The active desktop main window.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the desktop lifetime is not ready.</exception>
    private static Window GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is not null)
        {
            return desktop.MainWindow;
        }

        throw new InvalidOperationException("无法获取主窗口，请重新启动应用后重试。");
    }

    /// <summary>
    /// Resolves the current local path as a picker starting folder when the provider supports it.
    /// </summary>
    /// <param name="storageProvider">Platform storage provider used for path resolution.</param>
    /// <param name="currentPath">Candidate local path; invalid or unsupported paths are ignored.</param>
    /// <returns>The resolved folder, or <see langword="null"/> when no suggestion can be provided.</returns>
    private static async Task<IStorageFolder?> TryGetSuggestedFolderAsync(
        IStorageProvider storageProvider,
        string? currentPath)
    {
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            return null;
        }

        try
        {
            return await storageProvider.TryGetFolderFromPathAsync(currentPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Formats a non-negative byte count for compact confirmation text.
    /// </summary>
    /// <param name="bytes">Byte count reported by the verified cleanup preview.</param>
    /// <returns>A value expressed in B, KB, MB, GB, or TB.</returns>
    private static string FormatByteSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unitIndex = 0;
        double displayValue = value;
        while (displayValue >= 1024 && unitIndex < units.Length - 1)
        {
            displayValue /= 1024;
            unitIndex++;
        }

        return $"{displayValue:0.##} {units[unitIndex]}";
    }

    /// <summary>
    /// Displays one modal confirmation dialog and honors cancellation before and after display.
    /// </summary>
    /// <param name="title">Dialog title identifying the requested operation.</param>
    /// <param name="message">Risk and target details shown to the user.</param>
    /// <param name="confirmText">Affirmative button label.</param>
    /// <param name="cancellationToken">Cancellation checked around modal interaction.</param>
    /// <returns><see langword="true"/> when the affirmative button closes the dialog.</returns>
    private static async Task<bool> ShowConfirmationAsync(
        string title,
        string message,
        string confirmText,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConfirmationDialog dialog = new(title, message, confirmText);
        bool result = await dialog.ShowDialog<bool>(GetMainWindow());
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>
    /// Minimal owner-bound confirmation dialog used by patch and restore workflows.
    /// </summary>
    private sealed class ConfirmationDialog : Window
    {
        /// <summary>
        /// Creates a fixed-size accessible confirmation surface.
        /// </summary>
        /// <param name="title">Dialog window title.</param>
        /// <param name="message">Operation details displayed in the body.</param>
        /// <param name="confirmText">Affirmative button label.</param>
        public ConfirmationDialog(string title, string message, string confirmText)
        {
            Title = title;
            Width = 460;
            SizeToContent = SizeToContent.Height;
            MinHeight = 190;
            CanResize = false;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            Button cancelButton = new()
            {
                Content = "取消",
                MinWidth = 88,
                IsCancel = true,
            };
            cancelButton.Classes.Add("secondary");
            cancelButton.Click += CancelButtonOnClick;

            Button confirmButton = new()
            {
                Content = confirmText,
                MinWidth = 88,
                IsDefault = true,
            };
            confirmButton.Classes.Add("primary");
            confirmButton.Click += ConfirmButtonOnClick;

            Content = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto"),
                RowSpacing = 24,
                Margin = new Thickness(24),
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 412,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        [Grid.RowProperty] = 1,
                        Children =
                        {
                            cancelButton,
                            confirmButton,
                        },
                    },
                },
            };
        }

        /// <summary>
        /// Closes the modal dialog with a negative result.
        /// </summary>
        /// <param name="sender">Cancel button that raised the event.</param>
        /// <param name="eventArgs">Click event data.</param>
        private void CancelButtonOnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
            Close(false);

        /// <summary>
        /// Closes the modal dialog with an affirmative result.
        /// </summary>
        /// <param name="sender">Confirm button that raised the event.</param>
        /// <param name="eventArgs">Click event data.</param>
        private void ConfirmButtonOnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs) =>
            Close(true);
    }
}
