using System.Security.Cryptography;

namespace QQAntiRecall.Core;

/// <summary>
/// Detects and neutralises the QQNT.dll delayed integrity check that terminates patched QQ clients.
/// </summary>
public sealed class QqntIntegrityService : IQqntIntegrityService
{
    private const string BackupSuffix = ".qqantirecall-orig.bak";
    private static readonly SemaphoreSlim OperationGate = new(1, 1);

    private readonly IAntiRecallRuntime _runtime;

    /// <summary>
    /// Creates a service backed by the current operating system.
    /// </summary>
    public QqntIntegrityService()
        : this(new SystemAntiRecallRuntime())
    {
    }

    /// <summary>
    /// Creates a service with a controlled platform and process dependency for verification.
    /// </summary>
    /// <param name="runtime">Platform, process, and clock inputs.</param>
    internal QqntIntegrityService(IAntiRecallRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    /// <summary>
    /// Scans every installed QQNT.dll without modifying files.
    /// </summary>
    /// <param name="installRoot">QQ installation root that contains QQ.exe and versions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The verified integrity state of every discovered DLL.</returns>
    public Task<QqntIntegrityScanResult> ScanAsync(
        string installRoot,
        CancellationToken cancellationToken = default) =>
        ScanCoreAsync(installRoot, cancellationToken);

    /// <summary>
    /// Disables the delayed integrity check in every patchable QQNT.dll after saving original bytes.
    /// </summary>
    /// <param name="installRoot">QQ installation root that contains QQ.exe and versions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The refreshed scan and the operation message.</returns>
    public async Task<QqntIntegrityOperationResult> ApplyAsync(
        string installRoot,
        CancellationToken cancellationToken = default)
    {
        await OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var normalizedRoot = NormalizeInstallRoot(installRoot);
            var initialScan = await ScanCoreAsync(normalizedRoot, cancellationToken).ConfigureAwait(false);
            if (!initialScan.IsPlatformSupported)
            {
                return new QqntIntegrityOperationResult(initialScan, "安装已拒绝：当前平台不支持修改 QQ。");
            }

            if (initialScan.IsQqRunning)
            {
                return new QqntIntegrityOperationResult(initialScan, "安装已拒绝：请先完全退出 QQ。");
            }

            var patchable = initialScan.Targets
                .Where(target => target.State == QqntIntegrityState.ReadyToPatch)
                .ToArray();
            if (patchable.Length == 0)
            {
                var message = initialScan.Targets.Count == 0
                    ? "安装已拒绝：未找到可处理的 QQNT.dll。"
                    : "所有目标均已处理，无需重复操作。";
                return new QqntIntegrityOperationResult(initialScan, message);
            }

            var patchedCount = 0;
            try
            {
                foreach (var target in patchable)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PatchSingleTarget(target);
                    patchedCount++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException)
            {
                var refreshed = await ScanCoreAsync(normalizedRoot, cancellationToken).ConfigureAwait(false);
                return new QqntIntegrityOperationResult(
                    refreshed,
                    $"已处理 {patchedCount} 个文件后中断：{exception.Message}");
            }

            var result = await ScanCoreAsync(normalizedRoot, cancellationToken).ConfigureAwait(false);
            var succeeded = result.Targets.All(target => target.State != QqntIntegrityState.ReadyToPatch);
            return new QqntIntegrityOperationResult(
                result,
                succeeded
                    ? $"已关闭 {patchedCount} 个 QQNT.dll 的完整性自检，QQ 不会再因补丁而自动退出。"
                    : $"已处理 {patchedCount} 个文件，但复验发现仍有未关闭的自检点，请重新扫描确认。",
                succeeded);
        }
        finally
        {
            OperationGate.Release();
        }
    }

    /// <summary>
    /// Restores every patched QQNT.dll from the original bytes saved next to it.
    /// </summary>
    /// <param name="installRoot">QQ installation root that contains QQ.exe and versions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The refreshed scan and the operation message.</returns>
    public async Task<QqntIntegrityOperationResult> RestoreAsync(
        string installRoot,
        CancellationToken cancellationToken = default)
    {
        await OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var normalizedRoot = NormalizeInstallRoot(installRoot);
            var initialScan = await ScanCoreAsync(normalizedRoot, cancellationToken).ConfigureAwait(false);
            if (!initialScan.IsPlatformSupported)
            {
                return new QqntIntegrityOperationResult(initialScan, "恢复已拒绝：当前平台不支持修改 QQ。");
            }

            if (initialScan.IsQqRunning)
            {
                return new QqntIntegrityOperationResult(initialScan, "恢复已拒绝：请先完全退出 QQ。");
            }

            var restorable = initialScan.Targets
                .Where(target => target.State is QqntIntegrityState.Patched or QqntIntegrityState.SkipPatched)
                .ToArray();
            if (restorable.Length == 0)
            {
                return new QqntIntegrityOperationResult(initialScan, "所有目标均为原始状态，无需恢复。");
            }

            var withoutBackup = restorable.Where(target => !target.HasBackup).ToArray();
            if (withoutBackup.Length > 0)
            {
                return new QqntIntegrityOperationResult(
                    initialScan,
                    $"恢复已拒绝：{withoutBackup[0].FilePath} 缺少原始备份，无法还原。");
            }

            var restoredCount = 0;
            try
            {
                foreach (var target in restorable)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RestoreSingleTarget(target);
                    restoredCount++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException)
            {
                var refreshed = await ScanCoreAsync(normalizedRoot, cancellationToken).ConfigureAwait(false);
                return new QqntIntegrityOperationResult(
                    refreshed,
                    $"已恢复 {restoredCount} 个文件后中断：{exception.Message}");
            }

            var result = await ScanCoreAsync(normalizedRoot, cancellationToken).ConfigureAwait(false);
            var succeeded = !result.Targets.Any(target => IsPatched(target.State));
            return new QqntIntegrityOperationResult(
                result,
                succeeded
                    ? $"已从备份还原 {restoredCount} 个 QQNT.dll。"
                    : $"已还原 {restoredCount} 个文件，但复验仍检测到补丁字节，请重新扫描确认。",
                succeeded);
        }
        finally
        {
            OperationGate.Release();
        }
    }

    /// <summary>
    /// Builds the non-destructive scan snapshot for one installation root.
    /// </summary>
    /// <param name="installRoot">QQ installation root.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The scan result.</returns>
    private async Task<QqntIntegrityScanResult> ScanCoreAsync(
        string installRoot,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = NormalizeInstallRoot(installRoot);
        var isRunning = _runtime.IsQqRunning();
        if (!_runtime.IsWindows)
        {
            return new QqntIntegrityScanResult(
                normalizedRoot,
                IsPlatformSupported: false,
                isRunning,
                [],
                CanApply: false,
                CanRestore: false,
                "当前平台仅支持扫描界面；QQNT.dll 完整性自检补丁暂仅支持 Windows QQ。");
        }

        var targets = new List<QqntIntegrityTargetResult>();
        foreach (var dllPath in EnumerateTargets(normalizedRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            targets.Add(await AnalyzeTargetAsync(dllPath, cancellationToken).ConfigureAwait(false));
        }

        var anyPatched = targets.Any(target => IsPatched(target.State));
        var canApply = !isRunning && targets.Any(target => target.State == QqntIntegrityState.ReadyToPatch);
        var canRestore = !isRunning
            && anyPatched
            && targets.Where(target => IsPatched(target.State)).All(target => target.HasBackup);
        return new QqntIntegrityScanResult(
            normalizedRoot,
            IsPlatformSupported: true,
            isRunning,
            targets,
            canApply,
            canRestore,
            BuildSummary(targets, isRunning));
    }

    /// <summary>
    /// Measures and classifies one QQNT.dll without writing it.
    /// </summary>
    /// <param name="dllPath">Absolute DLL path.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The target result.</returns>
    private static async Task<QqntIntegrityTargetResult> AnalyzeTargetAsync(
        string dllPath,
        CancellationToken cancellationToken)
    {
        var version = Path.GetFileName(Path.GetDirectoryName(dllPath)) ?? string.Empty;
        var backupPath = dllPath + BackupSuffix;
        var hasBackup = File.Exists(backupPath);
        long fileSize = 0;
        try
        {
            var info = new FileInfo(dllPath);
            fileSize = info.Length;
            string sha256;
            await using (var hashStream = new FileStream(
                dllPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var hash = await SHA256.HashDataAsync(hashStream, cancellationToken).ConfigureAwait(false);
                sha256 = Convert.ToHexString(hash);
            }

            await using var stream = new FileStream(
                dllPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.RandomAccess);
            if (!QqntIntegrityLocator.TryLocate(stream, out var site, out var failure))
            {
                return new QqntIntegrityTargetResult(
                    version,
                    dllPath,
                    QqntIntegrityState.Unsupported,
                    fileSize,
                    sha256,
                    backupPath,
                    hasBackup,
                    failure.Replace("{file}", Path.GetFileName(dllPath), StringComparison.Ordinal));
            }

            var state = site!.State switch
            {
                QqntIntegrityByteState.SoftPatched => QqntIntegrityState.Patched,
                QqntIntegrityByteState.SkipPatched => QqntIntegrityState.SkipPatched,
                _ => QqntIntegrityState.ReadyToPatch,
            };
            return new QqntIntegrityTargetResult(
                version,
                dllPath,
                state,
                fileSize,
                sha256,
                backupPath,
                hasBackup,
                BuildTargetDetail(state, site, hasBackup));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new QqntIntegrityTargetResult(
                version,
                Path.GetFullPath(dllPath),
                QqntIntegrityState.Unreadable,
                fileSize,
                string.Empty,
                backupPath,
                hasBackup,
                $"无法分析该文件：{exception.Message}");
        }
    }

    /// <summary>
    /// Writes the soft patch into one prepared DLL after saving verified original bytes.
    /// </summary>
    /// <param name="target">Target reported as ready to patch.</param>
    private static void PatchSingleTarget(QqntIntegrityTargetResult target)
    {
        EnsureOriginalBackup(target.FilePath, target.BackupPath);

        using var stream = new FileStream(
            target.FilePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.RandomAccess);
        var site = QqntIntegrityLocator.Locate(stream, target.FilePath);
        if (site.State != QqntIntegrityByteState.Original)
        {
            throw new InvalidDataException("目标状态在写入前发生变化，已中止本次操作。");
        }

        var patched = QqntIntegrityCatalog.GetPatchedBytes(QqntIntegrityPatchVariant.Soft);
        stream.Position = site.JumpFileOffset;
        stream.Write(patched);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Replaces one patched DLL with the original bytes saved next to it.
    /// </summary>
    /// <param name="target">Target reported as patched.</param>
    private static void RestoreSingleTarget(QqntIntegrityTargetResult target)
    {
        if (!File.Exists(target.BackupPath))
        {
            throw new FileNotFoundException("缺少原始备份，无法还原。", target.BackupPath);
        }

        var directory = Path.GetDirectoryName(target.FilePath)
            ?? throw new InvalidDataException("目标路径无效。");
        var temporaryPath = Path.Combine(
            directory,
            Path.GetFileName(target.FilePath) + ".qqantirecall-restore.tmp");
        TryDeleteFile(temporaryPath);
        try
        {
            File.Copy(target.BackupPath, temporaryPath, overwrite: true);
            File.Replace(temporaryPath, target.FilePath, destinationBackupFileName: null);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    /// <summary>
    /// Copies the current DLL next to itself the first time, then verifies that the saved bytes are original.
    /// </summary>
    /// <param name="dllPath">Absolute DLL path.</param>
    /// <param name="backupPath">Backup path beside the DLL.</param>
    private static void EnsureOriginalBackup(string dllPath, string backupPath)
    {
        if (!File.Exists(backupPath))
        {
            var temporaryPath = backupPath + ".tmp";
            TryDeleteFile(temporaryPath);
            try
            {
                File.Copy(dllPath, temporaryPath, overwrite: true);
                File.Move(temporaryPath, backupPath);
            }
            finally
            {
                TryDeleteFile(temporaryPath);
            }
        }

        EnsureBackupIsOriginal(backupPath);
    }

    /// <summary>
    /// Confirms that a saved backup still holds the unpatched site bytes.
    /// </summary>
    /// <param name="backupPath">Backup path beside the DLL.</param>
    private static void EnsureBackupIsOriginal(string backupPath)
    {
        using var stream = new FileStream(
            backupPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.RandomAccess);
        var site = QqntIntegrityLocator.Locate(stream, backupPath);
        if (site.State != QqntIntegrityByteState.Original)
        {
            throw new InvalidDataException($"备份 {Path.GetFileName(backupPath)} 不是原始文件，已中止本次操作。");
        }
    }

    /// <summary>
    /// Enumerates every version directory that holds a QQNT.dll.
    /// </summary>
    /// <param name="installRoot">QQ installation root.</param>
    /// <returns>Absolute DLL paths in descending version order.</returns>
    private static IEnumerable<string> EnumerateTargets(string installRoot)
    {
        if (installRoot.Length == 0)
        {
            return [];
        }

        var versionsDirectory = Path.Combine(installRoot, "versions");
        if (!Directory.Exists(versionsDirectory))
        {
            return [];
        }

        return Directory.EnumerateDirectories(versionsDirectory)
            .Select(directory => Path.Combine(directory, "QQNT.dll"))
            .Where(File.Exists)
            .OrderByDescending(path => Path.GetFileName(Path.GetDirectoryName(path)), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Produces the per-target explanation shown beside the state badge.
    /// </summary>
    /// <param name="state">Detected state.</param>
    /// <param name="site">Located patch site.</param>
    /// <param name="hasBackup">Whether an original backup exists.</param>
    /// <returns>The detail text.</returns>
    private static string BuildTargetDetail(
        QqntIntegrityState state,
        QqntIntegritySite site,
        bool hasBackup) => state switch
        {
            QqntIntegrityState.ReadyToPatch =>
                $"校验点 0x{site.JumpVirtualAddress:X}，启动约 15 分钟后会因检测到补丁而退出 QQ。",
            QqntIntegrityState.Patched =>
                $"软补丁已生效（校验点 0x{site.JumpVirtualAddress:X}），失败时不再退出。备份：{(hasBackup ? "可用" : "缺失")}。",
            QqntIntegrityState.SkipPatched =>
                $"校验调用已被跳过（校验点 0x{site.SkipVirtualAddress:X}）。备份：{(hasBackup ? "可用" : "缺失")}。",
            QqntIntegrityState.Unsupported =>
                "未能在该文件中定位已知的完整性校验点，已保持原样。",
            _ => "无法读取该文件。",
        };

    /// <summary>
    /// Produces the summary shown under the section title.
    /// </summary>
    /// <param name="targets">Measured targets.</param>
    /// <param name="isRunning">Whether QQ currently runs.</param>
    /// <returns>The summary text.</returns>
    private static string BuildSummary(IReadOnlyList<QqntIntegrityTargetResult> targets, bool isRunning)
    {
        if (targets.Count == 0)
        {
            return "未在 versions 目录中找到 QQNT.dll。";
        }

        if (isRunning)
        {
            return "QQ 正在运行，请完全退出后修改 QQNT.dll。";
        }

        var patched = targets.Count(target => target.State is QqntIntegrityState.Patched
            or QqntIntegrityState.SkipPatched);
        var ready = targets.Count(target => target.State == QqntIntegrityState.ReadyToPatch);
        if (patched == targets.Count)
        {
            return "完整性自检已被关闭，QQ 不会因补丁自动退出。";
        }

        if (ready == targets.Count)
        {
            return "发现未处理的完整性自检，安装防撤回后需要一并关闭。";
        }

        return "部分目标状态不一致，请先确认后再操作。";
    }

    /// <summary>
    /// Reports whether a state means this tool has already neutralised the check.
    /// </summary>
    /// <param name="state">Detected state.</param>
    /// <returns><see langword="true"/> for both patched variants.</returns>
    private static bool IsPatched(QqntIntegrityState state) =>
        state is QqntIntegrityState.Patched or QqntIntegrityState.SkipPatched;

    /// <summary>
    /// Normalises a user-supplied installation root.
    /// </summary>
    /// <param name="installRoot">Raw path.</param>
    /// <returns>The normalised absolute path, or an empty string.</returns>
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
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Deletes a file, ignoring already-missing targets.
    /// </summary>
    /// <param name="path">Absolute path.</param>
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
