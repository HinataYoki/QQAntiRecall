using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace QQAntiRecall.Core.Tests;

public sealed class AntiRecallServiceTests
{
    /// <summary>
    /// Verifies wildcard positions match arbitrary bytes and all matching offsets are returned.
    /// </summary>
    [Fact]
    public void WildcardMatcher_ReturnsEveryUniqueOffset()
    {
        var content = new byte[] { 0xAA, 0x01, 0xCC, 0x00, 0xAA, 0xFE, 0xCC };

        var matches = WildcardPattern.FindAll(content, WildcardPattern.Parse("AA ?? CC"));

        Assert.Equal(new[] { 0, 4 }, matches);
    }

    /// <summary>
    /// Verifies exact, duplicate, partial, unknown, and missing binaries map to distinct safety states.
    /// </summary>
    [Fact]
    public async Task ScanAsync_ClassifiesSignatureSafetyStates()
    {
        using var workspace = new TestWorkspace();
        var targetPath = workspace.CreateInstall("9.9.1").Single();
        var service = workspace.CreateService();

        var ready = await service.ScanAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        Assert.Equal(TargetPatchState.ReadyToInstall, Assert.Single(ready.Targets).State);
        Assert.All(ready.Targets[0].Signatures.Zip(PatchCatalog.DefaultProfile.ExpectedMatchCounts), pair =>
        {
            Assert.Equal(pair.Second, pair.First.OriginalMatchCount);
            Assert.Equal(0, pair.First.PatchedMatchCount);
        });

        var duplicate = TestBinary.CreateOriginal();
        duplicate = [.. duplicate, .. TestBinary.Materialize(PatchCatalog.Definitions[0].OriginalPattern)];
        File.WriteAllBytes(targetPath, duplicate);
        var duplicateScan = await service.ScanAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        Assert.Equal(TargetPatchState.Inconsistent, Assert.Single(duplicateScan.Targets).State);
        Assert.Equal(
            PatchCatalog.DefaultProfile.ExpectedMatchCounts[0] + 1,
            duplicateScan.Targets[0].Signatures[0].OriginalMatchCount);

        File.WriteAllBytes(targetPath, TestBinary.CreateOriginal(PatchCatalog.Definitions.Take(2)));
        var partialScan = await service.ScanAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        Assert.Equal(TargetPatchState.Inconsistent, Assert.Single(partialScan.Targets).State);

        File.WriteAllBytes(targetPath, Enumerable.Repeat((byte)0xEE, 512).ToArray());
        var unsupportedScan = await service.ScanAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        Assert.Equal(TargetPatchState.Unsupported, Assert.Single(unsupportedScan.Targets).State);

        File.Delete(targetPath);
        var missingScan = await service.ScanAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        Assert.Equal(TargetPatchState.Missing, Assert.Single(missingScan.Targets).State);
    }

    /// <summary>
    /// Verifies every built-in code-layout profile completes the full install and restore lifecycle.
    /// </summary>
    [Fact]
    public async Task InstallAndRestoreAsync_AcceptsEveryBuiltInPatchProfile()
    {
        foreach (var profile in PatchCatalog.Profiles)
        {
            using var workspace = new TestWorkspace();
            var targetPath = workspace.CreateInstall(profile.Name).Single();
            File.WriteAllBytes(targetPath, TestBinary.CreateOriginal(profile: profile));
            var service = workspace.CreateService();

            var ready = await service.ScanAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
            var readyTarget = Assert.Single(ready.Targets);
            Assert.Equal(TargetPatchState.ReadyToInstall, readyTarget.State);
            Assert.Contains(profile.Name, readyTarget.Detail);
            Assert.Equal(profile.ExpectedMatchCounts, readyTarget.Signatures.Select(status => status.OriginalMatchCount));

            var installed = await service.InstallAsync(
                workspace.InstallRoot,
                TestContext.Current.CancellationToken);
            var installedTarget = Assert.Single(installed.Scan.Targets);
            Assert.True(installed.Succeeded);
            Assert.Equal(TargetPatchState.Installed, installedTarget.State);
            Assert.Contains(profile.Name, installedTarget.Detail);
            Assert.Equal(profile.ExpectedMatchCounts, installedTarget.Signatures.Select(status => status.PatchedMatchCount));

            var restored = await service.RestoreAsync(
                workspace.InstallRoot,
                TestContext.Current.CancellationToken);
            Assert.True(restored.Succeeded);
            Assert.Equal(TargetPatchState.ReadyToInstall, Assert.Single(restored.Scan.Targets).State);
        }
    }

    /// <summary>
    /// Verifies current and ready QQ targets can use different layouts in one transaction.
    /// </summary>
    [Fact]
    public async Task InstallAndRestoreAsync_AcceptsMixedProfilesAcrossConfiguredTargets()
    {
        using var workspace = new TestWorkspace();
        var targetPaths = workspace.CreateInstall("current", "ready");
        var selectedProfiles = PatchCatalog.Profiles.Take(targetPaths.Count).ToArray();
        Assert.Equal(targetPaths.Count, selectedProfiles.Length);
        for (var index = 0; index < targetPaths.Count; index++)
        {
            File.WriteAllBytes(targetPaths[index], TestBinary.CreateOriginal(profile: selectedProfiles[index]));
        }

        var service = workspace.CreateService();
        var installed = await service.InstallAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);

        Assert.True(installed.Succeeded);
        Assert.All(installed.Scan.Targets, target => Assert.Equal(TargetPatchState.Installed, target.State));
        Assert.Equal(
            selectedProfiles.Select(profile => profile.Name),
            installed.Scan.Targets.Select(target =>
                selectedProfiles.Single(profile => target.Detail.Contains(profile.Name, StringComparison.Ordinal)).Name));

        var restored = await service.RestoreAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        Assert.True(restored.Succeeded);
        Assert.All(restored.Scan.Targets, target => Assert.Equal(TargetPatchState.ReadyToInstall, target.State));
    }

    /// <summary>
    /// Verifies scanning is limited to unique curVersion and readyVersion config values.
    /// </summary>
    [Fact]
    public async Task ScanAsync_SelectsOnlyCurrentAndReadyVersions()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateInstall("current", "ready");
        workspace.CreateUnconfiguredVersion("ignored");
        var service = workspace.CreateService();

        var scan = await service.ScanAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "current", "ready" }, scan.Targets.Select(target => target.Version));

        workspace.WriteConfig("current", "current");
        var deduplicated = await service.ScanAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        Assert.Equal("current", Assert.Single(deduplicated.Targets).Version);
    }

    /// <summary>
    /// Verifies all configured files install together, produce a manifest, and restore byte-for-byte.
    /// </summary>
    [Fact]
    public async Task InstallAndRestoreAsync_RoundTripsAllConfiguredTargets()
    {
        using var workspace = new TestWorkspace();
        var targetPaths = workspace.CreateInstall("current", "ready");
        var originals = targetPaths.ToDictionary(path => path, File.ReadAllBytes);
        var service = workspace.CreateService();

        var installed = await service.InstallAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);

        Assert.True(installed.Succeeded);
        Assert.All(installed.Scan.Targets, target => Assert.Equal(TargetPatchState.Installed, target.State));
        Assert.True(installed.Scan.CanRestore);
        Assert.NotNull(installed.Scan.LatestBackupId);
        Assert.True(File.Exists(Path.Combine(
            workspace.BackupRoot,
            installed.Scan.LatestBackupId!,
            "manifest.json")));

        var restored = await service.RestoreAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);

        Assert.True(restored.Succeeded);
        Assert.All(restored.Scan.Targets, target => Assert.Equal(TargetPatchState.ReadyToInstall, target.State));
        Assert.False(restored.Scan.CanRestore);
        foreach (var targetPath in targetPaths)
        {
            Assert.Equal(originals[targetPath], File.ReadAllBytes(targetPath));
        }
    }

    /// <summary>
    /// Verifies a complete 0.0.1 installation upgrades from its exact backup and restores the local-recall code path.
    /// </summary>
    [Fact]
    public async Task InstallAsync_UpgradesLegacyPatchWithoutBlockingLocalRecall()
    {
        using var workspace = new TestWorkspace();
        var legacy = workspace.CreateLegacyInstalledWithBackup("current");
        var service = workspace.CreateService();

        var legacyScan = await service.ScanAsync(
            workspace.InstallRoot,
            TestContext.Current.CancellationToken);

        Assert.Equal(TargetPatchState.LegacyInstalled, Assert.Single(legacyScan.Targets).State);
        Assert.True(legacyScan.CanInstall);
        Assert.True(legacyScan.CanRestore);
        Assert.Equal(legacy.BackupId, legacyScan.LatestBackupId);

        var upgraded = await service.InstallAsync(
            workspace.InstallRoot,
            TestContext.Current.CancellationToken);

        Assert.True(upgraded.Succeeded);
        Assert.Contains("旧版补丁已升级", upgraded.Message);
        Assert.Equal(TargetPatchState.Installed, Assert.Single(upgraded.Scan.Targets).State);
        var upgradedBytes = File.ReadAllBytes(legacy.TargetPath);
        var legacyNormal = PatchCatalog.LegacyDefinitions[0];
        Assert.Single(WildcardPattern.FindAll(upgradedBytes, legacyNormal.OriginalPattern));
        Assert.Empty(WildcardPattern.FindAll(upgradedBytes, legacyNormal.PatchedPattern));
        Assert.All(PatchCatalog.Definitions.Take(3), definition =>
        {
            Assert.Empty(WildcardPattern.FindAll(upgradedBytes, definition.OriginalPattern));
            Assert.Equal(
                PatchCatalog.DefaultProfile.GetExpectedMatchCount(definition),
                WildcardPattern.FindAll(upgradedBytes, definition.PatchedPattern).Count);
        });

        var cleanup = await service.PreviewBackupCleanupAsync(
            workspace.InstallRoot,
            TestContext.Current.CancellationToken);
        Assert.Contains(legacy.BackupId, cleanup.BackupIds);
        Assert.Equal(1, cleanup.RetainedBackupCount);
        Assert.Equal(0, cleanup.UnrecognizedDirectoryCount);

        var restored = await service.RestoreAsync(
            workspace.InstallRoot,
            TestContext.Current.CancellationToken);

        Assert.True(restored.Succeeded);
        Assert.Equal(legacy.Original, File.ReadAllBytes(legacy.TargetPath));
    }

    /// <summary>
    /// Verifies normal-recall replacements are anchored to QQ's explicit server-notification flag.
    /// </summary>
    [Fact]
    public void PatchCatalog_NormalRecallDefinitionsRequireNotificationFlag()
    {
        byte?[] notificationFlag = [0xC6, 0x44, 0x24, 0x28, 0x01];
        byte?[] localOperationFlag = [0xC6, 0x44, 0x24, 0x28, 0x00];

        Assert.All(PatchCatalog.Definitions.Take(3), definition =>
        {
            var materialized = TestBinary.Materialize(definition.OriginalPattern);
            Assert.NotEmpty(WildcardPattern.FindAll(materialized, notificationFlag));
            Assert.Empty(WildcardPattern.FindAll(materialized, localOperationFlag));
            Assert.Equal(new byte[] { 0x90, 0x90, 0x90, 0x90, 0x90 }, definition.Replacement);
        });
    }

    /// <summary>
    /// Verifies a matching notification-call shape is rejected when it no longer targets QQ's normal-recall function.
    /// </summary>
    [Fact]
    public async Task ScanAsync_RejectsNormalRecallCallWithUnexpectedTarget()
    {
        using var workspace = new TestWorkspace();
        var targetPath = workspace.CreateInstall("current").Single();
        var content = File.ReadAllBytes(targetPath);
        var definition = PatchCatalog.Definitions[0];
        var match = Assert.Single(WildcardPattern.FindAll(content, definition.OriginalPattern));
        BinaryPrimitives.WriteInt32LittleEndian(
            content.AsSpan(match + definition.PatchOffset + 1, sizeof(int)),
            0);
        File.WriteAllBytes(targetPath, content);

        var scan = await workspace.CreateService().ScanAsync(
            workspace.InstallRoot,
            TestContext.Current.CancellationToken);

        Assert.Equal(TargetPatchState.Inconsistent, Assert.Single(scan.Targets).State);
        Assert.False(scan.CanInstall);
    }

    /// <summary>
    /// Verifies notification-call patches are not reported as complete while the legacy local-operation patch remains.
    /// </summary>
    [Fact]
    public async Task ScanAsync_RejectsCurrentCallsCombinedWithLegacyFunctionPatch()
    {
        using var workspace = new TestWorkspace();
        var targetPath = workspace.CreateInstall("current").Single();
        var service = workspace.CreateService();
        await service.InstallAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        var mixed = TestBinary.ApplyDefinitions(
            File.ReadAllBytes(targetPath),
            [PatchCatalog.LegacyDefinitions[0]],
            PatchCatalog.LegacyProfile);
        File.WriteAllBytes(targetPath, mixed);

        var scan = await service.ScanAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);

        Assert.Equal(TargetPatchState.Inconsistent, Assert.Single(scan.Targets).State);
        Assert.False(scan.CanInstall);
        Assert.False(scan.CanRestore);
    }

    /// <summary>
    /// Verifies repeated complete operations are no-ops and do not create redundant backups.
    /// </summary>
    [Fact]
    public async Task Operations_AreIdempotentForCompleteStates()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateInstall("current");
        var service = workspace.CreateService();
        await service.InstallAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        var backupCount = Directory.EnumerateDirectories(workspace.BackupRoot).Count();

        var repeatedInstall = await service.InstallAsync(
            workspace.InstallRoot,
            TestContext.Current.CancellationToken);

        Assert.Contains("无需重复", repeatedInstall.Message);
        Assert.Equal(backupCount, Directory.EnumerateDirectories(workspace.BackupRoot).Count());

        await service.RestoreAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        var repeatedRestore = await service.RestoreAsync(
            workspace.InstallRoot,
            TestContext.Current.CancellationToken);

        Assert.Contains("无需恢复", repeatedRestore.Message);
        Assert.Equal(backupCount, Directory.EnumerateDirectories(workspace.BackupRoot).Count());
    }

    /// <summary>
    /// Verifies cleanup removes only obsolete-version backups while preserving current recovery data and unknown directories.
    /// </summary>
    [Fact]
    public async Task BackupCleanup_RemovesObsoleteVersionAndPreservesCurrentBackup()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateInstall("old");
        var service = workspace.CreateService();
        var oldInstall = await service.InstallAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        var oldBackupId = oldInstall.Scan.LatestBackupId!;
        await service.RestoreAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);

        workspace.CreateUnconfiguredVersion("current");
        workspace.WriteConfig("current", null);
        workspace.Runtime.UtcNowValue = workspace.Runtime.UtcNowValue.AddMinutes(1);
        var currentInstall = await service.InstallAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        var currentBackupId = currentInstall.Scan.LatestBackupId!;
        var unknownDirectory = Path.Combine(workspace.BackupRoot, "user-content");
        Directory.CreateDirectory(unknownDirectory);
        File.WriteAllText(Path.Combine(unknownDirectory, "keep.txt"), "keep");

        var preview = await service.PreviewBackupCleanupAsync(
            workspace.InstallRoot,
            TestContext.Current.CancellationToken);

        Assert.Equal([oldBackupId], preview.BackupIds);
        Assert.True(preview.ReclaimableBytes > 0);
        Assert.Equal(1, preview.RetainedBackupCount);
        Assert.Equal(1, preview.UnrecognizedDirectoryCount);

        var result = await service.CleanupObsoleteBackupsAsync(
            workspace.InstallRoot,
            preview.BackupIds,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.DeletedBackupCount);
        Assert.False(Directory.Exists(Path.Combine(workspace.BackupRoot, oldBackupId)));
        Assert.True(Directory.Exists(Path.Combine(workspace.BackupRoot, currentBackupId)));
        Assert.True(File.Exists(Path.Combine(unknownDirectory, "keep.txt")));
        Assert.True((await service.ScanAsync(
            workspace.InstallRoot,
            TestContext.Current.CancellationToken)).CanRestore);
    }

    /// <summary>
    /// Verifies repeated restore-install cycles retain only the newest complete backup for identical hashes.
    /// </summary>
    [Fact]
    public async Task BackupCleanup_RemovesOlderExactDuplicateOnly()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateInstall("current");
        var service = workspace.CreateService();
        var firstInstall = await service.InstallAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        var firstBackupId = firstInstall.Scan.LatestBackupId!;
        await service.RestoreAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        workspace.Runtime.UtcNowValue = workspace.Runtime.UtcNowValue.AddMinutes(1);
        var secondInstall = await service.InstallAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        var secondBackupId = secondInstall.Scan.LatestBackupId!;

        var preview = await service.PreviewBackupCleanupAsync(
            workspace.InstallRoot,
            TestContext.Current.CancellationToken);

        Assert.Equal([firstBackupId], preview.BackupIds);
        Assert.Equal(1, preview.RetainedBackupCount);

        var result = await service.CleanupObsoleteBackupsAsync(
            workspace.InstallRoot,
            preview.BackupIds,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(workspace.BackupRoot, firstBackupId)));
        Assert.True(Directory.Exists(Path.Combine(workspace.BackupRoot, secondBackupId)));
        Assert.True((await service.ScanAsync(
            workspace.InstallRoot,
            TestContext.Current.CancellationToken)).CanRestore);
    }

    /// <summary>
    /// Verifies cleanup revalidation skips an approved backup when its version becomes current again after preview.
    /// </summary>
    [Fact]
    public async Task BackupCleanup_WhenTargetSetChangesAfterPreview_SkipsDeletion()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateInstall("old");
        var service = workspace.CreateService();
        var installed = await service.InstallAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        var backupId = installed.Scan.LatestBackupId!;
        await service.RestoreAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        workspace.CreateUnconfiguredVersion("new");
        workspace.WriteConfig("new", null);
        var preview = await service.PreviewBackupCleanupAsync(
            workspace.InstallRoot,
            TestContext.Current.CancellationToken);
        Assert.Equal([backupId], preview.BackupIds);

        workspace.WriteConfig("old", null);
        var result = await service.CleanupObsoleteBackupsAsync(
            workspace.InstallRoot,
            preview.BackupIds,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.DeletedBackupCount);
        Assert.Equal(1, result.SkippedBackupCount);
        Assert.True(Directory.Exists(Path.Combine(workspace.BackupRoot, backupId)));
    }

    /// <summary>
    /// Verifies mixed original and installed targets are never coerced in either direction.
    /// </summary>
    [Fact]
    public async Task Operations_RejectMixedTargetStates()
    {
        using var workspace = new TestWorkspace();
        var targetPaths = workspace.CreateInstall("current", "ready");
        var firstOriginal = File.ReadAllBytes(targetPaths[0]);
        var service = workspace.CreateService();
        await service.InstallAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        File.WriteAllBytes(targetPaths[0], firstOriginal);
        var before = targetPaths.ToDictionary(path => path, File.ReadAllBytes);

        var install = await service.InstallAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        var restore = await service.RestoreAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);

        Assert.Contains("混合状态", install.Message);
        Assert.Contains("混合状态", restore.Message);
        Assert.False(install.Succeeded);
        Assert.False(restore.Succeeded);
        foreach (var targetPath in targetPaths)
        {
            Assert.Equal(before[targetPath], File.ReadAllBytes(targetPath));
        }
    }

    /// <summary>
    /// Verifies a corrupted original backup disables restore and never overwrites the installed target.
    /// </summary>
    [Fact]
    public async Task RestoreAsync_RejectsCorruptedBackupHash()
    {
        using var workspace = new TestWorkspace();
        var targetPath = workspace.CreateInstall("current").Single();
        var service = workspace.CreateService();
        var installed = await service.InstallAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        var patchedBytes = File.ReadAllBytes(targetPath);
        var backupFile = Directory.EnumerateFiles(
            Path.Combine(workspace.BackupRoot, installed.Scan.LatestBackupId!, "files"),
            "*.wrapper.node").Single();
        File.WriteAllBytes(backupFile, [0x00, 0x01, 0x02]);

        var scan = await service.ScanAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        var restore = await service.RestoreAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);

        Assert.Equal(TargetPatchState.Installed, Assert.Single(scan.Targets).State);
        Assert.False(scan.CanRestore);
        Assert.Contains("拒绝", restore.Message);
        Assert.Equal(patchedBytes, File.ReadAllBytes(targetPath));
    }

    /// <summary>
    /// Verifies an otherwise recognizable patched file cannot restore after its live hash diverges.
    /// </summary>
    [Fact]
    public async Task RestoreAsync_RejectsChangedPatchedHash()
    {
        using var workspace = new TestWorkspace();
        var targetPath = workspace.CreateInstall("current").Single();
        var service = workspace.CreateService();
        await service.InstallAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        var changedBytes = File.ReadAllBytes(targetPath);
        changedBytes[0] ^= 0x01;
        File.WriteAllBytes(targetPath, changedBytes);

        var scan = await service.ScanAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        var restore = await service.RestoreAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);

        Assert.Equal(TargetPatchState.Installed, Assert.Single(scan.Targets).State);
        Assert.False(scan.CanRestore);
        Assert.Contains("拒绝", restore.Message);
        Assert.Equal(changedBytes, File.ReadAllBytes(targetPath));
    }

    /// <summary>
    /// Verifies QQ process detection disables both installation and restoration without changing bytes.
    /// </summary>
    [Fact]
    public async Task Operations_RefuseWhileQqIsRunning()
    {
        using var workspace = new TestWorkspace();
        var targetPath = workspace.CreateInstall("current").Single();
        var originalBytes = File.ReadAllBytes(targetPath);
        workspace.Runtime.QqRunning = true;
        var service = workspace.CreateService();

        var refusedInstall = await service.InstallAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);

        Assert.False(refusedInstall.Scan.CanInstall);
        Assert.Contains("退出 QQ", refusedInstall.Message);
        Assert.Equal(originalBytes, File.ReadAllBytes(targetPath));

        workspace.Runtime.QqRunning = false;
        await service.InstallAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        var patchedBytes = File.ReadAllBytes(targetPath);
        workspace.Runtime.QqRunning = true;

        var refusedRestore = await service.RestoreAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);

        Assert.Contains("退出 QQ", refusedRestore.Message);
        Assert.Equal(patchedBytes, File.ReadAllBytes(targetPath));
    }

    /// <summary>
    /// Verifies the automated running-QQ workflow closes QQ, installs transactionally, and restarts the verified executable.
    /// </summary>
    [Fact]
    public async Task CloseQqInstallAndRestartAsync_ClosesInstallsAndRestartsQq()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateInstall("current");
        workspace.Runtime.QqRunning = true;
        var service = workspace.CreateService();

        var result = await service.CloseQqInstallAndRestartAsync(
            workspace.InstallRoot,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(result.Scan.IsQqRunning);
        Assert.Equal(TargetPatchState.Installed, Assert.Single(result.Scan.Targets).State);
        Assert.Equal(1, workspace.Runtime.StopQqCallCount);
        Assert.Equal(1, workspace.Runtime.StartQqCallCount);
        Assert.Equal(
            Path.Combine(workspace.InstallRoot, "QQ.exe"),
            workspace.Runtime.StartedQqExecutablePath);
        Assert.Contains("重新启动", result.Message);
    }

    /// <summary>
    /// Verifies a failed QQ shutdown leaves every target untouched and never starts a second QQ instance.
    /// </summary>
    [Fact]
    public async Task CloseQqInstallAndRestartAsync_WhenShutdownFails_DoesNotModifyFiles()
    {
        using var workspace = new TestWorkspace();
        var targetPath = workspace.CreateInstall("current").Single();
        var originalBytes = File.ReadAllBytes(targetPath);
        workspace.Runtime.QqRunning = true;
        workspace.Runtime.StopQqSucceeds = false;
        var service = workspace.CreateService();

        var result = await service.CloseQqInstallAndRestartAsync(
            workspace.InstallRoot,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.True(result.Scan.IsQqRunning);
        Assert.Equal(originalBytes, File.ReadAllBytes(targetPath));
        Assert.Equal(1, workspace.Runtime.StopQqCallCount);
        Assert.Equal(0, workspace.Runtime.StartQqCallCount);
        Assert.Contains("无法完全关闭", result.Message);
    }

    /// <summary>
    /// Verifies an unverified installation root is rejected before any QQ process is closed.
    /// </summary>
    [Fact]
    public async Task CloseQqInstallAndRestartAsync_WhenInstallRootIsUnverified_DoesNotCloseQq()
    {
        using var workspace = new TestWorkspace();
        var targetPath = workspace.CreateInstall("current").Single();
        var originalBytes = File.ReadAllBytes(targetPath);
        File.Delete(Path.Combine(workspace.InstallRoot, "QQ.exe"));
        workspace.Runtime.QqRunning = true;
        var service = workspace.CreateService();

        var result = await service.CloseQqInstallAndRestartAsync(
            workspace.InstallRoot,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(originalBytes, File.ReadAllBytes(targetPath));
        Assert.Equal(0, workspace.Runtime.StopQqCallCount);
        Assert.Equal(0, workspace.Runtime.StartQqCallCount);
        Assert.Contains("不会关闭 QQ", result.Message);
    }

    /// <summary>
    /// Verifies QQ is restored even when transactional installation fails and rolls every target back.
    /// </summary>
    [Fact]
    public async Task CloseQqInstallAndRestartAsync_WhenInstallFails_StillRestartsQq()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateInstall("current", "ready");
        workspace.Runtime.QqRunning = true;
        var service = workspace.CreateService(new FailOnSecondReplacement());

        var result = await service.CloseQqInstallAndRestartAsync(
            workspace.InstallRoot,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.True(result.Scan.IsQqRunning);
        Assert.All(result.Scan.Targets, target => Assert.Equal(TargetPatchState.ReadyToInstall, target.State));
        Assert.Equal(1, workspace.Runtime.StartQqCallCount);
        Assert.Contains("已回滚", result.Message);
        Assert.Contains("重新启动", result.Message);
    }

    /// <summary>
    /// Verifies a restart failure reports partial completion while preserving the successfully installed target.
    /// </summary>
    [Fact]
    public async Task CloseQqInstallAndRestartAsync_WhenRestartFails_ReportsPartialCompletion()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateInstall("current");
        workspace.Runtime.QqRunning = true;
        workspace.Runtime.StartQqSucceeds = false;
        var service = workspace.CreateService();

        var result = await service.CloseQqInstallAndRestartAsync(
            workspace.InstallRoot,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.False(result.Scan.IsQqRunning);
        Assert.Equal(TargetPatchState.Installed, Assert.Single(result.Scan.Targets).State);
        Assert.Equal(1, workspace.Runtime.StartQqCallCount);
        Assert.Contains("未能自动重启", result.Message);
    }

    /// <summary>
    /// Verifies non-Windows environments expose scan status but reject every write operation.
    /// </summary>
    [Fact]
    public async Task Operations_RejectUnsupportedPlatform()
    {
        using var workspace = new TestWorkspace();
        workspace.Runtime.PlatformIsWindows = false;
        var service = workspace.CreateService();

        var scan = await service.ScanAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        var install = await service.InstallAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);
        var restore = await service.RestoreAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);

        Assert.False(scan.IsPlatformSupported);
        Assert.Empty(scan.Targets);
        Assert.Contains("当前平台", install.Message);
        Assert.Contains("当前平台", restore.Message);
    }

    /// <summary>
    /// Verifies a failure replacing the second target atomically rolls the first target back.
    /// </summary>
    [Fact]
    public async Task InstallAsync_RollsBackEarlierTargetsWhenLaterReplacementFails()
    {
        using var workspace = new TestWorkspace();
        var targetPaths = workspace.CreateInstall("current", "ready");
        var originals = targetPaths.ToDictionary(path => path, File.ReadAllBytes);
        var service = workspace.CreateService(new FailOnSecondReplacement());

        var result = await service.InstallAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);

        Assert.Contains("已回滚", result.Message);
        Assert.False(result.Succeeded);
        Assert.All(result.Scan.Targets, target => Assert.Equal(TargetPatchState.ReadyToInstall, target.State));
        foreach (var targetPath in targetPaths)
        {
            Assert.Equal(originals[targetPath], File.ReadAllBytes(targetPath));
        }
    }

    /// <summary>
    /// Verifies an original rollback file remains recoverable when automatic rollback itself fails.
    /// </summary>
    [Fact]
    public async Task InstallAsync_PreservesRollbackFileWhenRollbackFails()
    {
        using var workspace = new TestWorkspace();
        var targetPaths = workspace.CreateInstall("current", "ready");
        var firstOriginal = File.ReadAllBytes(targetPaths[0]);
        var service = workspace.CreateService(new FailSecondReplacementAndRollback());

        await Assert.ThrowsAsync<IOException>(() =>
            service.InstallAsync(workspace.InstallRoot, TestContext.Current.CancellationToken));

        var rollbackPath = Assert.Single(Directory.EnumerateFiles(
            Path.GetDirectoryName(targetPaths[0])!,
            "*.rollback"));
        Assert.Equal(firstOriginal, File.ReadAllBytes(rollbackPath));
    }

    /// <summary>
    /// Verifies a target swapped after preflight is detected from its displaced hash and restored unchanged.
    /// </summary>
    [Fact]
    public async Task InstallAsync_RollsBackTargetSwappedImmediatelyBeforeReplacement()
    {
        using var workspace = new TestWorkspace();
        var targetPath = workspace.CreateInstall("current").Single();
        var unexpectedBytes = Enumerable.Repeat((byte)0x5A, 256).ToArray();
        var service = workspace.CreateService(new SwapDestinationBeforeFirstReplacement(unexpectedBytes));

        var result = await service.InstallAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("已回滚", result.Message);
        Assert.Equal(unexpectedBytes, File.ReadAllBytes(targetPath));
    }

    /// <summary>
    /// Verifies discovery ignores unverified candidates and returns a root containing QQ.exe and config.json.
    /// </summary>
    [Fact]
    public void FindInstallRoot_ReturnsFirstVerifiedCandidate()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateInstall("current");
        workspace.Runtime.Candidates.Add(Path.Combine(workspace.Root, "invalid"));
        workspace.Runtime.Candidates.Add(workspace.InstallRoot);
        var service = workspace.CreateService();

        var discovered = service.FindInstallRoot();

        Assert.Equal(Path.GetFullPath(workspace.InstallRoot), discovered);
    }
}

/// <summary>
/// Owns an isolated synthetic QQ installation and backup root for one test.
/// </summary>
internal sealed class TestWorkspace : IDisposable
{
    /// <summary>
    /// Creates unique directories on the current test volume.
    /// </summary>
    internal TestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "QQAntiRecall.Core.Tests", Guid.NewGuid().ToString("N"));
        InstallRoot = Path.Combine(Root, "QQNT");
        BackupRoot = Path.Combine(Root, "backups");
        Runtime = new TestRuntime(Path.Combine(Root, "local-app-data"));
        Directory.CreateDirectory(InstallRoot);
    }

    internal string Root { get; }

    internal string InstallRoot { get; }

    internal string BackupRoot { get; }

    internal TestRuntime Runtime { get; }

    /// <summary>
    /// Creates a service bound to this test's controlled runtime and backup root.
    /// </summary>
    internal AntiRecallService CreateService(IAtomicFileReplacer? replacer = null)
    {
        return new AntiRecallService(Runtime, BackupRoot, replacer);
    }

    /// <summary>
    /// Creates QQ.exe, config.json, and an original synthetic wrapper.node for every supplied version.
    /// </summary>
    internal IReadOnlyList<string> CreateInstall(params string[] versions)
    {
        Directory.CreateDirectory(InstallRoot);
        File.WriteAllBytes(Path.Combine(InstallRoot, "QQ.exe"), []);
        WriteConfig(versions.ElementAtOrDefault(0), versions.ElementAtOrDefault(1));

        var targetPaths = new List<string>();
        foreach (var version in versions.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            targetPaths.Add(CreateUnconfiguredVersion(version));
        }

        return targetPaths;
    }

    /// <summary>
    /// Creates one version directory without changing config.json.
    /// </summary>
    internal string CreateUnconfiguredVersion(string version)
    {
        var appDirectory = Path.Combine(InstallRoot, "versions", version, "resources", "app");
        Directory.CreateDirectory(appDirectory);
        var targetPath = Path.Combine(appDirectory, "wrapper.node");
        File.WriteAllBytes(targetPath, TestBinary.CreateOriginal());
        return targetPath;
    }

    /// <summary>
    /// Creates a complete 0.0.1 patched target and the exact original backup required for migration tests.
    /// </summary>
    internal (string TargetPath, byte[] Original, string BackupId) CreateLegacyInstalledWithBackup(string version)
    {
        var targetPath = CreateInstall(version).Single();
        var original = File.ReadAllBytes(targetPath);
        var patched = TestBinary.ApplyDefinitions(
            original,
            PatchCatalog.LegacyDefinitions,
            PatchCatalog.LegacyProfile);
        File.WriteAllBytes(targetPath, patched);

        const string backupId = "legacy-0.0.1-backup";
        var backupDirectory = Path.Combine(BackupRoot, backupId);
        var filesDirectory = Path.Combine(backupDirectory, "files");
        Directory.CreateDirectory(filesDirectory);
        File.WriteAllBytes(Path.Combine(filesDirectory, "000.wrapper.node"), original);
        var manifest = new
        {
            SchemaVersion = 1,
            BackupId = backupId,
            CreatedUtc = Runtime.UtcNowValue.AddMinutes(-1),
            InstallRoot,
            Targets = new[]
            {
                new
                {
                    Version = version,
                    RelativePath = Path.GetRelativePath(InstallRoot, targetPath).Replace('\\', '/'),
                    BackupFileName = "files/000.wrapper.node",
                    OriginalSha256 = Convert.ToHexString(SHA256.HashData(original)),
                    PatchedSha256 = Convert.ToHexString(SHA256.HashData(patched)),
                },
            },
        };
        File.WriteAllText(
            Path.Combine(backupDirectory, "manifest.json"),
            JsonSerializer.Serialize(manifest));

        return (targetPath, original, backupId);
    }

    /// <summary>
    /// Writes current and ready version properties exactly as QQ's version config does.
    /// </summary>
    internal void WriteConfig(string? currentVersion, string? readyVersion)
    {
        var versionsDirectory = Path.Combine(InstallRoot, "versions");
        Directory.CreateDirectory(versionsDirectory);
        var json = JsonSerializer.Serialize(new
        {
            curVersion = currentVersion,
            readyVersion,
            previousVersion = "must-not-be-scanned",
        });
        File.WriteAllText(Path.Combine(versionsDirectory, "config.json"), json);
    }

    /// <summary>
    /// Removes only this test's generated unique root.
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

/// <summary>
/// Generates deterministic binaries containing only the requested independent signatures.
/// </summary>
internal static class TestBinary
{
    /// <summary>
    /// Creates an original-state file with padding around each requested signature.
    /// </summary>
    internal static byte[] CreateOriginal(
        IEnumerable<PatchDefinition>? definitions = null,
        PatchProfile? profile = null)
    {
        profile ??= PatchCatalog.DefaultProfile;
        var selectedDefinitions = (definitions ?? PatchCatalog.Definitions).ToArray();
        var bytes = new List<byte>(Enumerable.Repeat((byte)0xCC, 32));
        foreach (var definition in selectedDefinitions)
        {
            for (var match = 0; match < profile.GetExpectedMatchCount(definition); match++)
            {
                bytes.AddRange(Materialize(definition.OriginalPattern));
                bytes.AddRange(Enumerable.Repeat((byte)0xCC, 32));
            }
        }

        bytes.AddRange(Materialize(PatchCatalog.LegacyDefinitions[0].OriginalPattern));
        bytes.AddRange(Enumerable.Repeat((byte)0xCC, 32));
        var content = bytes.ToArray();
        var normalRecallSignature = Assert.Single(WildcardPattern.FindAll(
            content,
            PatchCatalog.LegacyDefinitions[0].OriginalPattern));
        var normalRecallFunction = normalRecallSignature - 0x26;
        foreach (var definition in selectedDefinitions.Intersect(PatchCatalog.Definitions.Take(3)))
        {
            foreach (var match in WildcardPattern.FindAll(content, definition.OriginalPattern))
            {
                var callOffset = match + definition.PatchOffset;
                BinaryPrimitives.WriteInt32LittleEndian(
                    content.AsSpan(callOffset + 1, sizeof(int)),
                    normalRecallFunction - (callOffset + 5));
            }
        }

        return content;
    }

    /// <summary>
    /// Replaces wildcard positions with stable non-significant bytes.
    /// </summary>
    internal static byte[] Materialize(IReadOnlyList<byte?> pattern)
    {
        return pattern.Select((value, index) => value ?? (byte)(0xA0 + (index % 31))).ToArray();
    }

    /// <summary>
    /// Applies selected definitions at the exact counts declared by their owning profile.
    /// </summary>
    internal static byte[] ApplyDefinitions(
        byte[] source,
        IEnumerable<PatchDefinition> definitions,
        PatchProfile profile)
    {
        var patched = (byte[])source.Clone();
        foreach (var definition in definitions)
        {
            var expectedCount = profile.GetExpectedMatchCount(definition);
            var matches = WildcardPattern.FindAll(patched, definition.OriginalPattern);
            if (matches.Count != expectedCount)
            {
                throw new InvalidDataException(
                    $"Expected {expectedCount} matches for {definition.Name}, found {matches.Count}.");
            }

            foreach (var match in matches)
            {
                definition.Replacement.CopyTo(patched, match + definition.PatchOffset);
            }
        }

        return patched;
    }
}

/// <summary>
/// Supplies deterministic platform, process, discovery, and clock values to tests.
/// </summary>
internal sealed class TestRuntime : IAntiRecallRuntime
{
    /// <summary>
    /// Creates a Windows-like runtime rooted at an isolated local application data path.
    /// </summary>
    internal TestRuntime(string localApplicationDataPath)
    {
        LocalApplicationDataPath = localApplicationDataPath;
    }

    /// <summary>
    /// Controls the platform support value returned to the service.
    /// </summary>
    internal bool PlatformIsWindows { get; set; } = true;

    /// <summary>
    /// Controls whether write operations observe an active QQ process.
    /// </summary>
    internal bool QqRunning { get; set; }

    /// <summary>
    /// Controls whether the simulated process shutdown completes successfully.
    /// </summary>
    internal bool StopQqSucceeds { get; set; } = true;

    /// <summary>
    /// Controls whether the simulated QQ launch completes successfully.
    /// </summary>
    internal bool StartQqSucceeds { get; set; } = true;

    /// <summary>
    /// Counts automated QQ shutdown attempts.
    /// </summary>
    internal int StopQqCallCount { get; private set; }

    /// <summary>
    /// Counts automated QQ launch attempts.
    /// </summary>
    internal int StartQqCallCount { get; private set; }

    /// <summary>
    /// Captures the executable path supplied to the simulated QQ launch.
    /// </summary>
    internal string? StartedQqExecutablePath { get; private set; }

    /// <summary>
    /// Holds discovery candidates in their expected priority order.
    /// </summary>
    internal List<string> Candidates { get; } = [];

    /// <summary>
    /// Returns the controlled platform support value.
    /// </summary>
    public bool IsWindows => PlatformIsWindows;

    /// <summary>
    /// Returns the isolated local application data directory for this test.
    /// </summary>
    public string LocalApplicationDataPath { get; }

    /// <summary>
    /// Returns a stable clock value so backup manifests are deterministic.
    /// </summary>
    public DateTimeOffset UtcNow => UtcNowValue;

    /// <summary>
    /// Controls backup timestamps so tests can deterministically order repeated installations.
    /// </summary>
    internal DateTimeOffset UtcNowValue { get; set; } = new(2026, 7, 26, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Returns the process state controlled by the current test.
    /// </summary>
    public bool IsQqRunning()
    {
        return QqRunning;
    }

    /// <summary>
    /// Applies the configured shutdown outcome and clears the simulated running state on success.
    /// </summary>
    public Task<bool> StopQqAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopQqCallCount++;
        if (StopQqSucceeds)
        {
            QqRunning = false;
        }

        return Task.FromResult(StopQqSucceeds);
    }

    /// <summary>
    /// Records the verified executable path and marks the simulated QQ process as running.
    /// </summary>
    public void StartQq(string executablePath)
    {
        StartQqCallCount++;
        if (!StartQqSucceeds)
        {
            throw new InvalidOperationException("Injected QQ restart failure.");
        }

        StartedQqExecutablePath = executablePath;
        QqRunning = true;
    }

    /// <summary>
    /// Returns discovery candidates in their test-defined priority order.
    /// </summary>
    public IEnumerable<string> EnumerateInstallCandidates()
    {
        return Candidates;
    }
}

/// <summary>
/// Injects one deterministic replacement failure after the first target has committed.
/// </summary>
internal sealed class FailOnSecondReplacement : IAtomicFileReplacer
{
    private readonly SystemAtomicFileReplacer _inner = new();
    private int _replacementCount;

    /// <summary>
    /// Throws before the second replacement and delegates every other call to the real atomic primitive.
    /// </summary>
    public void Replace(string source, string destination, string rollbackPath)
    {
        _replacementCount++;
        if (_replacementCount == 2)
        {
            throw new IOException("Injected second-target failure.");
        }

        _inner.Replace(source, destination, rollbackPath);
    }
}

/// <summary>
/// Injects a second-target failure followed by a rollback failure for recovery-file verification.
/// </summary>
internal sealed class FailSecondReplacementAndRollback : IAtomicFileReplacer
{
    private readonly SystemAtomicFileReplacer _inner = new();
    private int _replacementCount;

    /// <summary>
    /// Commits the first target, then rejects both the second commit and automatic rollback.
    /// </summary>
    public void Replace(string source, string destination, string rollbackPath)
    {
        _replacementCount++;
        if (_replacementCount >= 2)
        {
            throw new IOException("Injected replacement or rollback failure.");
        }

        _inner.Replace(source, destination, rollbackPath);
    }
}

/// <summary>
/// Simulates a target replacement race by changing the destination after the service's live hash check.
/// </summary>
internal sealed class SwapDestinationBeforeFirstReplacement : IAtomicFileReplacer
{
    private readonly SystemAtomicFileReplacer _inner = new();
    private readonly byte[] _unexpectedBytes;
    private bool _hasSwapped;

    /// <summary>
    /// Captures the unexpected bytes used to replace the first destination during commit.
    /// </summary>
    internal SwapDestinationBeforeFirstReplacement(byte[] unexpectedBytes)
    {
        _unexpectedBytes = unexpectedBytes;
    }

    /// <summary>
    /// Swaps the first destination immediately before delegating to the real atomic replacement primitive.
    /// </summary>
    public void Replace(string source, string destination, string rollbackPath)
    {
        if (!_hasSwapped)
        {
            File.WriteAllBytes(destination, _unexpectedBytes);
            _hasSwapped = true;
        }

        _inner.Replace(source, destination, rollbackPath);
    }
}
