using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QQAntiRecall.Core;

/// <summary>
/// Discovers QQ and installs or restores the complete offline wrapper.node anti-recall patch.
/// </summary>
public sealed partial class AntiRecallService : IAntiRecallService
{
    private const int BackupSchemaVersion = 1;
    private static readonly SemaphoreSlim OperationGate = new(1, 1);

    private readonly IAntiRecallRuntime _runtime;
    private readonly IAtomicFileReplacer _fileReplacer;
    private readonly string _backupRoot;

    /// <summary>
    /// Creates a service backed by the current operating system and the user's local application data folder.
    /// </summary>
    public AntiRecallService()
        : this(new SystemAntiRecallRuntime(), backupRoot: null, new SystemAtomicFileReplacer())
    {
    }

    /// <summary>
    /// Creates a service with controlled platform and file-replacement dependencies for verification.
    /// </summary>
    internal AntiRecallService(
        IAntiRecallRuntime runtime,
        string? backupRoot = null,
        IAtomicFileReplacer? fileReplacer = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _fileReplacer = fileReplacer ?? new SystemAtomicFileReplacer();
        _backupRoot = backupRoot ?? Path.Combine(runtime.LocalApplicationDataPath, "QQAntiRecall", "backups");
    }

    /// <summary>
    /// Gets the application-owned directory that stores verified QQ backups.
    /// </summary>
    public string BackupDirectoryPath => _backupRoot;

    /// <summary>
    /// Finds the first verified QQ root, preferring registry locations over fixed-drive conventions.
    /// </summary>
    public string? FindInstallRoot()
    {
        if (!_runtime.IsWindows)
        {
            return null;
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawCandidate in _runtime.EnumerateInstallCandidates())
        {
            var candidate = NormalizeInstallCandidate(rawCandidate);
            if (candidate is null || !visited.Add(candidate))
            {
                continue;
            }

            if (IsVerifiedInstallRoot(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Scans only configured current and ready QQ versions without writing files; non-Windows callers receive an unsupported result.
    /// </summary>
    public async Task<AntiRecallScanResult> ScanAsync(
        string installRoot,
        CancellationToken cancellationToken = default)
    {
        var normalizedRoot = NormalizeInstallRoot(installRoot);
        var isRunning = _runtime.IsQqRunning();
        if (!_runtime.IsWindows)
        {
            return new AntiRecallScanResult(
                normalizedRoot,
                IsPlatformSupported: false,
                isRunning,
                [],
                LatestBackupId: null,
                CanInstall: false,
                CanRestore: false,
                "当前平台仅支持扫描界面；wrapper.node 补丁暂仅支持 Windows QQ。");
        }

        var snapshotSet = await LoadTargetSnapshotsAsync(normalizedRoot, cancellationToken).ConfigureAwait(false);
        var results = snapshotSet.Targets.Select(target => target.Result).ToArray();
        var compatibleBackup = await FindLatestCompatibleBackupAsync(
            normalizedRoot,
            snapshotSet.Targets,
            cancellationToken).ConfigureAwait(false);

        var allReady = results.Length > 0 && results.All(target => target.State == TargetPatchState.ReadyToInstall);
        var allInstalled = results.Length > 0 && results.All(target => target.State == TargetPatchState.Installed);
        var allLegacy = results.Length > 0 && results.All(target => target.State == TargetPatchState.LegacyInstalled);
        return new AntiRecallScanResult(
            normalizedRoot,
            IsPlatformSupported: true,
            isRunning,
            results,
            compatibleBackup?.Manifest.BackupId,
            CanInstall: !isRunning && (allReady || (allLegacy && compatibleBackup is not null)),
            CanRestore: !isRunning && (allInstalled || allLegacy) && compatibleBackup is not null,
            BuildScanSummary(results, snapshotSet.Error, isRunning, compatibleBackup is not null));
    }

    /// <summary>
    /// Atomically installs every signature or upgrades the recognized legacy set after exact preflight and backup.
    /// </summary>
    public async Task<PatchOperationResult> InstallAsync(
        string installRoot,
        CancellationToken cancellationToken = default)
    {
        await OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var initialScan = await ScanAsync(installRoot, cancellationToken).ConfigureAwait(false);
            if (!initialScan.IsPlatformSupported)
            {
                return new PatchOperationResult(initialScan, "安装已拒绝：当前平台不支持修改 QQ。");
            }

            if (initialScan.IsQqRunning)
            {
                return new PatchOperationResult(initialScan, "安装已拒绝：请先完全退出 QQ。");
            }

            if (initialScan.Targets.Count > 0
                && initialScan.Targets.All(target => target.State == TargetPatchState.Installed))
            {
                return new PatchOperationResult(initialScan, "所有目标均已安装，无需重复操作。", Succeeded: true);
            }

            if (!initialScan.CanInstall)
            {
                return new PatchOperationResult(initialScan, "安装已拒绝：目标缺失、不受支持或处于混合状态。");
            }

            var snapshotSet = await LoadTargetSnapshotsAsync(initialScan.InstallRoot, cancellationToken).ConfigureAwait(false);
            var installsOriginal = snapshotSet.Targets.Count > 0
                && snapshotSet.Targets.All(target =>
                    target.Result.State == TargetPatchState.ReadyToInstall && target.Content is not null);
            var upgradesLegacy = snapshotSet.Targets.Count > 0
                && snapshotSet.Targets.All(target =>
                    target.Result.State == TargetPatchState.LegacyInstalled && target.Content is not null);
            if (!installsOriginal && !upgradesLegacy)
            {
                var changedScan = await ScanAsync(initialScan.InstallRoot, cancellationToken).ConfigureAwait(false);
                return new PatchOperationResult(changedScan, "安装已拒绝：预检后目标状态发生变化。");
            }

            IReadOnlyList<TargetSnapshot> backupSnapshots = snapshotSet.Targets;
            ReplacementPlan[] replacementPlans;
            if (upgradesLegacy)
            {
                var legacyBackup = await FindLatestCompatibleBackupAsync(
                    initialScan.InstallRoot,
                    snapshotSet.Targets,
                    cancellationToken).ConfigureAwait(false);
                if (legacyBackup is null)
                {
                    var failedScan = await ScanAsync(initialScan.InstallRoot, cancellationToken).ConfigureAwait(false);
                    return new PatchOperationResult(failedScan, "升级已拒绝：找不到与 0.0.1 补丁哈希匹配的原始备份。");
                }

                try
                {
                    backupSnapshots = await CreateOriginalSnapshotsFromBackupAsync(
                        snapshotSet.Targets,
                        legacyBackup,
                        cancellationToken).ConfigureAwait(false);
                    replacementPlans = backupSnapshots
                        .Zip(snapshotSet.Targets, (original, live) =>
                        {
                            var plan = CreateInstallPlan(original);
                            return plan with { ExpectedCurrentSha256 = live.Result.Sha256 };
                        })
                        .ToArray();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is
                    IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    var failedScan = await ScanAsync(initialScan.InstallRoot, cancellationToken).ConfigureAwait(false);
                    return new PatchOperationResult(failedScan, $"升级已拒绝：旧版原始备份验证失败。{exception.Message}");
                }
            }
            else
            {
                replacementPlans = snapshotSet.Targets.Select(CreateInstallPlan).ToArray();
            }

            BackupCandidate backup;
            try
            {
                backup = await CreateBackupAsync(
                    initialScan.InstallRoot,
                    backupSnapshots,
                    replacementPlans,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                var failedScan = await ScanAsync(initialScan.InstallRoot, cancellationToken).ConfigureAwait(false);
                return new PatchOperationResult(failedScan, $"安装已取消：无法创建完整备份。{exception.Message}");
            }

            var failure = await ReplaceTargetsTransactionallyAsync(
                replacementPlans,
                TargetPatchState.Installed,
                cancellationToken).ConfigureAwait(false);
            var finalScan = await ScanAsync(initialScan.InstallRoot, cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                return new PatchOperationResult(finalScan, $"安装失败，已回滚所有已替换目标：{failure}");
            }

            return new PatchOperationResult(
                finalScan,
                upgradesLegacy
                    ? $"0.0.1 旧版补丁已升级，备份编号：{backup.Manifest.BackupId}。"
                    : $"防撤回已安装，备份编号：{backup.Manifest.BackupId}。",
                Succeeded: true);
        }
        finally
        {
            OperationGate.Release();
        }
    }

    /// <summary>
    /// Validates the selected QQ root, closes QQ, installs through the normal transactional path, and restores the QQ session.
    /// </summary>
    public async Task<PatchOperationResult> CloseQqInstallAndRestartAsync(
        string installRoot,
        CancellationToken cancellationToken = default)
    {
        var initialScan = await ScanAsync(installRoot, cancellationToken).ConfigureAwait(false);
        if (!initialScan.IsPlatformSupported)
        {
            return new PatchOperationResult(initialScan, "安装已拒绝：当前平台不支持关闭或修改 QQ。");
        }

        if (!IsVerifiedInstallRoot(initialScan.InstallRoot))
        {
            return new PatchOperationResult(
                initialScan,
                "安装已拒绝：所选目录未通过 QQ.exe 和版本配置校验，因此不会关闭 QQ。");
        }

        var canInstallAfterStop = initialScan.Targets.Count > 0
            && (initialScan.Targets.All(target => target.State == TargetPatchState.ReadyToInstall)
                || (initialScan.Targets.All(target => target.State == TargetPatchState.LegacyInstalled)
                    && initialScan.LatestBackupId is not null));
        if (!canInstallAfterStop)
        {
            return new PatchOperationResult(
                initialScan,
                "安装已拒绝：目标缺失、不受支持或并非全部处于可安装状态，因此不会关闭 QQ。");
        }

        bool stopped = await _runtime.StopQqAsync(cancellationToken).ConfigureAwait(false);
        if (!stopped)
        {
            var failedScan = await ScanAsync(initialScan.InstallRoot, cancellationToken).ConfigureAwait(false);
            return new PatchOperationResult(
                failedScan,
                "无法完全关闭 QQ，未修改任何文件。请手动退出 QQ 后重试。");
        }

        string qqExecutablePath = Path.Combine(initialScan.InstallRoot, "QQ.exe");
        PatchOperationResult installResult;
        try
        {
            installResult = await InstallAsync(initialScan.InstallRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception operationException)
        {
            try
            {
                EnsureQqIsRunning(qqExecutablePath);
            }
            catch (Exception restartException)
            {
                throw new InvalidOperationException(
                    "安装中断，且 QQ 未能自动重新启动。",
                    new AggregateException(operationException, restartException));
            }

            throw;
        }

        Exception? restartFailure = null;
        try
        {
            EnsureQqIsRunning(qqExecutablePath);
        }
        catch (Exception exception)
        {
            restartFailure = exception;
        }

        var finalScan = await ScanAsync(initialScan.InstallRoot, cancellationToken).ConfigureAwait(false);
        if (restartFailure is not null)
        {
            return new PatchOperationResult(
                finalScan,
                $"{installResult.Message} QQ 未能自动重启：{restartFailure.Message} 请手动启动 QQ。",
                Succeeded: false);
        }

        return new PatchOperationResult(
            finalScan,
            $"{installResult.Message} QQ 已重新启动。",
            installResult.Succeeded);
    }

    /// <summary>
    /// Atomically restores every target from the newest exact hash-compatible backup; partial or unknown states are rejected.
    /// </summary>
    public async Task<PatchOperationResult> RestoreAsync(
        string installRoot,
        CancellationToken cancellationToken = default)
    {
        await OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var initialScan = await ScanAsync(installRoot, cancellationToken).ConfigureAwait(false);
            if (!initialScan.IsPlatformSupported)
            {
                return new PatchOperationResult(initialScan, "恢复已拒绝：当前平台不支持修改 QQ。");
            }

            if (initialScan.IsQqRunning)
            {
                return new PatchOperationResult(initialScan, "恢复已拒绝：请先完全退出 QQ。");
            }

            if (initialScan.Targets.Count > 0
                && initialScan.Targets.All(target => target.State == TargetPatchState.ReadyToInstall))
            {
                return new PatchOperationResult(initialScan, "所有目标均为原始状态，无需恢复。", Succeeded: true);
            }

            if (!initialScan.CanRestore)
            {
                return new PatchOperationResult(initialScan, "恢复已拒绝：没有与当前补丁哈希完全匹配的备份，或目标处于混合状态。");
            }

            var snapshotSet = await LoadTargetSnapshotsAsync(initialScan.InstallRoot, cancellationToken).ConfigureAwait(false);
            var backup = await FindLatestCompatibleBackupAsync(
                initialScan.InstallRoot,
                snapshotSet.Targets,
                cancellationToken).ConfigureAwait(false);
            if (backup is null)
            {
                var changedScan = await ScanAsync(initialScan.InstallRoot, cancellationToken).ConfigureAwait(false);
                return new PatchOperationResult(changedScan, "恢复已拒绝：预检后文件或备份哈希发生变化。");
            }

            ReplacementPlan[] replacementPlans;
            try
            {
                replacementPlans = await CreateRestorePlansAsync(
                    snapshotSet.Targets,
                    backup,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                var failedScan = await ScanAsync(initialScan.InstallRoot, cancellationToken).ConfigureAwait(false);
                return new PatchOperationResult(failedScan, $"恢复已拒绝：备份验证失败。{exception.Message}");
            }

            var failure = await ReplaceTargetsTransactionallyAsync(
                replacementPlans,
                TargetPatchState.ReadyToInstall,
                cancellationToken).ConfigureAwait(false);
            var finalScan = await ScanAsync(initialScan.InstallRoot, cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                return new PatchOperationResult(finalScan, $"恢复失败，已回滚所有已替换目标：{failure}");
            }

            return new PatchOperationResult(
                finalScan,
                $"已从备份 {backup.Manifest.BackupId} 恢复原始文件。",
                Succeeded: true);
        }
        finally
        {
            OperationGate.Release();
        }
    }

    /// <summary>
    /// Finds complete managed backups whose target set is obsolete or exactly duplicated without deleting them.
    /// </summary>
    public async Task<BackupCleanupPreview> PreviewBackupCleanupAsync(
        string installRoot,
        CancellationToken cancellationToken = default)
    {
        await OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var analysis = await AnalyzeBackupCleanupAsync(installRoot, cancellationToken).ConfigureAwait(false);
            return new BackupCleanupPreview(
                analysis.Removable.Select(candidate => candidate.Backup.Manifest.BackupId).ToArray(),
                analysis.Removable.Sum(candidate => candidate.Size),
                analysis.RetainedBackupCount,
                analysis.UnrecognizedDirectoryCount);
        }
        finally
        {
            OperationGate.Release();
        }
    }

    /// <summary>
    /// Revalidates the current QQ target set and deletes only approved backups that remain safely removable.
    /// </summary>
    public async Task<BackupCleanupResult> CleanupObsoleteBackupsAsync(
        string installRoot,
        IReadOnlyCollection<string> approvedBackupIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approvedBackupIds);

        var approved = approvedBackupIds
            .Where(backupId => !string.IsNullOrWhiteSpace(backupId))
            .ToHashSet(StringComparer.Ordinal);
        if (approved.Count == 0)
        {
            return new BackupCleanupResult(0, 0, 0, "没有已确认的备份需要清理。", Succeeded: true);
        }

        await OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var analysis = await AnalyzeBackupCleanupAsync(installRoot, cancellationToken).ConfigureAwait(false);
            var removableById = analysis.Removable.ToDictionary(
                candidate => candidate.Backup.Manifest.BackupId,
                StringComparer.Ordinal);
            var deletedCount = 0;
            long reclaimedBytes = 0;

            foreach (var backupId in approved)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!removableById.TryGetValue(backupId, out var candidate))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(candidate.Backup.Directory, recursive: true);
                    deletedCount++;
                    reclaimedBytes += candidate.Size;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Continue with other explicitly approved directories and report the partial result.
                }
            }

            var skippedCount = approved.Count - deletedCount;
            var succeeded = skippedCount == 0;
            var message = deletedCount == 0
                ? $"未删除任何备份；{skippedCount} 个备份已不存在、不再过期或无法访问。"
                : skippedCount == 0
                    ? $"已清理 {deletedCount} 个旧备份。当前版本可用备份已保留。"
                    : $"已清理 {deletedCount} 个旧备份，另有 {skippedCount} 个因状态变化或访问失败而保留。";
            return new BackupCleanupResult(
                deletedCount,
                reclaimedBytes,
                skippedCount,
                message,
                succeeded);
        }
        finally
        {
            OperationGate.Release();
        }
    }

    /// <summary>
    /// Classifies complete application-owned backups against the currently configured QQ target identities.
    /// </summary>
    private async Task<BackupCleanupAnalysis> AnalyzeBackupCleanupAsync(
        string installRoot,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = NormalizeInstallRoot(installRoot);
        var snapshotSet = await LoadTargetSnapshotsAsync(normalizedRoot, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(snapshotSet.Error))
        {
            throw new InvalidOperationException($"无法确定当前 QQ 版本，未清理任何备份。{snapshotSet.Error}");
        }

        if (snapshotSet.Targets.Count == 0)
        {
            throw new InvalidOperationException("无法确定当前 QQ 版本，未清理任何备份。");
        }

        var configuredTargets = snapshotSet.Targets.Select(snapshot => snapshot.Target).ToArray();

        if (!Directory.Exists(_backupRoot))
        {
            return new BackupCleanupAnalysis([], 0, 0);
        }

        string[] directories;
        try
        {
            directories = Directory.EnumerateDirectories(_backupRoot).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException("无法读取备份目录，未清理任何备份。", exception);
        }

        var managed = new List<ManagedBackupCandidate>();
        var unrecognizedDirectoryCount = 0;
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetFileName(directory).StartsWith(".pending-", StringComparison.Ordinal))
            {
                unrecognizedDirectoryCount++;
                continue;
            }

            var backup = await TryReadBackupCandidateAsync(directory, cancellationToken).ConfigureAwait(false);
            var size = backup is null
                ? null
                : await TryMeasureManagedBackupAsync(backup, cancellationToken).ConfigureAwait(false);
            if (backup is null || size is null)
            {
                unrecognizedDirectoryCount++;
                continue;
            }

            managed.Add(new ManagedBackupCandidate(backup, size.Value));
        }

        var removable = new List<ManagedBackupCandidate>();
        var currentTargetBackups = new List<ManagedBackupCandidate>();
        foreach (var candidate in managed)
        {
            if (!PathsEqual(candidate.Backup.Manifest.InstallRoot, normalizedRoot))
            {
                continue;
            }

            if (!HasExactTargetSet(candidate.Backup.Manifest.Targets, configuredTargets))
            {
                removable.Add(candidate);
                continue;
            }

            currentTargetBackups.Add(candidate);
        }

        var pending = currentTargetBackups
            .OrderByDescending(item => item.Backup.Manifest.CreatedUtc)
            .ToList();
        while (pending.Count > 0)
        {
            var seed = pending[0];
            var equivalentOriginals = pending.Where(candidate =>
                HaveEquivalentOriginalBytes(seed.Backup.Manifest, candidate.Backup.Manifest)).ToArray();
            pending.RemoveAll(candidate => equivalentOriginals.Contains(candidate));

            var compatibleWithLiveTargets = equivalentOriginals.Where(candidate =>
                HasLivePatchedHashes(candidate.Backup.Manifest, snapshotSet.Targets)).ToArray();
            if (compatibleWithLiveTargets.Length > 0)
            {
                removable.AddRange(equivalentOriginals.Except([compatibleWithLiveTargets[0]]));
                continue;
            }

            var retainedPatchVariants = new List<ManagedBackupCandidate>();
            foreach (var candidate in equivalentOriginals)
            {
                if (retainedPatchVariants.Any(retained =>
                    HaveEquivalentRestoreBytes(retained.Backup.Manifest, candidate.Backup.Manifest)))
                {
                    removable.Add(candidate);
                }
                else
                {
                    retainedPatchVariants.Add(candidate);
                }
            }
        }

        return new BackupCleanupAnalysis(
            removable.OrderBy(candidate => candidate.Backup.Manifest.CreatedUtc).ToArray(),
            managed.Count - removable.Count,
            unrecognizedDirectoryCount);
    }

    /// <summary>
    /// Validates the exact application-created directory layout, original hashes, and patch signatures before deletion is considered.
    /// </summary>
    private static async Task<long?> TryMeasureManagedBackupAsync(
        BackupCandidate backup,
        CancellationToken cancellationToken)
    {
        try
        {
            if ((File.GetAttributes(backup.Directory) & FileAttributes.ReparsePoint) != 0
                || backup.Manifest.Targets.Count == 0)
            {
                return null;
            }

            var manifestPath = Path.GetFullPath(Path.Combine(backup.Directory, "manifest.json"));
            var filesDirectory = Path.GetFullPath(Path.Combine(backup.Directory, "files"));
            if (!File.Exists(manifestPath)
                || !Directory.Exists(filesDirectory)
                || (File.GetAttributes(filesDirectory) & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            var childDirectories = Directory.EnumerateDirectories(backup.Directory).Select(Path.GetFullPath).ToArray();
            if (childDirectories.Length != 1
                || !string.Equals(childDirectories[0], filesDirectory, StringComparison.OrdinalIgnoreCase)
                || Directory.EnumerateDirectories(filesDirectory).Any())
            {
                return null;
            }

            var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { manifestPath };
            var seenTargets = new List<BackupTargetManifest>();
            foreach (var target in backup.Manifest.Targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(target.Version)
                    || !IsSafeVersionDirectoryName(target.Version)
                    || string.IsNullOrWhiteSpace(target.RelativePath)
                    || seenTargets.Any(existing =>
                        string.Equals(existing.Version, target.Version, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(existing.RelativePath, target.RelativePath, StringComparison.OrdinalIgnoreCase)))
                {
                    return null;
                }

                var expectedRelativePath = Path.Combine(
                        "versions",
                        target.Version,
                        "resources",
                        "app",
                        "wrapper.node")
                    .Replace('\\', '/');
                if (!string.Equals(target.RelativePath, expectedRelativePath, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var backupFilePath = ResolveBackupFilePath(backup.Directory, target.BackupFileName);
                if (backupFilePath is null
                    || !string.Equals(Path.GetDirectoryName(backupFilePath), filesDirectory, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(backupFilePath)
                    || (File.GetAttributes(backupFilePath) & FileAttributes.ReparsePoint) != 0
                    || !expectedFiles.Add(Path.GetFullPath(backupFilePath)))
                {
                    return null;
                }

                var content = await File.ReadAllBytesAsync(backupFilePath, cancellationToken).ConfigureAwait(false);
                var configuredTarget = new ConfiguredTarget(
                    target.Version,
                    FilePath: string.Empty,
                    target.RelativePath,
                    IsValid: true);
                var originalResult = AnalyzeTarget(configuredTarget, content);
                if (!string.Equals(originalResult.Sha256, target.OriginalSha256, StringComparison.OrdinalIgnoreCase)
                    || originalResult.State != TargetPatchState.ReadyToInstall)
                {
                    return null;
                }

                var generatedPatch = CreateInstallPlan(new TargetSnapshot(configuredTarget, originalResult, content));
                var currentPatchMatches = string.Equals(
                    generatedPatch.NewSha256,
                    target.PatchedSha256,
                    StringComparison.OrdinalIgnoreCase);
                if (!currentPatchMatches
                    && !string.Equals(
                        ComputeSha256(ApplyPatchDefinitions(content, PatchCatalog.LegacyDefinitions)),
                        target.PatchedSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                seenTargets.Add(target);
            }

            var actualFiles = Directory.EnumerateFiles(backup.Directory)
                .Concat(Directory.EnumerateFiles(filesDirectory))
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!expectedFiles.SetEquals(actualFiles))
            {
                return null;
            }

            return expectedFiles.Sum(path => new FileInfo(path).Length);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Determines whether a manifest names exactly the configured current and ready QQ target set.
    /// </summary>
    private static bool HasExactTargetSet(
        IReadOnlyList<BackupTargetManifest> manifestTargets,
        IReadOnlyList<ConfiguredTarget> configuredTargets)
    {
        return manifestTargets.Count == configuredTargets.Count
            && configuredTargets.All(target => FindUniqueManifestTarget(manifestTargets, target) is not null);
    }

    /// <summary>
    /// Determines whether two backups contain the same original and patched hashes for every target identity.
    /// </summary>
    private static bool HaveEquivalentRestoreBytes(BackupManifest left, BackupManifest right)
    {
        if (left.Targets.Count != right.Targets.Count)
        {
            return false;
        }

        foreach (var leftTarget in left.Targets)
        {
            var matches = right.Targets.Where(rightTarget =>
                string.Equals(rightTarget.Version, leftTarget.Version, StringComparison.OrdinalIgnoreCase)
                && string.Equals(rightTarget.RelativePath, leftTarget.RelativePath, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1
                || !string.Equals(matches[0].OriginalSha256, leftTarget.OriginalSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(matches[0].PatchedSha256, leftTarget.PatchedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Determines whether two backups restore identical original bytes for every target identity.
    /// </summary>
    private static bool HaveEquivalentOriginalBytes(BackupManifest left, BackupManifest right)
    {
        if (left.Targets.Count != right.Targets.Count)
        {
            return false;
        }

        foreach (var leftTarget in left.Targets)
        {
            var matches = right.Targets.Where(rightTarget =>
                string.Equals(rightTarget.Version, leftTarget.Version, StringComparison.OrdinalIgnoreCase)
                && string.Equals(rightTarget.RelativePath, leftTarget.RelativePath, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1
                || !string.Equals(matches[0].OriginalSha256, leftTarget.OriginalSha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks whether a backup manifest was created for the exact hashes currently installed in every target.
    /// </summary>
    private static bool HasLivePatchedHashes(
        BackupManifest manifest,
        IReadOnlyList<TargetSnapshot> liveSnapshots)
    {
        if (manifest.Targets.Count != liveSnapshots.Count)
        {
            return false;
        }

        foreach (var snapshot in liveSnapshots)
        {
            var target = FindUniqueManifestTarget(manifest.Targets, snapshot.Target);
            if (target is null
                || string.IsNullOrWhiteSpace(snapshot.Result.Sha256)
                || !string.Equals(target.PatchedSha256, snapshot.Result.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Loads configured target paths and captures their exact bytes and signature state.
    /// </summary>
    private static async Task<TargetSnapshotSet> LoadTargetSnapshotsAsync(
        string installRoot,
        CancellationToken cancellationToken)
    {
        var configuredTargets = await ReadConfiguredTargetsAsync(installRoot, cancellationToken).ConfigureAwait(false);
        var snapshots = new List<TargetSnapshot>(configuredTargets.Targets.Count);
        foreach (var configuredTarget in configuredTargets.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!configuredTarget.IsValid)
            {
                snapshots.Add(new TargetSnapshot(
                    configuredTarget,
                    CreateUnavailableResult(
                        configuredTarget,
                        TargetPatchState.Unsupported,
                        "配置中的版本名称不是安全的单级目录名。"),
                    Content: null));
                continue;
            }

            if (!File.Exists(configuredTarget.FilePath))
            {
                snapshots.Add(new TargetSnapshot(
                    configuredTarget,
                    CreateUnavailableResult(configuredTarget, TargetPatchState.Missing, "未找到 wrapper.node。"),
                    Content: null));
                continue;
            }

            try
            {
                var content = await File.ReadAllBytesAsync(configuredTarget.FilePath, cancellationToken).ConfigureAwait(false);
                snapshots.Add(new TargetSnapshot(configuredTarget, AnalyzeTarget(configuredTarget, content), content));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                snapshots.Add(new TargetSnapshot(
                    configuredTarget,
                    CreateUnavailableResult(
                        configuredTarget,
                        TargetPatchState.Unsupported,
                        $"无法读取 wrapper.node：{exception.Message}"),
                    Content: null));
            }
        }

        return new TargetSnapshotSet(snapshots, configuredTargets.Error);
    }

    /// <summary>
    /// Starts the verified QQ executable only when no QQ process is already visible.
    /// </summary>
    private void EnsureQqIsRunning(string executablePath)
    {
        if (!_runtime.IsQqRunning())
        {
            _runtime.StartQq(executablePath);
        }
    }

    /// <summary>
    /// Reads only curVersion and readyVersion and maps each unique non-empty value to wrapper.node.
    /// </summary>
    private static async Task<ConfiguredTargetSet> ReadConfiguredTargetsAsync(
        string installRoot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            return new ConfiguredTargetSet([], "QQ 安装目录为空。");
        }

        var versionsRoot = Path.Combine(installRoot, "versions");
        var configPath = Path.Combine(versionsRoot, "config.json");
        if (!File.Exists(configPath))
        {
            return new ConfiguredTargetSet([], "未找到 versions/config.json。");
        }

        try
        {
            await using var stream = new FileStream(
                configPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var targets = new List<ConfiguredTarget>();
            var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddConfiguredTarget(document.RootElement, "curVersion", installRoot, versionsRoot, versions, targets);
            AddConfiguredTarget(document.RootElement, "readyVersion", installRoot, versionsRoot, versions, targets);

            return targets.Count == 0
                ? new ConfiguredTargetSet([], "config.json 中没有非空的 curVersion 或 readyVersion。")
                : new ConfiguredTargetSet(targets, Error: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return new ConfiguredTargetSet([], $"无法解析 versions/config.json：{exception.Message}");
        }
    }

    /// <summary>
    /// Adds one unique string version property while rejecting path traversal and nested directories.
    /// </summary>
    private static void AddConfiguredTarget(
        JsonElement root,
        string propertyName,
        string installRoot,
        string versionsRoot,
        ISet<string> versions,
        ICollection<ConfiguredTarget> targets)
    {
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var version = property.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(version) || !versions.Add(version))
        {
            return;
        }

        var isValid = IsSafeVersionDirectoryName(version);
        var filePath = isValid
            ? Path.Combine(versionsRoot, version, "resources", "app", "wrapper.node")
            : string.Empty;
        var relativePath = isValid
            ? Path.GetRelativePath(installRoot, filePath).Replace('\\', '/')
            : string.Empty;
        targets.Add(new ConfiguredTarget(version, filePath, relativePath, isValid));
    }

    /// <summary>
    /// Ensures a version value can name exactly one child directory beneath versions.
    /// </summary>
    private static bool IsSafeVersionDirectoryName(string version)
    {
        return version is not "." and not ".."
            && !Path.IsPathRooted(version)
            && version.IndexOfAny(['\\', '/']) < 0
            && version.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    /// <summary>
    /// Classifies a target from exact original and patched wildcard match counts.
    /// </summary>
    private static TargetScanResult AnalyzeTarget(ConfiguredTarget target, byte[] content)
    {
        var statuses = PatchCatalog.Definitions
            .Select(definition => new PatchSignatureStatus(
                definition.Name,
                WildcardPattern.FindAll(content, definition.OriginalPattern).Count,
                WildcardPattern.FindAll(content, definition.PatchedPattern).Count))
            .ToArray();

        var allOriginal = statuses.Zip(PatchCatalog.Definitions).All(pair =>
                pair.First.OriginalMatchCount == pair.Second.ExpectedMatchCount
                && pair.First.PatchedMatchCount == 0)
            && PatchCatalog.HasValidNormalRecallCallTargets(content);
        var allPatched = statuses.Zip(PatchCatalog.Definitions).All(pair =>
                pair.First.OriginalMatchCount == 0
                && pair.First.PatchedMatchCount == pair.Second.ExpectedMatchCount)
            && PatchCatalog.HasUnmodifiedNormalRecallFunction(content);
        var legacyInstalled = PatchCatalog.IsLegacyInstalled(content)
            && PatchCatalog.HasValidNormalRecallCallTargets(content);
        var noKnownSignature = statuses.All(status => status.OriginalMatchCount == 0 && status.PatchedMatchCount == 0);
        var state = allOriginal
            ? TargetPatchState.ReadyToInstall
            : allPatched
                ? TargetPatchState.Installed
                : legacyInstalled
                    ? TargetPatchState.LegacyInstalled
                    : noKnownSignature
                        ? TargetPatchState.Unsupported
                        : TargetPatchState.Inconsistent;
        var detail = state switch
        {
            TargetPatchState.ReadyToInstall => $"{PatchCatalog.Definitions.Count} 组原始签名完整匹配。",
            TargetPatchState.Installed => $"{PatchCatalog.Definitions.Count} 组补丁签名完整匹配。",
            TargetPatchState.LegacyInstalled => "检测到 0.0.1 旧版补丁；可从原备份安全升级或恢复。",
            TargetPatchState.Unsupported => "未匹配到受支持的 QQ 签名。",
            _ => "签名缺失、重复，或原始与补丁状态混合。",
        };

        return new TargetScanResult(
            target.Version,
            target.FilePath,
            state,
            ComputeSha256(content),
            statuses,
            detail);
    }

    /// <summary>
    /// Builds a target result when bytes are unavailable and all signature counts are therefore zero.
    /// </summary>
    private static TargetScanResult CreateUnavailableResult(
        ConfiguredTarget target,
        TargetPatchState state,
        string detail)
    {
        var statuses = PatchCatalog.Definitions
            .Select(definition => new PatchSignatureStatus(definition.Name, 0, 0))
            .ToArray();
        return new TargetScanResult(target.Version, target.FilePath, state, string.Empty, statuses, detail);
    }

    /// <summary>
    /// Creates patched bytes only after re-confirming every original signature has its required match count.
    /// </summary>
    private static ReplacementPlan CreateInstallPlan(TargetSnapshot snapshot)
    {
        var original = snapshot.Content ?? throw new InvalidOperationException("Target content was not loaded.");
        var patched = ApplyPatchDefinitions(original, PatchCatalog.Definitions);
        var patchedResult = AnalyzeTarget(snapshot.Target, patched);
        if (patchedResult.State != TargetPatchState.Installed)
        {
            throw new InvalidDataException("Generated bytes did not produce the complete installed state.");
        }

        return new ReplacementPlan(
            snapshot.Target,
            snapshot.Result.Sha256,
            patchedResult.Sha256,
            patched);
    }

    /// <summary>
    /// Applies one complete catalog to a clone after validating every declared original match count.
    /// </summary>
    private static byte[] ApplyPatchDefinitions(
        byte[] original,
        IReadOnlyList<PatchDefinition> definitions)
    {
        var patched = (byte[])original.Clone();
        foreach (var definition in definitions)
        {
            var matches = WildcardPattern.FindAll(patched, definition.OriginalPattern);
            if (matches.Count != definition.ExpectedMatchCount)
            {
                throw new InvalidDataException(
                    $"{definition.Name} no longer has {definition.ExpectedMatchCount} original matches.");
            }

            foreach (var match in matches)
            {
                definition.Replacement.CopyTo(patched, match + definition.PatchOffset);
            }
        }

        return patched;
    }

    /// <summary>
    /// Creates a durable backup directory and manifest before any QQ target is modified.
    /// </summary>
    private async Task<BackupCandidate> CreateBackupAsync(
        string installRoot,
        IReadOnlyList<TargetSnapshot> snapshots,
        IReadOnlyList<ReplacementPlan> replacementPlans,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_backupRoot);
        var backupId = $"{_runtime.UtcNow:yyyyMMdd'T'HHmmssfffffff'Z'}-{Guid.NewGuid():N}";
        var pendingDirectory = Path.Combine(_backupRoot, $".pending-{backupId}");
        var finalDirectory = Path.Combine(_backupRoot, backupId);
        var filesDirectory = Path.Combine(pendingDirectory, "files");
        Directory.CreateDirectory(filesDirectory);

        try
        {
            var manifestTargets = new List<BackupTargetManifest>(snapshots.Count);
            for (var index = 0; index < snapshots.Count; index++)
            {
                var snapshot = snapshots[index];
                var content = snapshot.Content ?? throw new InvalidDataException("Backup source bytes are unavailable.");
                if (!string.Equals(ComputeSha256(content), snapshot.Result.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Backup source hash changed during preflight.");
                }

                var backupFileName = $"files/{index:D3}.wrapper.node";
                var backupFilePath = Path.Combine(pendingDirectory, backupFileName.Replace('/', Path.DirectorySeparatorChar));
                await WriteNewFileDurablyAsync(backupFilePath, content, cancellationToken).ConfigureAwait(false);
                var persistedHash = await ComputeFileSha256Async(backupFilePath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(persistedHash, snapshot.Result.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Persisted backup hash does not match its source.");
                }

                manifestTargets.Add(new BackupTargetManifest(
                    snapshot.Target.Version,
                    snapshot.Target.RelativePath,
                    backupFileName,
                    snapshot.Result.Sha256,
                    replacementPlans[index].NewSha256));
            }

            var manifest = new BackupManifest(
                BackupSchemaVersion,
                backupId,
                _runtime.UtcNow,
                NormalizeInstallRoot(installRoot),
                manifestTargets);
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
                manifest,
                BackupManifestJsonContext.Default.BackupManifest);
            await WriteNewFileDurablyAsync(
                Path.Combine(pendingDirectory, "manifest.json"),
                manifestBytes,
                cancellationToken).ConfigureAwait(false);
            var persistedManifest = await File.ReadAllBytesAsync(
                Path.Combine(pendingDirectory, "manifest.json"),
                cancellationToken).ConfigureAwait(false);
            if (!persistedManifest.AsSpan().SequenceEqual(manifestBytes))
            {
                throw new IOException("Persisted backup manifest failed byte-for-byte verification.");
            }

            Directory.Move(pendingDirectory, finalDirectory);
            return new BackupCandidate(finalDirectory, manifest);
        }
        catch
        {
            TryDeleteDirectory(pendingDirectory);
            throw;
        }
    }

    /// <summary>
    /// Finds the newest manifest whose target set, live patched hashes, and backup original hashes all match.
    /// </summary>
    private async Task<BackupCandidate?> FindLatestCompatibleBackupAsync(
        string installRoot,
        IReadOnlyList<TargetSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var state = snapshots.FirstOrDefault()?.Result.State;
        if (snapshots.Count == 0
            || state is not (TargetPatchState.Installed or TargetPatchState.LegacyInstalled)
            || snapshots.Any(snapshot => snapshot.Result.State != state)
            || !Directory.Exists(_backupRoot))
        {
            return null;
        }

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(_backupRoot).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var candidates = new List<BackupCandidate>();
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetFileName(directory).StartsWith(".pending-", StringComparison.Ordinal))
            {
                continue;
            }

            var candidate = await TryReadBackupCandidateAsync(directory, cancellationToken).ConfigureAwait(false);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        foreach (var candidate in candidates.OrderByDescending(item => item.Manifest.CreatedUtc))
        {
            if (await IsBackupCompatibleAsync(
                    installRoot,
                    snapshots,
                    candidate,
                    cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Loads the exact original files from a compatible legacy backup for a transactional patch upgrade.
    /// </summary>
    private static async Task<TargetSnapshot[]> CreateOriginalSnapshotsFromBackupAsync(
        IReadOnlyList<TargetSnapshot> liveSnapshots,
        BackupCandidate backup,
        CancellationToken cancellationToken)
    {
        var restorePlans = await CreateRestorePlansAsync(
            liveSnapshots,
            backup,
            cancellationToken).ConfigureAwait(false);
        return restorePlans.Select(plan =>
        {
            var result = AnalyzeTarget(plan.Target, plan.NewContent);
            if (result.State != TargetPatchState.ReadyToInstall)
            {
                throw new InvalidDataException("Legacy backup does not contain a supported original target.");
            }

            return new TargetSnapshot(plan.Target, result, plan.NewContent);
        }).ToArray();
    }

    /// <summary>
    /// Reads one manifest and rejects malformed schemas or identities without surfacing discovery errors.
    /// </summary>
    private static async Task<BackupCandidate?> TryReadBackupCandidateAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        try
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            await using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var manifest = await JsonSerializer.DeserializeAsync(
                stream,
                BackupManifestJsonContext.Default.BackupManifest,
                cancellationToken).ConfigureAwait(false);
            if (manifest is null
                || manifest.SchemaVersion != BackupSchemaVersion
                || string.IsNullOrWhiteSpace(manifest.BackupId)
                || !string.Equals(manifest.BackupId, Path.GetFileName(directory), StringComparison.Ordinal)
                || manifest.Targets is null
                || manifest.Targets.Any(target => target is null))
            {
                return null;
            }

            return new BackupCandidate(directory, manifest);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Verifies exact target identity, live patched hashes, and original backup bytes for one manifest.
    /// </summary>
    private static async Task<bool> IsBackupCompatibleAsync(
        string installRoot,
        IReadOnlyList<TargetSnapshot> snapshots,
        BackupCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (!PathsEqual(candidate.Manifest.InstallRoot, installRoot)
            || candidate.Manifest.Targets.Count != snapshots.Count)
        {
            return false;
        }

        foreach (var snapshot in snapshots)
        {
            var manifestTarget = FindUniqueManifestTarget(candidate.Manifest.Targets, snapshot.Target);
            if (manifestTarget is null
                || !string.Equals(manifestTarget.PatchedSha256, snapshot.Result.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var backupFilePath = ResolveBackupFilePath(candidate.Directory, manifestTarget.BackupFileName);
            if (backupFilePath is null || !File.Exists(backupFilePath))
            {
                return false;
            }

            try
            {
                var backupContent = await File.ReadAllBytesAsync(backupFilePath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(ComputeSha256(backupContent), manifestTarget.OriginalSha256, StringComparison.OrdinalIgnoreCase)
                    || AnalyzeTarget(snapshot.Target, backupContent).State != TargetPatchState.ReadyToInstall)
                {
                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Loads and revalidates original backup bytes and pairs them with their exact live patched hashes.
    /// </summary>
    private static async Task<ReplacementPlan[]> CreateRestorePlansAsync(
        IReadOnlyList<TargetSnapshot> snapshots,
        BackupCandidate backup,
        CancellationToken cancellationToken)
    {
        var plans = new List<ReplacementPlan>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            var manifestTarget = FindUniqueManifestTarget(backup.Manifest.Targets, snapshot.Target)
                ?? throw new InvalidDataException("Backup manifest target identity is missing or duplicated.");
            if (!string.Equals(snapshot.Result.Sha256, manifestTarget.PatchedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Current patched hash no longer matches the backup manifest.");
            }

            var backupFilePath = ResolveBackupFilePath(backup.Directory, manifestTarget.BackupFileName)
                ?? throw new InvalidDataException("Backup file path escapes its backup directory.");
            var originalContent = await File.ReadAllBytesAsync(backupFilePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(ComputeSha256(originalContent), manifestTarget.OriginalSha256, StringComparison.OrdinalIgnoreCase)
                || AnalyzeTarget(snapshot.Target, originalContent).State != TargetPatchState.ReadyToInstall)
            {
                throw new InvalidDataException("Backup original hash or signature validation failed.");
            }

            plans.Add(new ReplacementPlan(
                snapshot.Target,
                manifestTarget.PatchedSha256,
                manifestTarget.OriginalSha256,
                originalContent));
        }

        return plans.ToArray();
    }

    /// <summary>
    /// Returns one exact version/path manifest entry and rejects duplicate identities.
    /// </summary>
    private static BackupTargetManifest? FindUniqueManifestTarget(
        IReadOnlyList<BackupTargetManifest> manifestTargets,
        ConfiguredTarget target)
    {
        BackupTargetManifest? result = null;
        foreach (var candidate in manifestTargets)
        {
            if (candidate is null
                || !string.Equals(candidate.Version, target.Version, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(candidate.RelativePath, target.RelativePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (result is not null)
            {
                return null;
            }

            result = candidate;
        }

        return result;
    }

    /// <summary>
    /// Stages and atomically replaces every target, verifies both displaced and new bytes, and rolls back the batch on failure.
    /// </summary>
    private async Task<string?> ReplaceTargetsTransactionallyAsync(
        IReadOnlyList<ReplacementPlan> plans,
        TargetPatchState expectedNewState,
        CancellationToken cancellationToken)
    {
        var staged = new List<StagedReplacement>(plans.Count);
        var replaced = new List<StagedReplacement>(plans.Count);
        var preserveRollbackFiles = false;
        try
        {
            foreach (var plan in plans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetDirectory = Path.GetDirectoryName(plan.Target.FilePath)
                    ?? throw new IOException("Target has no parent directory.");
                var token = Guid.NewGuid().ToString("N");
                var temporaryPath = Path.Combine(targetDirectory, $".wrapper.node.qqantirecall.{token}.tmp");
                var rollbackPath = Path.Combine(targetDirectory, $".wrapper.node.qqantirecall.{token}.rollback");
                await WriteNewFileDurablyAsync(temporaryPath, plan.NewContent, cancellationToken).ConfigureAwait(false);
                var stagedContent = await File.ReadAllBytesAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(ComputeSha256(stagedContent), plan.NewSha256, StringComparison.OrdinalIgnoreCase)
                    || AnalyzeTarget(plan.Target, stagedContent).State != expectedNewState)
                {
                    throw new IOException("Staged target did not pass hash and signature verification.");
                }

                staged.Add(new StagedReplacement(plan, temporaryPath, rollbackPath));
            }

            foreach (var item in staged)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_runtime.IsQqRunning())
                {
                    throw new IOException("QQ started while the operation was being prepared.");
                }

                var liveHash = await ComputeFileSha256Async(item.Plan.Target.FilePath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(liveHash, item.Plan.ExpectedCurrentSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("A target changed after preflight; no unknown bytes were overwritten.");
                }

                _fileReplacer.Replace(item.TemporaryPath, item.Plan.Target.FilePath, item.RollbackPath);
                replaced.Add(item);
                var displacedHash = await ComputeFileSha256Async(item.RollbackPath, cancellationToken).ConfigureAwait(false);
                item.DisplacedSha256 = displacedHash;
                if (!string.Equals(
                        displacedHash,
                        item.Plan.ExpectedCurrentSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("The atomically displaced target did not match its preflight hash.");
                }
            }

            foreach (var item in replaced)
            {
                var installedContent = await File.ReadAllBytesAsync(item.Plan.Target.FilePath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(ComputeSha256(installedContent), item.Plan.NewSha256, StringComparison.OrdinalIgnoreCase)
                    || AnalyzeTarget(item.Plan.Target, installedContent).State != expectedNewState)
                {
                    throw new IOException("A replaced target failed post-write verification.");
                }
            }

            foreach (var item in replaced)
            {
                TryDeleteFile(item.RollbackPath);
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            var rollbackErrors = await RollBackReplacedTargetsAsync(replaced).ConfigureAwait(false);
            if (rollbackErrors.Count > 0)
            {
                preserveRollbackFiles = true;
                throw new IOException("Operation was cancelled and rollback was incomplete.", new AggregateException(rollbackErrors));
            }

            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            var rollbackErrors = await RollBackReplacedTargetsAsync(replaced).ConfigureAwait(false);
            if (rollbackErrors.Count > 0)
            {
                preserveRollbackFiles = true;
                throw new IOException("Patch operation failed and rollback was incomplete.", new AggregateException(rollbackErrors.Prepend(exception)));
            }

            return exception.Message;
        }
        finally
        {
            foreach (var item in staged)
            {
                TryDeleteFile(item.TemporaryPath);
                if (!preserveRollbackFiles)
                {
                    TryDeleteFile(item.RollbackPath);
                }
            }
        }
    }

    /// <summary>
    /// Restores targets in reverse order, verifies pre-operation hashes, and returns errors so recovery artifacts can be retained.
    /// </summary>
    private async Task<IReadOnlyList<Exception>> RollBackReplacedTargetsAsync(IReadOnlyList<StagedReplacement> replaced)
    {
        var errors = new List<Exception>();
        for (var index = replaced.Count - 1; index >= 0; index--)
        {
            var item = replaced[index];
            var discardPath = $"{item.RollbackPath}.{Guid.NewGuid():N}.discard";
            try
            {
                if (File.Exists(item.Plan.Target.FilePath))
                {
                    _fileReplacer.Replace(item.RollbackPath, item.Plan.Target.FilePath, discardPath);
                }
                else
                {
                    File.Move(item.RollbackPath, item.Plan.Target.FilePath);
                }

                var restoredHash = await ComputeFileSha256Async(item.Plan.Target.FilePath, CancellationToken.None).ConfigureAwait(false);
                var expectedRestoredHash = item.DisplacedSha256 ?? item.Plan.ExpectedCurrentSha256;
                if (!string.Equals(restoredHash, expectedRestoredHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Rollback hash verification failed.");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add(exception);
            }
            finally
            {
                TryDeleteFile(discardPath);
            }
        }

        return errors;
    }

    /// <summary>
    /// Writes a new file with write-through semantics and flushes its contents before returning.
    /// </summary>
    private static async Task WriteNewFileDurablyAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Computes an uppercase SHA-256 hash for in-memory bytes.
    /// </summary>
    private static string ComputeSha256(ReadOnlySpan<byte> content)
    {
        return Convert.ToHexString(SHA256.HashData(content));
    }

    /// <summary>
    /// Computes an uppercase SHA-256 hash without loading an additional full file copy.
    /// </summary>
    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Resolves a manifest file beneath its backup directory and rejects rooted or escaping paths.
    /// </summary>
    private static string? ResolveBackupFilePath(string backupDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(backupDirectory);
            var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? candidate : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Normalizes an explicit install root while preserving invalid input as an empty non-actionable value.
    /// </summary>
    private static string NormalizeInstallRoot(string? installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            return string.Empty;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot.Trim().Trim('"')));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Converts registry or conventional candidates to directory roots and handles executable values.
    /// </summary>
    private static string? NormalizeInstallCandidate(string? rawCandidate)
    {
        var normalized = NormalizeInstallRoot(rawCandidate);
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (string.Equals(Path.GetExtension(normalized), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            normalized = Path.GetDirectoryName(normalized) ?? string.Empty;
        }

        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    /// <summary>
    /// Requires both QQ.exe and versions/config.json before accepting a discovered install root.
    /// </summary>
    private static bool IsVerifiedInstallRoot(string installRoot)
    {
        return File.Exists(Path.Combine(installRoot, "QQ.exe"))
            && File.Exists(Path.Combine(installRoot, "versions", "config.json"));
    }

    /// <summary>
    /// Compares normalized Windows paths without case sensitivity.
    /// </summary>
    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            NormalizeInstallRoot(left),
            NormalizeInstallRoot(right),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Produces a concise aggregate explanation for UI status display.
    /// </summary>
    private static string BuildScanSummary(
        IReadOnlyList<TargetScanResult> targets,
        string? configurationError,
        bool isRunning,
        bool hasCompatibleBackup)
    {
        if (!string.IsNullOrWhiteSpace(configurationError))
        {
            return configurationError;
        }

        if (targets.Count == 0)
        {
            return "没有可扫描的 QQ 版本目标。";
        }

        var ready = targets.Count(target => target.State == TargetPatchState.ReadyToInstall);
        var installed = targets.Count(target => target.State == TargetPatchState.Installed);
        var legacy = targets.Count(target => target.State == TargetPatchState.LegacyInstalled);
        var blocked = targets.Count - ready - installed - legacy;
        var processDetail = isRunning ? " QQ 正在运行，写入操作已禁用。" : string.Empty;
        var backupDetail = hasCompatibleBackup ? " 已找到兼容备份。" : string.Empty;
        return $"目标 {targets.Count} 个：可安装 {ready}，已安装 {installed}，待升级 {legacy}，异常 {blocked}。{processDetail}{backupDetail}".Trim();
    }

    /// <summary>
    /// Deletes a known operation-owned temporary file without hiding primary operation errors.
    /// </summary>
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Temporary cleanup is best-effort after the durable target state has been decided.
        }
    }

    /// <summary>
    /// Deletes only the unique pending backup directory created by the current operation.
    /// </summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An incomplete .pending directory is ignored by backup discovery.
        }
    }

    private sealed record ConfiguredTarget(string Version, string FilePath, string RelativePath, bool IsValid);

    private sealed record ConfiguredTargetSet(IReadOnlyList<ConfiguredTarget> Targets, string? Error);

    private sealed record TargetSnapshot(ConfiguredTarget Target, TargetScanResult Result, byte[]? Content);

    private sealed record TargetSnapshotSet(IReadOnlyList<TargetSnapshot> Targets, string? Error);

    private sealed record ReplacementPlan(
        ConfiguredTarget Target,
        string ExpectedCurrentSha256,
        string NewSha256,
        byte[] NewContent);

    private sealed record StagedReplacement(ReplacementPlan Plan, string TemporaryPath, string RollbackPath)
    {
        public string? DisplacedSha256 { get; set; }
    }

    private sealed record BackupCandidate(string Directory, BackupManifest Manifest);

    private sealed record ManagedBackupCandidate(BackupCandidate Backup, long Size);

    private sealed record BackupCleanupAnalysis(
        IReadOnlyList<ManagedBackupCandidate> Removable,
        int RetainedBackupCount,
        int UnrecognizedDirectoryCount);

    private sealed record BackupManifest(
        int SchemaVersion,
        string BackupId,
        DateTimeOffset CreatedUtc,
        string InstallRoot,
        List<BackupTargetManifest> Targets);

    private sealed record BackupTargetManifest(
        string Version,
        string RelativePath,
        string BackupFileName,
        string OriginalSha256,
        string PatchedSha256);

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(BackupManifest))]
    private sealed partial class BackupManifestJsonContext : JsonSerializerContext;
}
