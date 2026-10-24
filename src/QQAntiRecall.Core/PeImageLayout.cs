using System.Buffers.Binary;
using System.Text;

namespace QQAntiRecall.Core;

/// <summary>
/// Describes one PE section header required to translate between file offsets and virtual addresses.
/// </summary>
internal sealed record PeSection(
    string Name,
    uint VirtualAddress,
    uint VirtualSize,
    uint RawPointer,
    uint RawSize);

/// <summary>
/// Caches the PE section table so a patch site can be addressed by file offset or by virtual address.
/// </summary>
internal sealed class PeImageLayout
{
    private const int DosHeaderSize = 0x40;
    private const int CoffHeaderSize = 0x18;
    private const int SectionHeaderSize = 40;
    private const int MaximumSectionCount = 96;
    private const int MaximumPeHeaderOffset = 0x100000;

    private PeImageLayout(IReadOnlyList<PeSection> sections)
    {
        Sections = sections;
    }

    /// <summary>
    /// Gets the section headers in declaration order.
    /// </summary>
    internal IReadOnlyList<PeSection> Sections { get; }

    /// <summary>
    /// Reads the DOS, COFF, and section headers from a PE image without loading mapped sections.
    /// </summary>
    /// <param name="stream">Seekable image stream positioned anywhere.</param>
    /// <returns>The parsed section table.</returns>
    /// <exception cref="InvalidDataException">The stream does not expose a supported PE header.</exception>
    internal static PeImageLayout Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Span<byte> dosHeader = stackalloc byte[DosHeaderSize];
        stream.Position = 0;
        stream.ReadExactly(dosHeader);
        if (dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
        {
            throw new InvalidDataException("文件不是有效的 PE 映像（缺少 MZ 头）。");
        }

        var peHeaderOffset = BinaryPrimitives.ReadInt32LittleEndian(dosHeader[0x3C..]);
        if (peHeaderOffset <= 0 || peHeaderOffset > MaximumPeHeaderOffset)
        {
            throw new InvalidDataException("PE 头偏移无效。");
        }

        Span<byte> coffHeader = stackalloc byte[CoffHeaderSize];
        stream.Position = peHeaderOffset;
        stream.ReadExactly(coffHeader);
        if (coffHeader[0] != (byte)'P' || coffHeader[1] != (byte)'E' || coffHeader[2] != 0 || coffHeader[3] != 0)
        {
            throw new InvalidDataException("PE 签名无效。");
        }

        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(coffHeader[6..]);
        var optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(coffHeader[0x14..]);
        if (sectionCount == 0 || sectionCount > MaximumSectionCount)
        {
            throw new InvalidDataException("PE 节数量无效。");
        }

        var sectionTable = new byte[sectionCount * SectionHeaderSize];
        stream.Position = peHeaderOffset + CoffHeaderSize + optionalHeaderSize;
        stream.ReadExactly(sectionTable);

        var sections = new List<PeSection>(sectionCount);
        for (var index = 0; index < sectionCount; index++)
        {
            var header = sectionTable.AsSpan(index * SectionHeaderSize, SectionHeaderSize);
            var name = Encoding.ASCII.GetString(header[..8]).TrimEnd('\0');
            sections.Add(new PeSection(
                name,
                VirtualAddress: BinaryPrimitives.ReadUInt32LittleEndian(header[12..]),
                VirtualSize: BinaryPrimitives.ReadUInt32LittleEndian(header[8..]),
                RawPointer: BinaryPrimitives.ReadUInt32LittleEndian(header[20..]),
                RawSize: BinaryPrimitives.ReadUInt32LittleEndian(header[16..])));
        }

        return new PeImageLayout(sections);
    }

    /// <summary>
    /// Maps a virtual address inside a mapped section to its file offset.
    /// </summary>
    /// <param name="virtualAddress">Image-relative virtual address.</param>
    /// <returns>The file offset, or <see langword="null"/> when no section contains the address.</returns>
    internal long? ToFileOffset(long virtualAddress)
    {
        foreach (PeSection section in Sections)
        {
            var mappedSize = Math.Max(section.VirtualSize, section.RawSize);
            if (virtualAddress >= section.VirtualAddress && virtualAddress < section.VirtualAddress + (long)mappedSize)
            {
                return section.RawPointer + (virtualAddress - section.VirtualAddress);
            }
        }

        return null;
    }

    /// <summary>
    /// Maps a file offset that carries section bytes to its virtual address.
    /// </summary>
    /// <param name="fileOffset">Offset from the start of the file.</param>
    /// <returns>The virtual address, or <see langword="null"/> when the offset is not backed by a section.</returns>
    internal long? ToVirtualAddress(long fileOffset)
    {
        foreach (PeSection section in Sections)
        {
            if (fileOffset >= section.RawPointer && fileOffset < section.RawPointer + (long)section.RawSize)
            {
                return section.VirtualAddress + (fileOffset - section.RawPointer);
            }
        }

        return null;
    }
}
