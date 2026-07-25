namespace QQAntiRecall.Core;

/// <summary>
/// Describes one independently verified wrapper.node signature and its replacement bytes.
/// </summary>
internal sealed class PatchDefinition
{
    /// <summary>
    /// Creates a patch definition and derives the signature expected after replacement.
    /// </summary>
    internal PatchDefinition(string name, string originalPattern, int patchOffset, string replacement)
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
/// Holds the complete QQ anti-recall patch set; all entries must transition together.
/// </summary>
internal static class PatchCatalog
{
    internal static readonly IReadOnlyList<PatchDefinition> Definitions =
    [
        new(
            "Normal recall",
            "48 89 CF 48 8B 0A 48 85 C9 0F 84 ?? ?? ?? ?? 44 89 CB 4D 89 C4 48 89 95 ?? ?? ?? ?? 48 8D 95 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8D 8D ?? ?? ?? ?? 48 8B 31",
            6,
            "48 31 C9"),
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
