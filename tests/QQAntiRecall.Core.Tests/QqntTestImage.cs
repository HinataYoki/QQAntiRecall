using System.Buffers.Binary;
using System.Text;

namespace QQAntiRecall.Core.Tests;

/// <summary>
/// Generates synthetic PE32+ images containing only the delayed integrity-check site.
/// </summary>
internal static class QqntTestImage
{
    internal const long TextVirtualAddress = 0x1000;
    internal const long TextRawPointer = 0x400;
    internal const int TextRawSize = 0x1000;
    internal const long PrologueVirtualAddress = 0x1100;
    internal const long AnchorVirtualAddress = 0x1400;

    /// <summary>
    /// Creates a minimal PE image whose single .text section holds the verification prologue and one or
    /// more delayed-check anchors, each with a correctly relocated call.
    /// </summary>
    /// <param name="original">Whether the failure jump is intact or already disabled.</param>
    /// <param name="skipPatched">Whether the delayed call is already removed.</param>
    /// <param name="withPrologue">Whether the call targets hold the known prologue.</param>
    /// <param name="anchorCount">Number of complete anchors to emit; more than one is ambiguous.</param>
    /// <returns>The image bytes.</returns>
    internal static byte[] Create(
        bool original = true,
        bool skipPatched = false,
        bool withPrologue = true,
        int anchorCount = 1)
    {
        var image = new byte[TextRawPointer + TextRawSize];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), 0x80);

        var peHeader = image.AsSpan(0x80);
        peHeader[0] = (byte)'P';
        peHeader[1] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(peHeader[6..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(peHeader[0x14..], 0xA0);

        var section = image.AsSpan(0x80 + 0x18 + 0xA0);
        Encoding.ASCII.GetBytes(".text").CopyTo(section);
        BinaryPrimitives.WriteUInt32LittleEndian(section[8..], TextRawSize);
        BinaryPrimitives.WriteUInt32LittleEndian(section[12..], (uint)TextVirtualAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(section[16..], TextRawSize);
        BinaryPrimitives.WriteUInt32LittleEndian(section[20..], (uint)TextRawPointer);

        if (withPrologue)
        {
            Convert.FromHexString(
                QqntIntegrityCatalog.ValidationFunctionPrologue.Replace(" ", string.Empty, StringComparison.Ordinal))
                .CopyTo(image, (int)OffsetOf(PrologueVirtualAddress));
        }

        for (var index = 0; index < anchorCount; index++)
        {
            WriteAnchor(image, AnchorVirtualAddress + (index * 0x100), original, skipPatched, withPrologue);
        }

        return image;
    }

    /// <summary>
    /// Converts a virtual address inside .text to its file offset.
    /// </summary>
    /// <param name="virtualAddress">Virtual address inside .text.</param>
    /// <returns>The file offset.</returns>
    internal static long OffsetOf(long virtualAddress) => TextRawPointer + (virtualAddress - TextVirtualAddress);

    /// <summary>
    /// Writes one complete anchor with a correctly relocated call.
    /// </summary>
    /// <param name="image">Destination image.</param>
    /// <param name="virtualAddress">Virtual address of the anchor.</param>
    /// <param name="original">Whether the failure jump is intact.</param>
    /// <param name="skipPatched">Whether the delayed call is already removed.</param>
    /// <param name="withPrologue">Whether the call resolves to the known prologue.</param>
    private static void WriteAnchor(
        byte[] image,
        long virtualAddress,
        bool original,
        bool skipPatched,
        bool withPrologue)
    {
        var anchorOffset = (int)OffsetOf(virtualAddress);
        byte[] prefix = [0x4C, 0x89, 0xE1, 0x4C, 0x39, 0xF0, 0x7C, 0xC9, 0xEB, 0xC4];
        prefix.CopyTo(image, anchorOffset);
        if (skipPatched)
        {
            Convert.FromHexString("B0019090909090").CopyTo(image, anchorOffset + 10);
        }
        else
        {
            image[anchorOffset + 10] = 0xB1;
            image[anchorOffset + 11] = 0x01;
            image[anchorOffset + 12] = 0xE8;
            var callSite = virtualAddress + 12;
            var relative = withPrologue
                ? (int)(PrologueVirtualAddress - (callSite + 5))
                : 0x7FFF;
            BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(anchorOffset + 13), relative);
        }

        image[anchorOffset + 17] = 0x84;
        image[anchorOffset + 18] = 0xC0;
        // The soft variant replaces the whole two-byte conditional jump with NOPs.
        image[anchorOffset + 19] = original ? (byte)0x74 : (byte)0x90;
        image[anchorOffset + 20] = original ? (byte)0x67 : (byte)0x90;
    }
}
