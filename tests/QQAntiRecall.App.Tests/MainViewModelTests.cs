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
        MainViewModel viewModel = new(core, new FakeQqntIntegrityService(), new FakeUserInteractionService());

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
        MainViewModel viewModel = new(core, new FakeQqntIntegrityService(), new FakeUserInteractionService());

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
        MainViewModel viewModel = new(core, new FakeQqntIntegrityService(), interaction);
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
        MainViewModel viewModel = new(core, new FakeQqntIntegrityService(), new FakeUserInteractionService());
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
        MainViewModel viewModel = new(core, new FakeQqntIntegrityService(), interaction);
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
        MainViewModel viewModel = new(core, new FakeQqntIntegrityService(), interaction);
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
        MainViewModel viewModel = new(core, new FakeQqntIntegrityService(), new FakeUserInteractionService());

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
        MainViewModel viewModel = new(core, new FakeQqntIntegrityService(), new FakeUserInteractionService())
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
        MainViewModel viewModel = new(core, new FakeQqntIntegrityService(), interaction);
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
        MainViewModel viewModel = new(core, new FakeQqntIntegrityService(), interaction);
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
        MainViewModel viewModel = new(core, new FakeQqntIntegrityService(), interaction);
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
        MainViewModel viewModel = new(core, new FakeQqntIntegrityService(), interaction);
        await viewModel.Initialization;

        await viewModel.CleanupBackupsCommand.ExecuteAsync(null);

        Assert.Equal(0, core.CleanupCallCount);
        Assert.False(viewModel.IsBusy);
    }

    /// <summary>
    /// Verifies startup maps the QQNT.dll integrity state into its own section without touching patch state.
    /// </summary>
    [Fact]
    public async Task Initialization_MapsIntegrityStateIntoSeparateSection()
    {
        FakeAntiRecallService core = new()
        {
            DiscoveredPath = @"D:\Tencent\QQNT",
            ScanResult = CreateScan(canInstall: true),
        };
        FakeQqntIntegrityService integrity = new();
        MainViewModel viewModel = new(core, integrity, new FakeUserInteractionService());

        await viewModel.Initialization;

        var target = Assert.Single(viewModel.IntegrityTargets);
        Assert.Equal("检测有效", target.StatusLabel);
        Assert.Equal("检测有效", target.StateLabel);
        Assert.True(target.IsReady);
        Assert.False(target.IsPatched);
        Assert.False(target.HasBackup);
        Assert.Equal("无备份", target.BackupLabel);
        Assert.True(viewModel.HasIntegrityTargets);
        Assert.False(viewModel.HasNoIntegrityTargets);
        Assert.Equal("完整性自检会退出 QQ", viewModel.IntegrityStatus);
        Assert.True(viewModel.IsIntegrityWarning);
        Assert.False(viewModel.IsIntegritySuccess);
        Assert.True(viewModel.CanApplyIntegrity);
        Assert.False(viewModel.CanRestoreIntegrity);
        Assert.Equal("已找到可安装版本", viewModel.StatusTitle);
        Assert.Equal(1, integrity.ScanCallCount);
    }

    /// <summary>
    /// Verifies a confirmed integrity apply reaches the core service and reports success.
    /// </summary>
    [Fact]
    public async Task ApplyIntegrityCommand_WhenConfirmed_DisablesSelfCheck()
    {
        FakeAntiRecallService core = new()
        {
            DiscoveredPath = @"D:\Tencent\QQNT",
            ScanResult = CreateScan(canInstall: true),
        };
        QqntIntegrityScanResult patchedScan = FakeQqntIntegrityService.CreateIntegrityScan(
            QqntIntegrityState.Patched,
            canApply: false,
            canRestore: true);
        FakeQqntIntegrityService integrity = new()
        {
            ApplyResult = new QqntIntegrityOperationResult(
                patchedScan,
                "已关闭 1 个 QQNT.dll 的完整性自检，QQ 不会再因补丁而自动退出。",
                Succeeded: true),
        };
        FakeUserInteractionService interaction = new();
        MainViewModel viewModel = new(core, integrity, interaction);
        await viewModel.Initialization;

        await viewModel.ApplyIntegrityCommand.ExecuteAsync(null);

        Assert.Equal(1, interaction.IntegrityApplyConfirmationCount);
        Assert.Equal(1, integrity.ApplyCallCount);
        var target = Assert.Single(viewModel.IntegrityTargets);
        Assert.Equal("已关闭", target.StatusLabel);
        Assert.True(target.IsPatched);
        Assert.True(target.HasBackup);
        Assert.True(viewModel.IsIntegritySuccess);
        Assert.Equal("完整性自检已关闭", viewModel.IntegrityStatus);
        Assert.Contains("完整性自检", viewModel.IntegrityDetail);
        Assert.False(viewModel.CanApplyIntegrity);
        Assert.True(viewModel.CanRestoreIntegrity);
        Assert.False(viewModel.IsBusy);
        Assert.Equal("已找到可安装版本", viewModel.StatusTitle);
    }

    /// <summary>
    /// Verifies declining the integrity confirmation never invokes the core operation.
    /// </summary>
    [Fact]
    public async Task ApplyIntegrityCommand_WhenConfirmationDeclined_DoesNotModify()
    {
        FakeAntiRecallService core = new()
        {
            DiscoveredPath = @"D:\Tencent\QQNT",
            ScanResult = CreateScan(canInstall: true),
        };
        FakeQqntIntegrityService integrity = new();
        FakeUserInteractionService interaction = new() { ConfirmIntegrityApplyResult = false };
        MainViewModel viewModel = new(core, integrity, interaction);
        await viewModel.Initialization;

        await viewModel.ApplyIntegrityCommand.ExecuteAsync(null);

        Assert.Equal(1, interaction.IntegrityApplyConfirmationCount);
        Assert.Equal(0, integrity.ApplyCallCount);
        Assert.True(viewModel.CanApplyIntegrity);
    }

    /// <summary>
    /// Verifies the integrity restore path reaches the core service and reports its refreshed state.
    /// </summary>
    [Fact]
    public async Task RestoreIntegrityCommand_WhenConfirmed_RestoresOriginalBytes()
    {
        FakeAntiRecallService core = new()
        {
            DiscoveredPath = @"D:\Tencent\QQNT",
            ScanResult = CreateScan(canRestore: true, state: TargetPatchState.Installed),
        };
        FakeQqntIntegrityService integrity = new()
        {
            ScanResult = FakeQqntIntegrityService.CreateIntegrityScan(
                QqntIntegrityState.Patched,
                canApply: false,
                canRestore: true),
            RestoreResult = new QqntIntegrityOperationResult(
                FakeQqntIntegrityService.CreateIntegrityScan(),
                "已从备份还原 1 个 QQNT.dll。",
                Succeeded: true),
        };
        FakeUserInteractionService interaction = new();
        MainViewModel viewModel = new(core, integrity, interaction);
        await viewModel.Initialization;

        Assert.True(viewModel.CanRestoreIntegrity);

        await viewModel.RestoreIntegrityCommand.ExecuteAsync(null);

        Assert.Equal(1, interaction.IntegrityRestoreConfirmationCount);
        Assert.Equal(1, integrity.RestoreCallCount);
        var target = Assert.Single(viewModel.IntegrityTargets);
        Assert.Equal("检测有效", target.StatusLabel);
        Assert.True(viewModel.IsIntegrityWarning);
        Assert.Equal("已从备份还原 1 个 QQNT.dll。", viewModel.IntegrityDetail);
    }

    /// <summary>
    /// Verifies an integrity scan failure is contained in its own section and never blocks the patch workflow.
    /// </summary>
    [Fact]
    public async Task Initialization_WhenIntegrityScanFails_KeepsPatchWorkflowUsable()
    {
        FakeAntiRecallService core = new()
        {
            DiscoveredPath = @"D:\Tencent\QQNT",
            ScanResult = CreateScan(canInstall: true),
        };
        FakeQqntIntegrityService integrity = new()
        {
            ScanException = new UnauthorizedAccessException("denied"),
        };
        MainViewModel viewModel = new(core, integrity, new FakeUserInteractionService());

        await viewModel.Initialization;

        Assert.Empty(viewModel.IntegrityTargets);
        Assert.True(viewModel.HasNoIntegrityTargets);
        Assert.Equal("扫描失败", viewModel.IntegrityStatus);
        Assert.Contains("denied", viewModel.IntegrityDetail);
        Assert.True(viewModel.IsIntegrityError);
        Assert.False(viewModel.CanApplyIntegrity);
        Assert.False(viewModel.CanRestoreIntegrity);
        Assert.True(viewModel.CanInstall);
        Assert.Equal("已找到可安装版本", viewModel.StatusTitle);
        Assert.False(viewModel.IsStatusError);
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

        public int IntegrityApplyConfirmationCount { get; private set; }

        public int IntegrityRestoreConfirmationCount { get; private set; }

        public bool ConfirmIntegrityApplyResult { get; init; } = true;

        public bool ConfirmIntegrityRestoreResult { get; init; } = true;

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
        /// Returns the configured integrity apply confirmation and records the prompt.
        /// </summary>
        public Task<bool> ConfirmIntegrityApplyAsync(
            string installPath,
            int targetCount,
            CancellationToken cancellationToken = default)
        {
            IntegrityApplyConfirmationCount++;
            return Task.FromResult(ConfirmIntegrityApplyResult);
        }

        /// <summary>
        /// Returns the configured integrity restore confirmation and records the prompt.
        /// </summary>
        public Task<bool> ConfirmIntegrityRestoreAsync(
            string installPath,
            CancellationToken cancellationToken = default)
        {
            IntegrityRestoreConfirmationCount++;
            return Task.FromResult(ConfirmIntegrityRestoreResult);
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

    /// <summary>
    /// In-memory QQNT.dll integrity fake used to isolate ViewModel behavior from real files.
    /// </summary>
    private sealed class FakeQqntIntegrityService : IQqntIntegrityService
    {
        public QqntIntegrityScanResult ScanResult { get; init; } = CreateIntegrityScan();

        public Exception? ScanException { get; init; }

        public QqntIntegrityOperationResult? ApplyResult { get; init; }

        public QqntIntegrityOperationResult? RestoreResult { get; init; }

        public int ScanCallCount { get; private set; }

        public int ApplyCallCount { get; private set; }

        public int RestoreCallCount { get; private set; }

        /// <summary>
        /// Returns, or throws, the configured integrity scan behavior.
        /// </summary>
        public Task<QqntIntegrityScanResult> ScanAsync(
            string installRoot,
            CancellationToken cancellationToken = default)
        {
            ScanCallCount++;
            if (ScanException is not null)
            {
                throw ScanException;
            }

            return Task.FromResult(ScanResult);
        }

        /// <summary>
        /// Records the apply request and returns its configured refreshed scan.
        /// </summary>
        public Task<QqntIntegrityOperationResult> ApplyAsync(
            string installRoot,
            CancellationToken cancellationToken = default)
        {
            ApplyCallCount++;
            return Task.FromResult(ApplyResult
                ?? new QqntIntegrityOperationResult(ScanResult, "已关闭完整性自检。", Succeeded: true));
        }

        /// <summary>
        /// Records the restore request and returns its configured refreshed scan.
        /// </summary>
        public Task<QqntIntegrityOperationResult> RestoreAsync(
            string installRoot,
            CancellationToken cancellationToken = default)
        {
            RestoreCallCount++;
            return Task.FromResult(RestoreResult
                ?? new QqntIntegrityOperationResult(ScanResult, "已还原原始文件。", Succeeded: true));
        }

        /// <summary>
        /// Creates a deterministic integrity snapshot that never touches real QQ files.
        /// </summary>
        public static QqntIntegrityScanResult CreateIntegrityScan(
            QqntIntegrityState state = QqntIntegrityState.ReadyToPatch,
            bool canApply = true,
            bool canRestore = false,
            bool isQqRunning = false,
            string installRoot = @"D:\Tencent\QQNT")
        {
            QqntIntegrityTargetResult target = new(
                "9.9.37-54276",
                Path.Combine(installRoot, "versions", "9.9.37-54276", "QQNT.dll"),
                state,
                214607912,
                "ABCDEF0123456789",
                Path.Combine(installRoot, "versions", "9.9.37-54276", "QQNT.dll.qqantirecall-orig.bak"),
                state is QqntIntegrityState.Patched or QqntIntegrityState.SkipPatched,
                "校验点 0x517643。");

            return new QqntIntegrityScanResult(
                installRoot,
                IsPlatformSupported: true,
                isQqRunning,
                [target],
                canApply,
                canRestore,
                "发现未处理的完整性自检。");
        }
    }
}
