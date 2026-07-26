using System.Buffers.Binary;

namespace QQAntiRecall.Core;

/// <summary>
/// Describes one independently verified wrapper.node signature and its replacement bytes.
/// </summary>
internal sealed class PatchDefinition
{
    /// <summary>
    /// Creates a patch definition and derives the signature expected after replacement.
    /// </summary>
    internal PatchDefinition(
        string name,
        string originalPattern,
        int patchOffset,
        string replacement)
    {
        Name = name;
        OriginalPattern = WildcardPattern.Parse(originalPattern);
        PatchOffset = patchOffset;
        Replacement = Convert.FromHexString(replacement.Replace(" ", string.Empty, StringComparison.Ordinal));

        if (patchOffset < 0 || patchOffset + Replacement.Length > OriginalPattern.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(patchOffset));
        }

        PatchedPattern = (byte?[])OriginalPattern.Clone();
        for (var index = 0; index < Replacement.Length; index++)
        {
            PatchedPattern[PatchOffset + index] = Replacement[index];
        }
    }

    internal string Name { get; }

    internal byte?[] OriginalPattern { get; }

    internal byte?[] PatchedPattern { get; }

    internal int PatchOffset { get; }

    internal byte[] Replacement { get; }
}

/// <summary>
/// Couples one complete patch definition set with the exact match counts verified for a QQ code layout.
/// </summary>
internal sealed class PatchProfile
{
    /// <summary>
    /// Creates an immutable profile whose requirement order matches its definition order.
    /// </summary>
    internal PatchProfile(
        string name,
        IReadOnlyList<PatchDefinition> definitions,
        IReadOnlyList<int> expectedMatchCounts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(expectedMatchCounts);
        if (definitions.Count == 0 || definitions.Count != expectedMatchCounts.Count)
        {
            throw new ArgumentException("Definitions and expected match counts must have the same non-zero length.");
        }

        if (expectedMatchCounts.Any(count => count <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedMatchCounts));
        }

        Name = name;
        Definitions = definitions.ToArray();
        ExpectedMatchCounts = expectedMatchCounts.ToArray();
    }

    internal string Name { get; }

    internal IReadOnlyList<PatchDefinition> Definitions { get; }

    internal IReadOnlyList<int> ExpectedMatchCounts { get; }

    /// <summary>
    /// Returns the exact required count for one definition owned by this profile.
    /// </summary>
    internal int GetExpectedMatchCount(PatchDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        for (var index = 0; index < Definitions.Count; index++)
        {
            if (ReferenceEquals(Definitions[index], definition))
            {
                return ExpectedMatchCounts[index];
            }
        }

        throw new ArgumentException("The definition does not belong to this patch profile.", nameof(definition));
    }
}

/// <summary>
/// Holds the complete QQ anti-recall patch set; all entries must transition together.
/// </summary>
internal static class PatchCatalog
{
    private const int NormalRecallSignatureOffsetFromFunctionStart = 0x26;

    internal static readonly IReadOnlyList<PatchDefinition> Definitions =
    [
        new(
            "Normal recall notification",
            "48 8D 86 A8 00 00 00 44 8B 4E 78 48 83 EE 80 48 8B 55 ?? 48 8B 0A 48 89 4D ?? 48 8B 4A 08 48 89 4D ?? 48 85 C9 74 04 F0 FF 41 08 48 89 44 24 20 C6 44 24 28 01 48 8D 55 ?? 48 89 F9 49 89 F0 E8 ?? ?? ?? ??",
            63,
            "90 90 90 90 90"),
        new(
            "Normal recall notification batch",
            "44 8B 4D ?? 49 8B 46 10 48 89 85 ?? ?? ?? ?? 49 8B 46 18 48 89 85 ?? ?? ?? ?? 48 85 C0 74 04 F0 FF 40 08 48 8D 45 ?? 48 89 44 24 20 C6 44 24 28 01 48 8D 95 ?? ?? ?? ?? 4C 8D 45 ?? E8 ?? ?? ?? ?? 48 8B 85 ?? ?? ?? ?? C6 80 F0 00 00 00 01",
            60,
            "90 90 90 90 90"),
        new(
            "Normal recall notification batch fallback",
            "49 8B 8E 00 01 00 00 48 8B 41 10 48 89 85 ?? ?? ?? ?? 48 8B 49 18 48 89 8D ?? ?? ?? ?? 48 85 C9 74 0D F0 FF 41 08 48 8B 95 ?? ?? ?? ?? EB 02 31 D2 48 8B 8D ?? ?? ?? ?? 44 8B 4D ?? 48 89 85 ?? ?? ?? ?? 48 89 95 ?? ?? ?? ?? 48 85 D2 74 04 F0 FF 42 08 48 8D 45 ?? 48 89 44 24 20 C6 44 24 28 01 48 8D 95 ?? ?? ?? ?? 4C 8D 45 ?? E8 ?? ?? ?? ??",
            108,
            "90 90 90 90 90"),
        new(
            "Traceless recall",
            "48 8B 01 FF 50 28 3C 01 0F 84 ?? ?? ?? ?? 48 8B 85 ?? ?? ?? ?? 48 8B 08 48 8B 01 FF 50 30 4C 8B 73 30",
            6,
            "38 C0"),
        new(
            "Recall notification update",
            "48 83 7A 10 00 0F 84 ?? ?? ?? ?? 4C 89 C3 48 89 D7 48 89 CE 48 8D 45 ?? 48 89 00 48 89 40 08 48 83 60 10 00",
            0,
            "48 31 C0 90 90"),
    ];

    internal static readonly IReadOnlyList<PatchProfile> Profiles =
    [
        new("9.9.32-51246", Definitions, [1, 1, 2, 1, 1]),
        new("9.9.33-51728", Definitions, [1, 2, 2, 1, 1]),
    ];

    internal static PatchProfile DefaultProfile => Profiles[^1];

    internal static readonly IReadOnlyList<PatchDefinition> LegacyDefinitions =
    [
        new(
            "Legacy normal recall",
            "48 89 CF 48 8B 0A 48 85 C9 0F 84 ?? ?? ?? ?? 44 89 CB 4D 89 C4 48 89 95 ?? ?? ?? ?? 48 8D 95 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8D 8D ?? ?? ?? ?? 48 8B 31",
            6,
            "48 31 C9"),
        Definitions[3],
        Definitions[4],
    ];

    internal static readonly PatchProfile LegacyProfile = new(
        "0.0.1 legacy",
        LegacyDefinitions,
        [1, 1, 1]);

    /// <summary>
    /// Finds the single current profile whose complete original signature counts match.
    /// </summary>
    internal static PatchProfile? FindOriginalProfile(IReadOnlyList<PatchSignatureStatus> statuses)
    {
        return FindUniqueProfile(statuses, static (status, expectedCount) =>
            status.OriginalMatchCount == expectedCount && status.PatchedMatchCount == 0);
    }

    /// <summary>
    /// Finds the single current profile whose complete patched signature counts match.
    /// </summary>
    internal static PatchProfile? FindPatchedProfile(IReadOnlyList<PatchSignatureStatus> statuses)
    {
        return FindUniqueProfile(statuses, static (status, expectedCount) =>
            status.OriginalMatchCount == 0 && status.PatchedMatchCount == expectedCount);
    }

    /// <summary>
    /// Detects the complete 0.0.1 patch set so it can be restored or upgraded from its verified backup.
    /// </summary>
    internal static bool IsLegacyInstalled(ReadOnlySpan<byte> content)
    {
        for (var index = 0; index < LegacyProfile.Definitions.Count; index++)
        {
            var definition = LegacyProfile.Definitions[index];
            var expectedCount = LegacyProfile.ExpectedMatchCounts[index];
            if (WildcardPattern.FindAll(content, definition.OriginalPattern).Count != 0
                || WildcardPattern.FindAll(content, definition.PatchedPattern).Count != expectedCount)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Confirms the current patch leaves QQ's shared normal-recall function unchanged for local operations.
    /// </summary>
    internal static bool HasUnmodifiedNormalRecallFunction(ReadOnlySpan<byte> content)
    {
        var normalRecall = LegacyDefinitions[0];
        return WildcardPattern.FindAll(content, normalRecall.OriginalPattern).Count == 1
            && WildcardPattern.FindAll(content, normalRecall.PatchedPattern).Count == 0;
    }

    /// <summary>
    /// Confirms every unpatched notification call resolves to the uniquely identified normal-recall function.
    /// </summary>
    internal static bool HasValidNormalRecallCallTargets(ReadOnlySpan<byte> content, PatchProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var normalRecall = LegacyDefinitions[0];
        var originalTargets = WildcardPattern.FindAll(content, normalRecall.OriginalPattern);
        var patchedTargets = WildcardPattern.FindAll(content, normalRecall.PatchedPattern);
        if (originalTargets.Count + patchedTargets.Count != 1)
        {
            return false;
        }

        var signatureOffset = originalTargets.Count == 1 ? originalTargets[0] : patchedTargets[0];
        var expectedFunctionOffset = signatureOffset - NormalRecallSignatureOffsetFromFunctionStart;
        for (var definitionIndex = 0; definitionIndex < 3; definitionIndex++)
        {
            var definition = profile.Definitions[definitionIndex];
            var matches = WildcardPattern.FindAll(content, definition.OriginalPattern);
            if (matches.Count != profile.ExpectedMatchCounts[definitionIndex])
            {
                return false;
            }

            foreach (var match in matches)
            {
                var callOffset = match + definition.PatchOffset;
                if (content[callOffset] != 0xE8)
                {
                    return false;
                }

                var displacement = BinaryPrimitives.ReadInt32LittleEndian(content[(callOffset + 1)..]);
                if (callOffset + 5 + displacement != expectedFunctionOffset)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Returns one profile only when its complete count vector uniquely satisfies the supplied predicate.
    /// </summary>
    private static PatchProfile? FindUniqueProfile(
        IReadOnlyList<PatchSignatureStatus> statuses,
        Func<PatchSignatureStatus, int, bool> matches)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        ArgumentNullException.ThrowIfNull(matches);
        PatchProfile? matchedProfile = null;
        foreach (var profile in Profiles)
        {
            if (statuses.Count != profile.ExpectedMatchCounts.Count)
            {
                continue;
            }

            var matchesProfile = true;
            for (var index = 0; index < statuses.Count; index++)
            {
                if (!matches(statuses[index], profile.ExpectedMatchCounts[index]))
                {
                    matchesProfile = false;
                    break;
                }
            }

            if (!matchesProfile)
            {
                continue;
            }

            if (matchedProfile is not null)
            {
                return null;
            }

            matchedProfile = profile;
        }

        return matchedProfile;
    }
}

/// <summary>
/// Parses and searches byte patterns whose unknown positions are represented by question marks.
/// </summary>
internal static class WildcardPattern
{
    /// <summary>
    /// Parses space-delimited hexadecimal bytes and question-mark wildcards.
    /// </summary>
    internal static byte?[] Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token == "??" ? (byte?)null : Convert.ToByte(token, 16))
            .ToArray();
    }

    /// <summary>
    /// Returns every start offset that satisfies the supplied wildcard pattern.
    /// </summary>
    internal static IReadOnlyList<int> FindAll(ReadOnlySpan<byte> content, byte?[] pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var matches = new List<int>();
        if (pattern.Length == 0 || content.Length < pattern.Length)
        {
            return matches;
        }

        var start = 0;
        while (start <= content.Length - pattern.Length)
        {
            if (pattern[0].HasValue)
            {
                var next = content[start..].IndexOf(pattern[0]!.Value);
                if (next < 0)
                {
                    break;
                }

                start += next;
                if (start > content.Length - pattern.Length)
                {
                    break;
                }
            }

            var matched = true;
            for (var patternIndex = 0; patternIndex < pattern.Length; patternIndex++)
            {
                var expected = pattern[patternIndex];
                if (expected.HasValue && content[start + patternIndex] != expected.Value)
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                matches.Add(start);
            }

            start++;
        }

        return matches;
    }
}
