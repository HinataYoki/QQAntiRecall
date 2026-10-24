using System.Buffers.Binary;

namespace QQAntiRecall.Core;

/// <summary>
/// Identifies one located integrity-check patch site inside a QQNT.dll image.
/// </summary>
internal sealed record QqntIntegritySite(
    long SkipFileOffset,
    long SkipVirtualAddress,
    long JumpFileOffset,
    long JumpVirtualAddress,
    QqntIntegrityByteState State);

/// <summary>
/// Finds the delayed integrity-check site without relying on version-specific hard-coded offsets.
/// </summary>
internal static class QqntIntegrityLocator
{
    private const int ChunkSize = 4 * 1024 * 1024;

    /// <summary>
    /// Locates the unique integrity-check patch site in a QQNT.dll image.
    /// </summary>
    /// <param name="stream">Seekable image stream; the stream is left open.</param>
    /// <param name="filePath">Path used only for diagnostics.</param>
    /// <returns>The unique located site.</returns>
    /// <exception cref="InvalidDataException">The site is missing, ambiguous, or the image is not a PE file.</exception>
    internal static QqntIntegritySite Locate(Stream stream, string filePath)
    {
        if (TryLocate(stream, out var site, out var failure))
        {
            return site!;
        }

        throw new InvalidDataException(failure.Replace("{file}", Path.GetFileName(filePath), StringComparison.Ordinal));
    }

    /// <summary>
    /// Attempts to locate the unique integrity-check patch site in a QQNT.dll image.
    /// </summary>
    /// <param name="stream">Seekable image stream; the stream is left open.</param>
    /// <param name="site">The located site, or <see langword="null"/> on failure.</param>
    /// <param name="failure">A localized explanation when the site could not be determined.</param>
    /// <returns><see langword="true"/> when exactly one verified site was found.</returns>
    /// <exception cref="InvalidDataException">The image is not a supported PE file.</exception>
    internal static bool TryLocate(Stream stream, out QqntIntegritySite? site, out string failure)
    {
        ArgumentNullException.ThrowIfNull(stream);

        site = null;
        failure = string.Empty;
        PeImageLayout layout = PeImageLayout.Read(stream);
        var candidates = new List<(long Offset, QqntIntegrityByteState State)>();
        foreach (var offset in FindAnchorOffsets(stream))
        {
            var window = ReadAt(stream, offset, QqntIntegrityCatalog.WindowLength);
            if (!QqntIntegrityCatalog.IsPlausibleCandidate(window))
            {
                continue;
            }

            var state = QqntIntegrityCatalog.ClassifyBytes(window);
            if (state == QqntIntegrityByteState.Unknown)
            {
                continue;
            }

            if (state != QqntIntegrityByteState.SkipPatched
                && !CallTargetsValidationFunction(stream, layout, offset))
            {
                continue;
            }

            candidates.Add((offset, state));
        }

        if (candidates.Count == 0)
        {
            failure = "{file} 中未找到可识别的完整性校验点，QQ 可能已更换布局。";
            return false;
        }

        if (candidates.Count > 1)
        {
            var described = string.Join(", ", candidates.Select(candidate => $"0x{candidate.Offset:X}"));
            failure = $"{{file}} 中命中了 {candidates.Count} 处候选校验点（{described}），无法唯一定位。";
            return false;
        }

        var (anchorOffset, detectedState) = candidates[0];
        var skipOffset = anchorOffset + QqntIntegrityCatalog.AnchorSkipOffset;
        var jumpOffset = anchorOffset + QqntIntegrityCatalog.AnchorJumpOffset;
        var skipVirtualAddress = layout.ToVirtualAddress(skipOffset);
        var jumpVirtualAddress = layout.ToVirtualAddress(jumpOffset);
        if (skipVirtualAddress is null || jumpVirtualAddress is null)
        {
            failure = "{file} 的补丁点没有对应的映像地址，文件可能已被截断。";
            return false;
        }

        site = new QqntIntegritySite(
            skipOffset,
            skipVirtualAddress.Value,
            jumpOffset,
            jumpVirtualAddress.Value,
            detectedState);
        return true;
    }

    /// <summary>
    /// Confirms that the call at the candidate site reaches the shared verification function.
    /// </summary>
    /// <param name="stream">Image stream.</param>
    /// <param name="layout">Parsed section table.</param>
    /// <param name="anchorOffset">File offset of the anchor.</param>
    /// <returns><see langword="true"/> when the relocated call target starts with the known prologue.</returns>
    private static bool CallTargetsValidationFunction(Stream stream, PeImageLayout layout, long anchorOffset)
    {
        var callOperandOffset = anchorOffset + QqntIntegrityCatalog.AnchorCallRelativeOffset;
        var nextInstruction = layout.ToVirtualAddress(callOperandOffset + 4);
        if (nextInstruction is null)
        {
            return false;
        }

        var relative = BinaryPrimitives.ReadInt32LittleEndian(ReadAt(stream, callOperandOffset, 4));
        var targetOffset = layout.ToFileOffset(nextInstruction.Value + relative);
        if (targetOffset is null
            || targetOffset < 0
            || targetOffset + QqntIntegrityCatalog.PrologueLength > stream.Length)
        {
            return false;
        }

        return QqntIntegrityCatalog.IsValidationFunctionPrologue(
            ReadAt(stream, targetOffset.Value, QqntIntegrityCatalog.PrologueLength));
    }

    /// <summary>
    /// Scans the image in bounded chunks and reports every anchor start offset exactly once.
    /// </summary>
    /// <param name="stream">Image stream positioned anywhere.</param>
    /// <returns>Absolute file offsets of anchor candidates.</returns>
    private static List<long> FindAnchorOffsets(Stream stream)
    {
        byte?[] anchor = QqntIntegrityCatalog.AnchorBytes;
        var overlap = anchor.Length - 1;
        var matches = new List<long>();
        var buffer = new byte[ChunkSize + overlap];
        var length = stream.Length;
        long position = 0;
        var carried = 0;
        stream.Position = 0;
        while (position < length)
        {
            var wanted = (int)Math.Min(ChunkSize, length - position);
            stream.ReadExactly(buffer.AsSpan(carried, wanted));
            var filled = carried + wanted;

            // The carried prefix is shorter than the anchor, so a match can never sit entirely inside
            // it: nothing is reported twice and nothing is dropped at a chunk boundary.
            foreach (var match in WildcardPattern.FindAll(buffer.AsSpan(0, filled), anchor))
            {
                matches.Add(position - carried + match);
            }

            carried = Math.Min(overlap, filled);
            Buffer.BlockCopy(buffer, filled - carried, buffer, 0, carried);
            position += wanted;
        }

        return matches;
    }

    /// <summary>
    /// Reads exactly the requested bytes from an absolute file offset.
    /// </summary>
    /// <param name="stream">Image stream.</param>
    /// <param name="offset">Absolute offset.</param>
    /// <param name="count">Number of bytes to read.</param>
    /// <returns>The requested bytes.</returns>
    private static byte[] ReadAt(Stream stream, long offset, int count)
    {
        var buffer = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }
}
