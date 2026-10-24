namespace QQAntiRecall.Core;

/// <summary>
/// Describes the recognised on-disk byte state of one QQNT.dll integrity patch site.
/// </summary>
internal enum QqntIntegrityByteState
{
    /// <summary>The failure branch is intact, so QQ still terminates itself about 15 minutes after start.</summary>
    Original,

    /// <summary>The failure jump was replaced, so a failed check no longer terminates QQ.</summary>
    SoftPatched,

    /// <summary>The delayed verification call was removed, so the check never runs.</summary>
    SkipPatched,

    /// <summary>The site exists but holds bytes this tool did not write.</summary>
    Unknown,
}

/// <summary>
/// Enumerates the supported ways to neutralise the QQNT.dll delayed integrity check.
/// </summary>
internal enum QqntIntegrityPatchVariant
{
    /// <summary>Keeps the check running and only disables its failure branch.</summary>
    Soft,

    /// <summary>Removes the delayed verification call entirely.</summary>
    Skip,
}

/// <summary>
/// Holds the verified byte signatures that identify the QQ 9.9.3x delayed integrity check in QQNT.dll.
/// </summary>
internal static class QqntIntegrityCatalog
{
    /// <summary>
    /// Full 17-byte prologue of the shared verification function (VA 0x511CB0 in 9.9.37-54276).
    /// </summary>
    internal const string ValidationFunctionPrologue =
        "41 57 41 56 41 55 41 54 56 57 55 53 48 81 EC 28 04 00 00";

    /// <summary>
    /// 20-byte anchor around the delayed-check site. Wildcard positions are rewritten by the supported
    /// variants, so the anchor must still match after this tool patches the file.
    /// </summary>
    internal const string AnchorPattern = "4C 89 E1 4C 39 F0 7C ?? EB ?? ?? ?? ?? ?? ?? ?? ?? 84 C0 ??";

    /// <summary>
    /// Offset inside the anchor of the delayed-check call site (VA 0x51763A in 9.9.37-54276).
    /// </summary>
    internal const int AnchorSkipOffset = 10;

    /// <summary>
    /// Offset inside the anchor of the rel32 field of the verification call.
    /// </summary>
    internal const int AnchorCallRelativeOffset = 13;

    /// <summary>
    /// Offset inside the anchor of the failure jump (VA 0x517643 in 9.9.37-54276).
    /// </summary>
    internal const int AnchorJumpOffset = 19;

    /// <summary>
    /// Distance from the skip site to the failure jump that the soft variant rewrites.
    /// </summary>
    internal const int SoftJumpOffsetFromSkip = AnchorJumpOffset - AnchorSkipOffset;

    /// <summary>
    /// Original skip-site prefix: mov cl,1.
    /// </summary>
    internal const string SkipOriginalPrefixBytes = "B1 01";

    /// <summary>
    /// Replacement that forces the check flag to success and removes the call entirely.
    /// </summary>
    internal const string SkipPatchedBytes = "B0 01 90 90 90 90 90";

    /// <summary>
    /// Original two-byte conditional jump into the message box and exit path.
    /// </summary>
    internal const string SoftOriginalBytes = "74 67";

    /// <summary>
    /// Replacement that falls through into the normal cleanup path.
    /// </summary>
    internal const string SoftPatchedBytes = "90 90";

    private const byte CallOpcode = 0xE8;
    private const byte FailedTestOpcode = 0x84;
    private const byte FailedTestSecondByte = 0xC0;

    private static readonly byte?[] Anchor = WildcardPattern.Parse(AnchorPattern);

    private static readonly byte[] SkipOriginalPrefix = Parse(SkipOriginalPrefixBytes);
    private static readonly byte[] SkipPatched = Parse(SkipPatchedBytes);
    private static readonly byte[] SoftOriginal = Parse(SoftOriginalBytes);
    private static readonly byte[] SoftPatched = Parse(SoftPatchedBytes);
    private static readonly byte[] Prologue = Parse(ValidationFunctionPrologue);

    /// <summary>
    /// Gets the wildcard anchor used to find candidate patch sites in the section that holds the check.
    /// </summary>
    internal static byte?[] AnchorBytes => Anchor;

    /// <summary>
    /// Gets the number of bytes that must be read from an anchor candidate before it can be classified.
    /// </summary>
    internal static int WindowLength => Anchor.Length + 1;

    /// <summary>
    /// Gets the number of bytes of the verification function prologue that must appear at the call target.
    /// </summary>
    internal static int PrologueLength => Prologue.Length;

    /// <summary>
    /// Confirms that the verification function prologue starts at the supplied bytes.
    /// </summary>
    /// <param name="buffer">Bytes read from the claimed call target.</param>
    /// <returns><see langword="true"/> when every prologue byte matches.</returns>
    internal static bool IsValidationFunctionPrologue(ReadOnlySpan<byte> buffer) =>
        buffer.Length >= Prologue.Length && buffer[..Prologue.Length].SequenceEqual(Prologue);

    /// <summary>
    /// Rejects anchor hits that cannot be the delayed-check site.
    /// </summary>
    /// <param name="window">Bytes starting at the candidate anchor.</param>
    /// <returns><see langword="true"/> when the jump holds an original or patched value.</returns>
    internal static bool IsPlausibleCandidate(ReadOnlySpan<byte> window) =>
        window.Length >= WindowLength && window[AnchorJumpOffset] is 0x74 or 0x90;

    /// <summary>
    /// Classifies the bytes around a located site without trusting the tool that wrote them.
    /// </summary>
    /// <param name="window">Bytes starting at the anchor.</param>
    /// <returns>The recognised byte state.</returns>
    internal static QqntIntegrityByteState ClassifyBytes(ReadOnlySpan<byte> window)
    {
        if (window.Length < WindowLength)
        {
            throw new ArgumentException("Anchor window is too short.", nameof(window));
        }

        if (window.Slice(AnchorSkipOffset, SkipPatched.Length).SequenceEqual(SkipPatched))
        {
            return QqntIntegrityByteState.SkipPatched;
        }

        var jumpPatched = window.Slice(AnchorJumpOffset, SoftPatched.Length).SequenceEqual(SoftPatched);
        var skipIsOriginal = window.Slice(AnchorSkipOffset, SkipOriginalPrefix.Length).SequenceEqual(SkipOriginalPrefix)
            && window[AnchorSkipOffset + 2] == CallOpcode
            && window[AnchorJumpOffset - 2] == FailedTestOpcode
            && window[AnchorJumpOffset - 1] == FailedTestSecondByte;
        if (skipIsOriginal)
        {
            return jumpPatched ? QqntIntegrityByteState.SoftPatched : QqntIntegrityByteState.Original;
        }

        return QqntIntegrityByteState.Unknown;
    }

    /// <summary>
    /// Produces the replacement bytes for one variant at its site.
    /// </summary>
    /// <param name="variant">Variant to write.</param>
    /// <returns>The replacement bytes for that variant.</returns>
    internal static byte[] GetPatchedBytes(QqntIntegrityPatchVariant variant) => variant switch
    {
        QqntIntegrityPatchVariant.Skip => SkipPatched,
        _ => SoftPatched,
    };

    /// <summary>
    /// Gets the offset of one variant relative to the located anchor.
    /// </summary>
    /// <param name="variant">Variant whose offset is required.</param>
    /// <returns>The offset from the anchor start.</returns>
    internal static int GetSiteOffset(QqntIntegrityPatchVariant variant) => variant switch
    {
        QqntIntegrityPatchVariant.Skip => AnchorSkipOffset,
        _ => AnchorJumpOffset,
    };

    /// <summary>
    /// Gets the offset of the failure jump relative to the located anchor.
    /// </summary>
    /// <param name="variant">Variant whose failure jump is required.</param>
    /// <returns>The offset from the anchor start.</returns>
    internal static int GetFailureJumpOffset(QqntIntegrityPatchVariant variant) =>
        GetSiteOffset(variant) + (variant == QqntIntegrityPatchVariant.Skip ? SoftJumpOffsetFromSkip : 0);

    /// <summary>
    /// Gets every byte range this tool is allowed to rewrite, relative to the anchor start.
    /// </summary>
    /// <returns>The known patch windows.</returns>
    internal static IReadOnlyList<(int Offset, int Length)> GetPatchWindows() =>
    [
        (AnchorSkipOffset, SkipPatched.Length),
        (AnchorJumpOffset, SoftPatched.Length),
    ];

    /// <summary>
    /// Parses space-delimited hexadecimal bytes into a compact array.
    /// </summary>
    /// <param name="value">Hex bytes separated by spaces.</param>
    /// <returns>The parsed bytes.</returns>
    private static byte[] Parse(string value) =>
        Convert.FromHexString(value.Replace(" ", string.Empty, StringComparison.Ordinal));
}
