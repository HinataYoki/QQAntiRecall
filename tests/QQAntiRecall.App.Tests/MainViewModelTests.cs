using QQAntiRecall.App.Services;
using QQAntiRecall.App.ViewModels;
using QQAntiRecall.Core;

namespace QQAntiRecall.App.Tests;

public sealed class MainViewModelTests
{
    /// <summary>
    /// Verifies that production startup discovers, scans, and maps actionable targets.
    /// </summary>
    [Fact]
    public async Task Initialization_DiscoversAndScansInstallRoot()
    {
        FakeAntiRecallService core = new()
        {
            DiscoveredPath = @"D:\Tencent\QQNT",
            ScanResult = CreateScan(canInstall: true),
        };
        MainViewModel viewModel = new(core, new FakeUserInteractionService());

        await viewModel.Initialization;

        Assert.Equal(@"D:\Tencent\QQNT", viewModel.InstallPath);
        Assert.Single(viewModel.Targets);
        Assert.True(viewModel.HasTargets);
        Assert.False(viewModel.HasNoTargets);
        Assert.True(viewModel.CanInstall);
        Assert.True(viewModel.IsStatusWarning);
        Assert.Equal(1, core.ScanCallCount);
    }

    /// <summary>
    /// Verifies a recognized 0.0.1 target is presented as an actionable upgrade instead of an error.
    /// </summary>
    [Fact]
    public async Task Initialization_LegacyPatchIsPresentedAsUpgrade()
    {
        FakeAntiRecallService core = new()
        {
            DiscoveredPath = @"D:\Tencent\QQNT",
            ScanResult = CreateScan(
                canInstall: true,
                canRestore: true,
                state: TargetPatchState.LegacyInstalled),
        };
        MainViewModel viewModel = new(core, new FakeUserInteractionService());

        await viewModel.Initialization;

        var target = Assert.Single(viewModel.Targets);
        Assert.Equal("可升级", target.StatusLabel);
        Assert.True(target.IsReady);
        Assert.True(viewModel.CanInstall);
        Assert.True(viewModel.CanRestore);
        Assert.Equal("检测到可升级的旧版补丁", viewModel.StatusTitle);
        Assert.True(viewModel.IsStatusWarning);
    }

    /// <summary>
    /// Verifies that declining installation leaves the core service untouched.
    /// </summary>
    [Fact]
    public async Task InstallCommand_WhenConfirmationDeclined_DoesNotInstall()
    {
        FakeAntiRecallService core = new()
        {
            DiscoveredPath = @"D:\Tencent\QQNT",
            ScanResult = CreateScan(canInstall: true),
        };
        FakeUserInteractionService interaction = new() { ConfirmInstallResult = false };
        MainViewModel viewModel = new(core, interaction);
        await viewModel.Initialization;

        await viewModel.InstallCommand.ExecuteAsync(null);

        Assert.Equal(1, interaction.InstallConfirmationCount);
        Assert.Equal(0, core.InstallCallCount);
        Assert.True(viewModel.CanInstall);
    }

    /// <summary>
    /// Verifies a core safety refusal is not presented as a successful installation.
    /// </summary>
    [Fact]
    public async Task InstallCommand_WhenCoreRefuses_PreservesWarningStatus()
    {
        FakeAntiRecallService core = new()
        {
            DiscoveredPath = @"D:\Tencent\QQNT",
            ScanResult = CreateScan(canInstall: true),
            InstallSucceeded = false,
            InstallMessage = "安装已拒绝：预检后目标状态发生变化。",
        };
        MainViewModel viewModel = new(core, new FakeUserInteractionService());
        await viewModel.Initialization;

        await viewModel.InstallCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsStatusSuccess);
        Assert.True(viewModel.IsStatusWarning);
        Assert.Contains("安装已拒绝", viewModel.StatusDetail);
    }

    /// <summary>
    /// Verifies a running QQ exposes the dedicated action and completes the confirmed close-install-restart workflow.
    /// </summary>
    [Fact]
    public async Task CloseQqInstallAndRestartCommand_WhenQqIsRunning_UsesDedicatedWorkflow()
    {
        AntiRecallScanResult runningScan = CreateScan(isQqRunning: true);
        AntiRecallScanResult restartedScan = CreateScan(
            state: TargetPatchState.Installed,
            isQqRunning: true);
        FakeAntiRecallService core = new()
        {
            DiscoveredPath = @"D:\Tencent\QQNT",
            ScanResult = runningScan,
            CloseQqInstallAndRestartResult = new PatchOperationResult(
                restartedScan,
                "防撤回已安装，QQ 已重新启动。",
                Succeeded: true),
        };
        FakeUserInteractionService interaction = new();
        MainViewModel viewModel = new(core, interaction);
        await viewModel.Initialization;

        Assert.True(viewModel.IsQqRunning);
        Assert.True(viewModel.ShowCloseQqInstallAndRestart);
        Assert.False(viewModel.ShowStandardInstallAction);
        Assert.True(viewModel.CanCloseQqInstallAndRestart);
        Assert.False(viewModel.CanInstall);

        await viewModel.CloseQqInstallAndRestartCommand.ExecuteAsync(null);

        Assert.Equal(1, interaction.CloseQqInstallConfirmationCount);
        Assert.Equal(1, core.CloseQqInstallAndRestartCallCount);
        Assert.True(viewModel.IsStatusSuccess);
        Assert.Equal("安装完成，QQ 已重启", viewModel.StatusTitle);
        Assert.Equal("备份可用", viewModel.BackupStatus);
    }

    /// <summary>
    /// Verifies that declining restore leaves the verified backup untouched.
    /// </summary>
    [Fact]
    public async Task RestoreCommand_WhenConfirmationDeclined_DoesNotRestore()
    {
        FakeAntiRecallService core = new()
        {
            DiscoveredPath = @"D:\Tencent\QQNT",
            ScanResult = CreateScan(canRestore: true, state: TargetPatchState.Installed),
        };
        FakeUserInteractionService interaction = new() { ConfirmRestoreResult = false };
        MainViewModel viewModel = new(core, interaction);
        await viewModel.Initialization;

        await viewModel.RestoreCommand.ExecuteAsync(null);

        Assert.Equal(1, interaction.RestoreConfirmationCount);
        Assert.Equal(0, core.RestoreCallCount);
        Assert.True(viewModel.CanRestore);
    }

    /// <summary>
    /// Verifies that scan exceptions become actionable Chinese error state and never escape the command.
    /// </summary>
    [Fact]
    public async Task ScanError_IsCapturedAsActionableStatus()
    {
        FakeAntiRecallService core = new()
        {
            DiscoveredPath = @"D:\Tencent\QQNT",
            ScanException = new UnauthorizedAccessException("denied"),
        };
        MainViewModel viewModel = new(core, new FakeUserInteractionService());

        await viewModel.Initialization;

        Assert.True(viewModel.IsStatusError);
        Assert.Equal("自动检测失败", viewModel.StatusTitle);
        Assert.Contains("管理员", viewModel.StatusDetail);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.CanInstall);
        Assert.False(viewModel.CanRestore);
    }

    /// <summary>
    /// Verifies command availability and public action flags while a scan is in flight.
    /// </summary>
    [Fact]
    public async Task ScanCommand_WhileBusy_DisablesAllMutatingCommands()
    {
        TaskCompletionSource<AntiRecallScanResult> pendingScan =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeAntiRecallService core = new() { PendingScan = pendingScan.Task };
        MainViewModel viewModel = new(core, new FakeUserInteractionService())
        {
            InstallPath = @"D:\Tencent\QQNT",
        };
        await viewModel.Initialization;

        Task commandTask = viewModel.ScanCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.BrowseCommand.CanExecute(null));
        Assert.False(viewModel.ScanCommand.CanExecute(null));
        Assert.False(viewModel.InstallCommand.CanExecute(null));
        Assert.False(viewModel.RestoreCommand.CanExecute(null));

        pendingScan.SetResult(CreateScan(canInstall: true));
        await commandTask;

        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.ScanCommand.CanExecute(null));
        Assert.True(viewModel.InstallCommand.CanExecute(null));
    }

    /// <summary>
    /// Verifies that selecting a directory updates the path and immediately scans it.
    /// </summary>
    [Fact]
    public async Task BrowseCommand_WhenDirectorySelected_ScansSelection()
    {
        FakeAntiRecallService core = new()
        {
            ScanResult = CreateScan(canInstall: true, installRoot: @"E:\Apps\QQ"),
        };
        FakeUserInteractionService interaction = new() { SelectedPath = @"E:\Apps\QQ" };
        MainViewModel viewModel = new(core, interaction);
        await viewModel.Initialization;

        await viewModel.BrowseCommand.ExecuteAsync(null);

        Assert.Equal(@"E:\Apps\QQ", viewModel.InstallPath);
        Assert.Equal(1, core.ScanCallCount);
        Assert.True(viewModel.CanInstall);
    }

    /// <summary>
    /// Verifies the backup directory command delegates the exact core-owned path to the platform launcher.
    /// </summary>
    [Fact]
    public async Task OpenBackupDirectoryCommand_OpensCoreBackupPath()
    {
        FakeAntiRecallService core = new() { BackupDirectoryPath = @"C:\Data\QQAntiRecall\backups" };
        FakeUserInteractionService interaction = new();
        MainViewModel viewModel = new(core, interaction);
        await viewModel.Initialization;

        await viewModel.OpenBackupDirectoryCommand.ExecuteAsync(null);

        Assert.Equal(core.BackupDirectoryPath, interaction.OpenedDirectoryPath);
    }

    /// <summary>
    /// Verifies cleanup previews exact identifiers, confirms their count and size, and passes only those identifiers for deletion.
    /// </summary>
    [Fact]
    public async Task CleanupBackupsCommand_WhenConfirmed_DeletesPreviewedIdentifiers()
    {
        string[] backupIds = ["old-backup", "duplicate-backup"];
        FakeAntiRecallService core = new()
        {
            DiscoveredPath = @"D:\Tencent\QQNT",
            ScanResult = CreateScan(),
            CleanupPreview = new BackupCleanupPreview(backupIds, 4096, 1, 0),
            CleanupResult = new BackupCleanupResult(
                2,
                4096,
                0,
                "已清理 2 个旧备份。当前版本可用备份已保留。",
                Succeeded: true),
        };
        FakeUserInteractionService interaction = new();
        MainViewModel viewModel = new(core, interaction);
        await viewModel.Initialization;

        await viewModel.CleanupBackupsCommand.ExecuteAsync(null);

        Assert.Equal(1, core.CleanupPreviewCallCount);
        Assert.Equal(1, interaction.BackupCleanupConfirmationCount);
        Assert.Equal(2, interaction.LastBackupCleanupCount);
        Assert.Equal(4096, interaction.LastBackupCleanupBytes);
        Assert.Equal(backupIds, core.ApprovedBackupIds);
        Assert.Equal("备份清理完成", viewModel.StatusTitle);
        Assert.False(viewModel.IsBusy);
    }

    /// <summary>
    /// Verifies declining the exact cleanup preview never invokes the destructive core operation.
    /// </summary>
    [Fact]
    public async Task CleanupBackupsCommand_WhenConfirmationDeclined_DoesNotDelete()
    {
        FakeAntiRecallService core = new()
        {
            DiscoveredPath = @"D:\Tencent\QQNT",
            ScanResult = CreateScan(),
            CleanupPreview = new BackupCleanupPreview(["old-backup"], 1024, 1, 0),
        };
        FakeUserInteractionService interaction = new() { ConfirmBackupCleanupResult = false };
        MainViewModel viewModel = new(core, interaction);
        await viewModel.Initialization;

        await viewModel.CleanupBackupsCommand.ExecuteAsync(null);

        Assert.Equal(0, core.CleanupCallCount);
        Assert.False(viewModel.IsBusy);
    }

    /// <summary>
    /// Creates a deterministic core scan snapshot without accessing real QQ files.
    /// </summary>
    private static AntiRecallScanResult CreateScan(
        bool canInstall = false,
        bool canRestore = false,
        TargetPatchState state = TargetPatchState.ReadyToInstall,
        string installRoot = @"D:\Tencent\QQNT",
        bool isQqRunning = false)
    {
        TargetScanResult target = new(
            "9.9.33-51552",
            Path.Combine(installRoot, "versions", "9.9.33-51552", "wrapper.node"),
            state,
            "ABCDEF",
            [new PatchSignatureStatus("普通撤回", 1, 0)],
            "签名验证完成");

        return new AntiRecallScanResult(
            installRoot,
            IsPlatformSupported: true,
            IsQqRunning: isQqRunning,
            [target],
            state is TargetPatchState.Installed or TargetPatchState.LegacyInstalled ? "backup-1" : null,
            canInstall && !isQqRunning,
            canRestore && !isQqRunning,
            state switch
            {
                TargetPatchState.Installed => "所有目标均已安装。",
                TargetPatchState.LegacyInstalled => "检测到可升级的旧版补丁。",
                _ => "目标可以安全安装。",
            });
    }

    /// <summary>
    /// In-memory core fake used to isolate ViewModel behavior from files and processes.
    /// </summary>
    private sealed class FakeAntiRecallService : IAntiRecallService
    {
        public string BackupDirectoryPath { get; init; } = @"C:\Data\QQAntiRecall\backups";

        public string? DiscoveredPath { get; init; }

        public AntiRecallScanResult ScanResult { get; init; } = new(
            string.Empty,
            IsPlatformSupported: true,
            IsQqRunning: false,
            [],
            LatestBackupId: null,
            CanInstall: false,
            CanRestore: false,
            "未找到目标。");

        public Exception? ScanException { get; init; }

        public Task<AntiRecallScanResult>? PendingScan { get; init; }

        public int ScanCallCount { get; private set; }

        public int InstallCallCount { get; private set; }

        public int RestoreCallCount { get; private set; }

        public int CloseQqInstallAndRestartCallCount { get; private set; }

        public int CleanupPreviewCallCount { get; private set; }

        public int CleanupCallCount { get; private set; }

        public BackupCleanupPreview CleanupPreview { get; init; } = new([], 0, 0, 0);

        public BackupCleanupResult CleanupResult { get; init; } = new(
            0,
            0,
            0,
            "没有旧备份。",
            Succeeded: true);

        public IReadOnlyCollection<string> ApprovedBackupIds { get; private set; } = [];

        public bool InstallSucceeded { get; init; } = true;

        public string InstallMessage { get; init; } = "安装成功。";

        public PatchOperationResult? CloseQqInstallAndRestartResult { get; init; }

        /// <summary>
        /// Returns the configured discovery result without touching the filesystem.
        /// </summary>
        public string? FindInstallRoot() => DiscoveredPath;

        /// <summary>
        /// Returns, delays, or throws the configured scan behavior.
        /// </summary>
        public async Task<AntiRecallScanResult> ScanAsync(
            string installRoot,
            CancellationToken cancellationToken = default)
        {
            ScanCallCount++;
            if (ScanException is not null)
            {
                throw ScanException;
            }

            if (PendingScan is not null)
            {
                return await PendingScan.WaitAsync(cancellationToken);
            }

            return ScanResult;
        }

        /// <summary>
        /// Records installation and returns the configured refreshed scan.
        /// </summary>
        public Task<PatchOperationResult> InstallAsync(
            string installRoot,
            CancellationToken cancellationToken = default)
        {
            InstallCallCount++;
            return Task.FromResult(new PatchOperationResult(ScanResult, InstallMessage, InstallSucceeded));
        }

        /// <summary>
        /// Records the automated running-QQ workflow and returns its configured result.
        /// </summary>
        public Task<PatchOperationResult> CloseQqInstallAndRestartAsync(
            string installRoot,
            CancellationToken cancellationToken = default)
        {
            CloseQqInstallAndRestartCallCount++;
            return Task.FromResult(CloseQqInstallAndRestartResult
                ?? new PatchOperationResult(ScanResult, "QQ 已重新启动。", Succeeded: true));
        }

        /// <summary>
        /// Records restore and returns the configured refreshed scan.
        /// </summary>
        public Task<PatchOperationResult> RestoreAsync(
            string installRoot,
            CancellationToken cancellationToken = default)
        {
            RestoreCallCount++;
            return Task.FromResult(new PatchOperationResult(ScanResult, "恢复成功。", Succeeded: true));
        }

        /// <summary>
        /// Returns the configured non-destructive cleanup preview and records the request.
        /// </summary>
        public Task<BackupCleanupPreview> PreviewBackupCleanupAsync(
            string installRoot,
            CancellationToken cancellationToken = default)
        {
            CleanupPreviewCallCount++;
            return Task.FromResult(CleanupPreview);
        }

        /// <summary>
        /// Captures approved identifiers and returns the configured cleanup result.
        /// </summary>
        public Task<BackupCleanupResult> CleanupObsoleteBackupsAsync(
            string installRoot,
            IReadOnlyCollection<string> approvedBackupIds,
            CancellationToken cancellationToken = default)
        {
            CleanupCallCount++;
            ApprovedBackupIds = approvedBackupIds.ToArray();
            return Task.FromResult(CleanupResult);
        }
    }

    /// <summary>
    /// In-memory UI fake used to drive selection and confirmation outcomes.
    /// </summary>
    private sealed class FakeUserInteractionService : IUserInteractionService
    {
        public string? SelectedPath { get; init; }

        public bool ConfirmInstallResult { get; init; } = true;

        public bool ConfirmRestoreResult { get; init; } = true;

        public bool ConfirmCloseQqInstallResult { get; init; } = true;

        public bool ConfirmBackupCleanupResult { get; init; } = true;

        public int InstallConfirmationCount { get; private set; }

        public int RestoreConfirmationCount { get; private set; }

        public int CloseQqInstallConfirmationCount { get; private set; }

        public int BackupCleanupConfirmationCount { get; private set; }

        public string? OpenedDirectoryPath { get; private set; }

        public int LastBackupCleanupCount { get; private set; }

        public long LastBackupCleanupBytes { get; private set; }

        /// <summary>
        /// Captures the application-owned directory passed to the platform launcher.
        /// </summary>
        public Task OpenDirectoryAsync(
            string directoryPath,
            CancellationToken cancellationToken = default)
        {
            OpenedDirectoryPath = directoryPath;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Returns the configured folder selection.
        /// </summary>
        public Task<string?> PickInstallDirectoryAsync(
            string? currentPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SelectedPath);

        /// <summary>
        /// Returns the configured install confirmation and records the prompt.
        /// </summary>
        public Task<bool> ConfirmInstallAsync(
            string installPath,
            int targetCount,
            CancellationToken cancellationToken = default)
        {
            InstallConfirmationCount++;
            return Task.FromResult(ConfirmInstallResult);
        }

        /// <summary>
        /// Returns the configured process-close confirmation and records the prompt.
        /// </summary>
        public Task<bool> ConfirmCloseQqInstallAndRestartAsync(
            string installPath,
            int targetCount,
            CancellationToken cancellationToken = default)
        {
            CloseQqInstallConfirmationCount++;
            return Task.FromResult(ConfirmCloseQqInstallResult);
        }

        /// <summary>
        /// Returns the configured restore confirmation and records the prompt.
        /// </summary>
        public Task<bool> ConfirmRestoreAsync(
            string installPath,
            CancellationToken cancellationToken = default)
        {
            RestoreConfirmationCount++;
            return Task.FromResult(ConfirmRestoreResult);
        }

        /// <summary>
        /// Captures cleanup preview details and returns the configured confirmation result.
        /// </summary>
        public Task<bool> ConfirmBackupCleanupAsync(
            int backupCount,
            long reclaimableBytes,
            CancellationToken cancellationToken = default)
        {
            BackupCleanupConfirmationCount++;
            LastBackupCleanupCount = backupCount;
            LastBackupCleanupBytes = reclaimableBytes;
            return Task.FromResult(ConfirmBackupCleanupResult);
        }
    }
}
