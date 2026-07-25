using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QQAntiRecall.App.Services;
using QQAntiRecall.Core;

namespace QQAntiRecall.App.ViewModels;

/// <summary>
/// Coordinates QQ discovery, scanning, patch operations, and managed backup UI state.
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly IAntiRecallService? _antiRecallService;
    private readonly IUserInteractionService? _userInteractionService;
    private bool _scanAllowsInstall;
    private bool _scanAllowsRestore;
    private bool _scanAllowsCloseQqInstallAndRestart;
    private string? _lastScannedPath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanupBackupsCommand))]
    private string _installPath = string.Empty;

    [ObservableProperty]
    private string _platformLabel = GetPlatformLabel();

    [ObservableProperty]
    private string _platformDetail = GetPlatformDetail();

    [ObservableProperty]
    private bool _isPlatformSupported = OperatingSystem.IsWindows();

    [ObservableProperty]
    private string _statusTitle = "等待扫描";

    [ObservableProperty]
    private string _statusDetail = "正在准备 QQ 防撤回工具。";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(CanRestore))]
    [NotifyPropertyChangedFor(nameof(CanCloseQqInstallAndRestart))]
    [NotifyCanExecuteChangedFor(nameof(BrowseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseQqInstallAndRestartCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenBackupDirectoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(CleanupBackupsCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = string.Empty;

    [ObservableProperty]
    private bool _isStatusSuccess;

    [ObservableProperty]
    private bool _isStatusWarning;

    [ObservableProperty]
    private bool _isStatusError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCloseQqInstallAndRestart))]
    [NotifyPropertyChangedFor(nameof(ShowStandardInstallAction))]
    [NotifyPropertyChangedFor(nameof(CanCloseQqInstallAndRestart))]
    [NotifyCanExecuteChangedFor(nameof(CloseQqInstallAndRestartCommand))]
    private bool _isQqRunning;

    [ObservableProperty]
    private string _qqProcessStatus = "等待检测";

    [ObservableProperty]
    private string _versionStatus = "尚未扫描";

    [ObservableProperty]
    private string _backupStatus = "尚未检查";

    /// <summary>
    /// Creates design-time state without discovering or reading a QQ installation.
    /// </summary>
    public MainViewModel()
    {
        StatusTitle = "QQ 防撤回";
        StatusDetail = IsPlatformSupported
            ? "选择 QQ 安装目录后即可检查状态。"
            : "当前平台可以运行界面，但暂不支持修改 QQ。";
        IsStatusWarning = !IsPlatformSupported;
        Initialization = Task.CompletedTask;
    }

    /// <summary>
    /// Creates the production workflow and immediately starts safe discovery and scanning.
    /// </summary>
    /// <param name="antiRecallService">Core service that owns all QQ file operations.</param>
    /// <param name="userInteractionService">UI service for directory picking and confirmations.</param>
    public MainViewModel(
        IAntiRecallService antiRecallService,
        IUserInteractionService userInteractionService)
    {
        _antiRecallService = antiRecallService ?? throw new ArgumentNullException(nameof(antiRecallService));
        _userInteractionService = userInteractionService ?? throw new ArgumentNullException(nameof(userInteractionService));
        Initialization = InitializeAsync();
    }

    /// <summary>
    /// Gets the initial discovery and scan task so hosts and tests can await startup deterministically.
    /// </summary>
    public Task Initialization { get; }

    /// <summary>
    /// Gets the version targets from the most recent successful scan.
    /// </summary>
    public ObservableCollection<TargetItemViewModel> Targets { get; } = [];

    /// <summary>
    /// Gets whether at least one version target is available for display.
    /// </summary>
    public bool HasTargets => Targets.Count > 0;

    /// <summary>
    /// Gets whether the current scan has no version targets.
    /// </summary>
    public bool HasNoTargets => !HasTargets;

    /// <summary>
    /// Gets whether the complete patch set can be installed now.
    /// </summary>
    public bool CanInstall => _scanAllowsInstall && !IsBusy;

    /// <summary>
    /// Gets whether the latest compatible backup can be restored now.
    /// </summary>
    public bool CanRestore => _scanAllowsRestore && !IsBusy;

    /// <summary>
    /// Gets whether the running-QQ action should replace the standard install action in the command bar.
    /// </summary>
    public bool ShowCloseQqInstallAndRestart => IsQqRunning;

    /// <summary>
    /// Gets whether the standard install action should be shown while QQ is not running.
    /// </summary>
    public bool ShowStandardInstallAction => !IsQqRunning;

    /// <summary>
    /// Gets whether the latest scan permits closing QQ and running the automated install-and-restart workflow.
    /// </summary>
    public bool CanCloseQqInstallAndRestart => _scanAllowsCloseQqInstallAndRestart && !IsBusy;

    /// <summary>
    /// Clears stale scan results when the user changes the installation path.
    /// </summary>
    /// <param name="value">New path entered or selected by the user.</param>
    partial void OnInstallPathChanged(string value)
    {
        if (_lastScannedPath is null || PathsEqual(_lastScannedPath, value))
        {
            return;
        }

        _lastScannedPath = null;
        ReplaceTargets([]);
        IsQqRunning = false;
        QqProcessStatus = "等待检测";
        VersionStatus = "尚未扫描";
        BackupStatus = "尚未检查";
        SetActionAvailability(
            canInstall: false,
            canRestore: false,
            canCloseQqInstallAndRestart: false);
        SetStatus(
            "目录已更改",
            "请重新扫描所选 QQ 安装目录。",
            StatusSeverity.Warning);
    }

    /// <summary>
    /// Opens the native folder picker and scans a newly selected directory.
    /// </summary>
    /// <returns>A task that completes after selection is dismissed or the selected path is scanned.</returns>
    [RelayCommand(CanExecute = nameof(CanBrowseCommand))]
    private async Task BrowseAsync()
    {
        try
        {
            if (_userInteractionService is null)
            {
                SetMissingRuntimeServicesStatus();
                return;
            }

            string? selectedPath = await _userInteractionService.PickInstallDirectoryAsync(InstallPath);
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            InstallPath = selectedPath;
            await ScanCoreAsync("正在扫描所选 QQ 目录...");
        }
        catch (Exception exception)
        {
            HandleCommandException("选择目录失败", exception);
        }
    }

    /// <summary>
    /// Determines whether directory selection is currently available.
    /// </summary>
    /// <returns><see langword="true"/> when runtime interaction services are ready and no operation is active.</returns>
    private bool CanBrowseCommand() => _userInteractionService is not null && !IsBusy;

    /// <summary>
    /// Opens the application-owned backup directory without changing patch or scan state.
    /// </summary>
    /// <returns>A task that completes after the platform file manager accepts the directory.</returns>
    [RelayCommand(CanExecute = nameof(CanOpenBackupDirectoryCommand))]
    private async Task OpenBackupDirectoryAsync()
    {
        try
        {
            if (_antiRecallService is null || _userInteractionService is null)
            {
                SetMissingRuntimeServicesStatus();
                return;
            }

            await _userInteractionService.OpenDirectoryAsync(_antiRecallService.BackupDirectoryPath);
        }
        catch (Exception exception)
        {
            HandleCommandException("打开备份目录失败", exception);
        }
    }

    /// <summary>
    /// Determines whether the platform directory launcher is ready and no workflow is active.
    /// </summary>
    /// <returns><see langword="true"/> when the backup directory can be opened.</returns>
    private bool CanOpenBackupDirectoryCommand() =>
        _antiRecallService is not null &&
        _userInteractionService is not null &&
        !IsBusy;

    /// <summary>
    /// Previews obsolete backups, requests exact confirmation, and deletes only the approved identifiers.
    /// </summary>
    /// <returns>A task that completes after preview, optional confirmation, and cleanup reporting.</returns>
    [RelayCommand(CanExecute = nameof(CanCleanupBackupsCommand))]
    private async Task CleanupBackupsAsync()
    {
        try
        {
            if (_antiRecallService is null || _userInteractionService is null)
            {
                SetMissingRuntimeServicesStatus();
                return;
            }

            SetBusy(true, "正在检查本地备份...");
            BackupCleanupPreview preview = await _antiRecallService.PreviewBackupCleanupAsync(InstallPath);
            if (preview.BackupCount == 0)
            {
                SetStatus(
                    "无需清理备份",
                    "没有发现旧版本或完全重复的备份。当前版本及其他安装目录的备份均已保留。",
                    StatusSeverity.Success);
                return;
            }

            bool confirmed = await _userInteractionService.ConfirmBackupCleanupAsync(
                preview.BackupCount,
                preview.ReclaimableBytes);
            if (!confirmed)
            {
                return;
            }

            SetBusy(true, "正在重新验证并清理旧备份...");
            BackupCleanupResult result = await _antiRecallService.CleanupObsoleteBackupsAsync(
                InstallPath,
                preview.BackupIds);
            SetStatus(
                result.Succeeded ? "备份清理完成" : "部分备份未清理",
                result.Message,
                result.Succeeded ? StatusSeverity.Success : StatusSeverity.Warning);
        }
        catch (Exception exception)
        {
            HandleCommandException("清理备份失败", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Determines whether a selected QQ directory can be used to classify obsolete backups.
    /// </summary>
    /// <returns><see langword="true"/> when runtime services, an install path, and an idle workflow are available.</returns>
    private bool CanCleanupBackupsCommand() =>
        _antiRecallService is not null &&
        _userInteractionService is not null &&
        !IsBusy &&
        !string.IsNullOrWhiteSpace(InstallPath);

    /// <summary>
    /// Scans the current path and presents a complete, actionable status.
    /// </summary>
    /// <returns>A task that completes after scan state or an actionable error is presented.</returns>
    [RelayCommand(CanExecute = nameof(CanScanCommand))]
    private async Task ScanAsync()
    {
        try
        {
            await ScanCoreAsync("正在检查 QQ 版本和补丁状态...");
        }
        catch (Exception exception)
        {
            HandleCommandException("扫描失败", exception);
        }
    }

    /// <summary>
    /// Determines whether the current path can be scanned.
    /// </summary>
    /// <returns><see langword="true"/> when a core service, non-empty path, and idle workflow are available.</returns>
    private bool CanScanCommand() =>
        _antiRecallService is not null &&
        !IsBusy &&
        !string.IsNullOrWhiteSpace(InstallPath);

    /// <summary>
    /// Confirms and installs the complete patch set through the transactional core service.
    /// </summary>
    /// <returns>A task that completes after confirmation and the optional install operation.</returns>
    [RelayCommand(CanExecute = nameof(CanInstallCommand))]
    private async Task InstallAsync()
    {
        try
        {
            if (_antiRecallService is null || _userInteractionService is null)
            {
                SetMissingRuntimeServicesStatus();
                return;
            }

            bool confirmed = await _userInteractionService.ConfirmInstallAsync(InstallPath, Targets.Count);
            if (!confirmed)
            {
                return;
            }

            SetBusy(true, "正在备份并安装防撤回补丁...");
            PatchOperationResult result = await _antiRecallService.InstallAsync(InstallPath);
            ApplyScanResult(result.Scan);
            if (result.Succeeded)
            {
                SetStatus("安装完成", result.Message, StatusSeverity.Success);
            }
            else
            {
                SetStatus("安装未完成", result.Message, StatusSeverity.Warning);
            }
        }
        catch (Exception exception)
        {
            HandleCommandException("安装失败", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Determines whether installation is allowed by the latest scan and current activity state.
    /// </summary>
    /// <returns>The current bindable installation availability.</returns>
    private bool CanInstallCommand() => CanInstall;

    /// <summary>
    /// Confirms process termination, then closes QQ, installs the patch, and restores the QQ session.
    /// </summary>
    /// <returns>A task that completes after confirmation and the automated restart workflow.</returns>
    [RelayCommand(CanExecute = nameof(CanCloseQqInstallAndRestartCommand))]
    private async Task CloseQqInstallAndRestartAsync()
    {
        try
        {
            if (_antiRecallService is null || _userInteractionService is null)
            {
                SetMissingRuntimeServicesStatus();
                return;
            }

            bool confirmed = await _userInteractionService.ConfirmCloseQqInstallAndRestartAsync(
                InstallPath,
                Targets.Count);
            if (!confirmed)
            {
                return;
            }

            SetBusy(true, "正在关闭 QQ、安装补丁并重新启动...");
            PatchOperationResult result = await _antiRecallService.CloseQqInstallAndRestartAsync(InstallPath);
            ApplyScanResult(result.Scan);
            SetStatus(
                result.Succeeded ? "安装完成，QQ 已重启" : "自动安装未完成",
                result.Message,
                result.Succeeded ? StatusSeverity.Success : StatusSeverity.Warning);
        }
        catch (Exception exception)
        {
            HandleCommandException("自动安装失败", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Determines whether the latest verified scan permits the automated running-QQ workflow.
    /// </summary>
    /// <returns>The current bindable process-close installation availability.</returns>
    private bool CanCloseQqInstallAndRestartCommand() => CanCloseQqInstallAndRestart;

    /// <summary>
    /// Confirms and restores the newest compatible verified backup.
    /// </summary>
    /// <returns>A task that completes after confirmation and the optional restore operation.</returns>
    [RelayCommand(CanExecute = nameof(CanRestoreCommand))]
    private async Task RestoreAsync()
    {
        try
        {
            if (_antiRecallService is null || _userInteractionService is null)
            {
                SetMissingRuntimeServicesStatus();
                return;
            }

            bool confirmed = await _userInteractionService.ConfirmRestoreAsync(InstallPath);
            if (!confirmed)
            {
                return;
            }

            SetBusy(true, "正在验证备份并恢复 QQ 文件...");
            PatchOperationResult result = await _antiRecallService.RestoreAsync(InstallPath);
            ApplyScanResult(result.Scan);
            if (result.Succeeded)
            {
                SetStatus("恢复完成", result.Message, StatusSeverity.Success);
            }
            else
            {
                StatusDetail = result.Message;
            }
        }
        catch (Exception exception)
        {
            HandleCommandException("恢复失败", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Determines whether restore is allowed by the latest scan and current activity state.
    /// </summary>
    /// <returns>The current bindable restore availability.</returns>
    private bool CanRestoreCommand() => CanRestore;

    /// <summary>
    /// Discovers the QQ root and performs the first scan without allowing exceptions to escape startup.
    /// </summary>
    /// <returns>A task exposed through <see cref="Initialization"/> for deterministic host and test startup.</returns>
    private async Task InitializeAsync()
    {
        try
        {
            SetBusy(true, "正在查找 QQ 安装目录...");
            string? discoveredPath = _antiRecallService!.FindInstallRoot();
            if (string.IsNullOrWhiteSpace(discoveredPath))
            {
                if (OperatingSystem.IsWindows())
                {
                    SetStatus(
                        "未找到 QQ 安装目录",
                        "请点击浏览，选择包含 versions 目录的 QQ 安装目录。",
                        StatusSeverity.Warning);
                }
                else
                {
                    SetStatus(
                        "当前平台暂不支持修改",
                        "应用可在此平台运行，但当前补丁签名仅验证了 Windows QQ NT。",
                        StatusSeverity.Warning);
                }

                return;
            }

            InstallPath = discoveredPath;
            await ScanCoreAsync("正在检查 QQ 版本和补丁状态...");
        }
        catch (Exception exception)
        {
            HandleCommandException("自动检测失败", exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Runs one scan and applies its state; callers are responsible for exception presentation.
    /// </summary>
    /// <param name="busyText">Progress text displayed while the scan is active.</param>
    /// <returns>A task that completes after the core scan result is mapped to bindable state.</returns>
    private async Task ScanCoreAsync(string busyText)
    {
        if (_antiRecallService is null)
        {
            SetMissingRuntimeServicesStatus();
            return;
        }

        if (string.IsNullOrWhiteSpace(InstallPath))
        {
            SetStatus(
                "请选择 QQ 安装目录",
                "目标目录应包含 QQ 的 versions 目录。",
                StatusSeverity.Warning);
            return;
        }

        SetBusy(true, busyText);
        try
        {
            AntiRecallScanResult result = await _antiRecallService.ScanAsync(InstallPath);
            ApplyScanResult(result);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Maps a core scan result into bindable target, action, platform, and aggregate status state.
    /// </summary>
    /// <param name="result">Verified snapshot returned by the core service.</param>
    private void ApplyScanResult(AntiRecallScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!string.IsNullOrWhiteSpace(result.InstallRoot))
        {
            InstallPath = result.InstallRoot;
        }

        _lastScannedPath = InstallPath;
        IsPlatformSupported = result.IsPlatformSupported;
        PlatformDetail = result.IsPlatformSupported
            ? "支持 Windows QQ NT，群聊和私聊撤回使用同一组完整补丁。"
            : "当前补丁签名仅验证了 Windows QQ NT，此平台不会修改文件。";

        ReplaceTargets(result.Targets.Select(target => new TargetItemViewModel(target)));
        IsQqRunning = result.IsQqRunning;
        QqProcessStatus = result.IsQqRunning ? "正在运行" : "未运行";
        VersionStatus = result.Targets.Count switch
        {
            0 => "未发现版本",
            1 => "已发现 1 个版本",
            _ => $"已发现 {result.Targets.Count} 个版本",
        };
        BackupStatus = string.IsNullOrWhiteSpace(result.LatestBackupId) ? "尚无可用备份" : "备份可用";
        bool canCloseQqInstallAndRestart = result.IsPlatformSupported
            && result.IsQqRunning
            && result.Targets.Count > 0
            && result.Targets.All(target => target.State == TargetPatchState.ReadyToInstall);
        SetActionAvailability(result.CanInstall, result.CanRestore, canCloseQqInstallAndRestart);

        if (!result.IsPlatformSupported)
        {
            SetStatus("当前平台暂不支持修改", result.Summary, StatusSeverity.Warning);
            return;
        }

        if (result.IsQqRunning)
        {
            SetStatus(
                "请先完全退出 QQ",
                string.IsNullOrWhiteSpace(result.Summary)
                    ? "检测到 QQ 正在运行。退出托盘中的 QQ 后重新扫描。"
                    : result.Summary,
                StatusSeverity.Warning);
            return;
        }

        if (result.Targets.Count == 0)
        {
            SetStatus("未找到可处理的 QQ 版本", result.Summary, StatusSeverity.Warning);
            return;
        }

        if (result.Targets.Any(target => target.State is TargetPatchState.Inconsistent or TargetPatchState.Missing))
        {
            SetStatus("检测到无法安全处理的版本", result.Summary, StatusSeverity.Error);
            return;
        }

        if (result.Targets.Any(target => target.State == TargetPatchState.Unsupported))
        {
            SetStatus("当前 QQ 版本暂不支持", result.Summary, StatusSeverity.Warning);
            return;
        }

        bool allInstalled = result.Targets.All(target => target.State == TargetPatchState.Installed);
        if (allInstalled)
        {
            SetStatus("防撤回已启用", result.Summary, StatusSeverity.Success);
            return;
        }

        bool anyInstalled = result.Targets.Any(target => target.State == TargetPatchState.Installed);
        SetStatus(
            anyInstalled ? "部分 QQ 版本尚未安装" : "已找到可安装版本",
            result.Summary,
            StatusSeverity.Warning);
    }

    /// <summary>
    /// Replaces target rows and raises the two complementary collection-state properties once.
    /// </summary>
    /// <param name="targets">New target rows in display order.</param>
    private void ReplaceTargets(IEnumerable<TargetItemViewModel> targets)
    {
        Targets.Clear();
        foreach (TargetItemViewModel target in targets)
        {
            Targets.Add(target);
        }

        OnPropertyChanged(nameof(HasTargets));
        OnPropertyChanged(nameof(HasNoTargets));
    }

    /// <summary>
    /// Updates scan-derived action flags and all corresponding command states.
    /// </summary>
    /// <param name="canInstall">Whether the latest scan permits a complete installation.</param>
    /// <param name="canRestore">Whether the latest scan found a compatible backup to restore.</param>
    private void SetActionAvailability(
        bool canInstall,
        bool canRestore,
        bool canCloseQqInstallAndRestart)
    {
        _scanAllowsInstall = canInstall;
        _scanAllowsRestore = canRestore;
        _scanAllowsCloseQqInstallAndRestart = canCloseQqInstallAndRestart;
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanRestore));
        OnPropertyChanged(nameof(CanCloseQqInstallAndRestart));
        InstallCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
        CloseQqInstallAndRestartCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Updates activity state while preserving the latest completed operation text.
    /// </summary>
    /// <param name="isBusy">Whether a workflow operation is active.</param>
    /// <param name="busyText">Optional progress text for an active operation.</param>
    private void SetBusy(bool isBusy, string? busyText = null)
    {
        IsBusy = isBusy;
        BusyText = isBusy ? busyText ?? "正在处理..." : string.Empty;
    }

    /// <summary>
    /// Applies mutually exclusive visual status flags and user-facing text.
    /// </summary>
    /// <param name="title">Concise workflow result displayed as the status heading.</param>
    /// <param name="detail">Actionable explanation displayed below the heading.</param>
    /// <param name="severity">Visual severity used to select exactly one status class.</param>
    private void SetStatus(string title, string detail, StatusSeverity severity)
    {
        StatusTitle = title;
        StatusDetail = string.IsNullOrWhiteSpace(detail) ? "没有更多详细信息。" : detail;
        IsStatusSuccess = severity == StatusSeverity.Success;
        IsStatusWarning = severity == StatusSeverity.Warning;
        IsStatusError = severity == StatusSeverity.Error;
    }

    /// <summary>
    /// Converts a command exception into a Chinese status with a concrete recovery action.
    /// </summary>
    /// <param name="operationTitle">Operation-specific failure heading.</param>
    /// <param name="exception">Failure mapped to a safe user-facing recovery message.</param>
    private void HandleCommandException(string operationTitle, Exception exception)
    {
        SetActionAvailability(
            canInstall: false,
            canRestore: false,
            canCloseQqInstallAndRestart: false);

        string detail = exception switch
        {
            UnauthorizedAccessException => "没有权限修改 QQ 文件。请以管理员身份运行后重试。",
            DirectoryNotFoundException => "QQ 安装目录不存在或已被移动，请重新选择目录。",
            FileNotFoundException => "未找到 QQ 的关键文件，可能刚完成更新。请重新扫描或选择正确目录。",
            OperationCanceledException => "操作已取消，未修改 QQ 文件。",
            _ => $"{exception.Message}\n请确认 QQ 已完全退出、目录正确，然后重试。",
        };

        SetStatus(operationTitle, detail, StatusSeverity.Error);
        SetBusy(false);
    }

    /// <summary>
    /// Presents a deterministic error when a design-time instance is accidentally used at runtime.
    /// </summary>
    private void SetMissingRuntimeServicesStatus()
    {
        SetStatus(
            "应用尚未完成初始化",
            "缺少运行所需的服务，请重新启动应用。",
            StatusSeverity.Error);
    }

    /// <summary>
    /// Compares user-entered paths using the current platform's path case rules.
    /// </summary>
    /// <param name="left">Previously scanned path.</param>
    /// <param name="right">Current user-entered or selected path.</param>
    /// <returns><see langword="true"/> when both normalized paths identify the same platform path.</returns>
    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Returns a concise display name for the current desktop platform.
    /// </summary>
    /// <returns>The Windows, macOS, Linux, or fallback platform label.</returns>
    private static string GetPlatformLabel()
    {
        if (OperatingSystem.IsWindows())
        {
            return "Windows";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }

        if (OperatingSystem.IsLinux())
        {
            return "Linux";
        }

        return "未知平台";
    }

    /// <summary>
    /// Returns the initial support explanation for the current desktop platform.
    /// </summary>
    /// <returns>A platform-specific explanation shown before the first scan completes.</returns>
    private static string GetPlatformDetail() => OperatingSystem.IsWindows()
        ? "支持 Windows QQ NT，扫描过程不会修改文件。"
        : "界面可在当前平台运行，但 QQ 文件修改功能暂未开放。";

    private enum StatusSeverity
    {
        Success,
        Warning,
        Error,
    }
}
