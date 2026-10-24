using System.Buffers.Binary;

namespace QQAntiRecall.Core.Tests;

/// <summary>
/// Verifies site location, byte-state classification, and the transactional apply and restore workflow.
/// </summary>
public sealed class QqntIntegrityServiceTests
{
    /// <summary>
    /// Verifies the locator converts the anchor to both a file offset and the matching virtual address.
    /// </summary>
    [Fact]
    public void TryLocate_OnOriginalImage_ReportsBothAddresses()
    {
        using var stream = new MemoryStream(QqntTestImage.Create());

        Assert.True(QqntIntegrityLocator.TryLocate(stream, out var site, out var failure), failure);
        Assert.NotNull(site);
        Assert.Equal(QqntTestImage.OffsetOf(QqntTestImage.AnchorVirtualAddress) + 19, site.JumpFileOffset);
        Assert.Equal(QqntIntegrityByteState.Original, site.State);
        Assert.Equal(QqntTestImage.AnchorVirtualAddress + 10, site.SkipVirtualAddress);
        Assert.Equal(QqntTestImage.AnchorVirtualAddress + 19, site.JumpVirtualAddress);
    }

    /// <summary>
    /// Verifies an already patched image is still recognised so the tool never rewrites it as unknown.
    /// </summary>
    /// <param name="softPatched">Whether the failure jump is already disabled.</param>
    /// <param name="skipPatched">Whether the delayed call is already removed.</param>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void TryLocate_OnPatchedImage_KeepsIdentifyingTheSite(bool softPatched, bool skipPatched)
    {
        var image = QqntTestImage.Create(original: !softPatched, skipPatched: skipPatched);
        using var stream = new MemoryStream(image);

        Assert.True(QqntIntegrityLocator.TryLocate(stream, out var site, out var failure), failure);
        Assert.Equal(
            skipPatched ? QqntIntegrityByteState.SkipPatched : QqntIntegrityByteState.SoftPatched,
            site!.State);
    }

    /// <summary>
    /// Verifies a lookalike anchor whose call does not reach the verification function is rejected.
    /// </summary>
    [Fact]
    public void TryLocate_WhenCallDoesNotReachVerificationFunction_RejectsAnchor()
    {
        using var stream = new MemoryStream(QqntTestImage.Create(withPrologue: false));

        Assert.False(QqntIntegrityLocator.TryLocate(stream, out var site, out var failure));
        Assert.Null(site);
        Assert.Contains("未找到可识别的完整性校验点", failure);
    }

    /// <summary>
    /// Verifies duplicate candidates are reported instead of silently patching the first match.
    /// </summary>
    [Fact]
    public void TryLocate_WhenTwoAnchorsExist_ReportsAmbiguity()
    {
        using var stream = new MemoryStream(QqntTestImage.Create(anchorCount: 2));

        Assert.False(QqntIntegrityLocator.TryLocate(stream, out _, out var failure));
        Assert.Contains("无法唯一定位", failure);
    }

    /// <summary>
    /// Verifies classification covers every state this tool can write.
    /// </summary>
    [Fact]
    public void ClassifyBytes_RecognisesOriginalSoftAndSkipStates()
    {
        Assert.Equal(
            QqntIntegrityByteState.Original,
            Classify(QqntTestImage.Create()));
        Assert.Equal(
            QqntIntegrityByteState.SoftPatched,
            Classify(QqntTestImage.Create(original: false)));
        Assert.Equal(
            QqntIntegrityByteState.SkipPatched,
            Classify(QqntTestImage.Create(skipPatched: true)));
        var foreign = QqntTestImage.Create(skipPatched: true);
        foreign[(int)QqntTestImage.OffsetOf(QqntTestImage.AnchorVirtualAddress) + 13] = 0x11;
        Assert.Equal(QqntIntegrityByteState.Unknown, Classify(foreign));
    }

    /// <summary>
    /// Verifies scanning never writes and reports the untouched site as patchable.
    /// </summary>
    [Fact]
    public async Task ScanAsync_WithoutWriting_ReportsReadyToPatch()
    {
        using var workspace = new QqntTestWorkspace();
        var installer = workspace.CreateInstaller("9.9.37-54276");
        var service = workspace.CreateService();

        var result = await service.ScanAsync(installer.Root, TestContext.Current.CancellationToken);

        var target = Assert.Single(result.Targets);
        Assert.Equal(QqntIntegrityState.ReadyToPatch, target.State);
        Assert.False(target.HasBackup);
        Assert.Equal(new FileInfo(target.FilePath).Length, target.FileSize);
        Assert.True(result.CanApply);
        Assert.False(result.CanRestore);
        Assert.Equal(installer.Bytes, await File.ReadAllBytesAsync(target.FilePath, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies apply rewrites exactly the two failure-jump bytes and keeps a verifiable original backup.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_WritesOnlyTheFailureJumpAndKeepsBackup()
    {
        using var workspace = new QqntTestWorkspace();
        var installer = workspace.CreateInstaller("9.9.37-54276");
        var service = workspace.CreateService();

        var result = await service.ApplyAsync(installer.Root, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Message);
        var target = Assert.Single(result.Scan.Targets);
        Assert.Equal(QqntIntegrityState.Patched, target.State);
        Assert.True(target.HasBackup);
        Assert.True(result.Scan.CanRestore);
        Assert.False(result.Scan.CanApply);

        var patched = await File.ReadAllBytesAsync(target.FilePath, TestContext.Current.CancellationToken);
        Assert.Equal(installer.Bytes, await File.ReadAllBytesAsync(target.BackupPath, TestContext.Current.CancellationToken));
        int[] changed = Enumerable.Range(0, patched.Length)
            .Where(index => patched[index] != installer.Bytes[index])
            .ToArray();
        int[] expected = [
            (int)QqntTestImage.OffsetOf(QqntTestImage.AnchorVirtualAddress) + 19,
            (int)QqntTestImage.OffsetOf(QqntTestImage.AnchorVirtualAddress) + 20,
        ];
        Assert.Equal(expected, changed);
        Assert.Equal(0x90, patched[changed[0]]);
        Assert.Equal(0x90, patched[changed[1]]);
    }

    /// <summary>
    /// Verifies a running QQ blocks both apply and restore without touching any byte.
    /// </summary>
    [Fact]
    public async Task ApplyAndRestore_WhenQqRuns_RefuseWithoutWriting()
    {
        using var workspace = new QqntTestWorkspace();
        var installer = workspace.CreateInstaller("9.9.37-54276");
        workspace.Runtime.QqRunning = true;
        var service = workspace.CreateService();

        var apply = await service.ApplyAsync(installer.Root, TestContext.Current.CancellationToken);
        var restore = await service.RestoreAsync(installer.Root, TestContext.Current.CancellationToken);

        Assert.False(apply.Succeeded);
        Assert.Contains("请先完全退出 QQ", apply.Message);
        Assert.False(restore.Succeeded);
        Assert.Contains("请先完全退出 QQ", restore.Message);
        Assert.Equal(installer.Bytes, await File.ReadAllBytesAsync(installer.FilePath, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies a second apply is a no-op instead of rewriting a patched file.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_WhenAlreadyPatched_IsIdempotent()
    {
        using var workspace = new QqntTestWorkspace();
        var installer = workspace.CreateInstaller("9.9.37-54276");
        var service = workspace.CreateService();
        await service.ApplyAsync(installer.Root, TestContext.Current.CancellationToken);
        var afterFirst = await File.ReadAllBytesAsync(installer.FilePath, TestContext.Current.CancellationToken);

        var second = await service.ApplyAsync(installer.Root, TestContext.Current.CancellationToken);

        Assert.False(second.Succeeded);
        Assert.Contains("无需重复操作", second.Message);
        Assert.Equal(afterFirst, await File.ReadAllBytesAsync(installer.FilePath, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies restore returns the exact original bytes and leaves the site patchable again.
    /// </summary>
    [Fact]
    public async Task RestoreAsync_ReturnsExactOriginalBytes()
    {
        using var workspace = new QqntTestWorkspace();
        var installer = workspace.CreateInstaller("9.9.37-54276");
        var service = workspace.CreateService();
        await service.ApplyAsync(installer.Root, TestContext.Current.CancellationToken);

        var result = await service.RestoreAsync(installer.Root, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(QqntIntegrityState.ReadyToPatch, Assert.Single(result.Scan.Targets).State);
        Assert.Equal(installer.Bytes, await File.ReadAllBytesAsync(installer.FilePath, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies every version directory is handled, including an unsupported lookalike that must stay untouched.
    /// </summary>
    [Fact]
    public async Task ScanAsync_WithUnsupportedLookalike_KeepsItOutOfTheOperation()
    {
        using var workspace = new QqntTestWorkspace();
        var installer = workspace.CreateInstaller("9.9.37-54276");
        var lookalikeBytes = QqntTestImage.Create(withPrologue: false);
        var lookalike = workspace.CreateVersion("9.9.38-60000", lookalikeBytes);
        var service = workspace.CreateService();

        var result = await service.ScanAsync(installer.Root, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Targets.Count);
        Assert.Contains(result.Targets, target => target.State == QqntIntegrityState.Unsupported);
        Assert.True(result.CanApply);

        var applied = await service.ApplyAsync(installer.Root, TestContext.Current.CancellationToken);

        Assert.True(applied.Succeeded, applied.Message);
        Assert.Equal(lookalikeBytes, await File.ReadAllBytesAsync(lookalike, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Verifies a non-Windows runtime reports an unsupported scan without listing targets.
    /// </summary>
    [Fact]
    public async Task ScanAsync_WhenPlatformIsUnsupported_ReportsUnsupported()
    {
        using var workspace = new QqntTestWorkspace();
        workspace.CreateInstaller("9.9.37-54276");
        workspace.Runtime.PlatformIsWindows = false;
        var service = workspace.CreateService();

        var result = await service.ScanAsync(workspace.InstallRoot, TestContext.Current.CancellationToken);

        Assert.False(result.IsPlatformSupported);
        Assert.Empty(result.Targets);
        Assert.False(result.CanApply);
        Assert.False(result.CanRestore);
    }

    /// <summary>
    /// Classifies the anchor window of a synthetic image.
    /// </summary>
    /// <param name="image">Synthetic image.</param>
    /// <returns>The detected byte state.</returns>
    private static QqntIntegrityByteState Classify(byte[] image)
    {
        var offset = (int)QqntTestImage.OffsetOf(QqntTestImage.AnchorVirtualAddress);
        return QqntIntegrityCatalog.ClassifyBytes(image.AsSpan(offset, QqntIntegrityCatalog.WindowLength));
    }
}
