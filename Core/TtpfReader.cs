using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace RpfPreview.Core;

public sealed record TtpfTextureInfo(int Index, string Name, int Width, int Height, int DxgiFormat,
    string Format, int Mips, int Depth, int Dimension, long StoredSize, long DdsSize)
{
    public int SourceMips { get; init; }
    public int NonTailMipCount { get; init; }
    public int PackedMipCount { get; init; }
    public bool SupportsNativeReplacement { get; init; }
    public int BaseMipLevel { get; init; }
    public int ExportWidth => Math.Max(1, Width >> BaseMipLevel);
    public int ExportHeight => Math.Max(1, Height >> BaseMipLevel);

    public ulong AssetKey { get; init; }

    public string Id { get; init; } = "";
    public long VirtualOffset { get; init; }
    public bool TopOnly => BaseMipLevel == 0 && Mips == 1 && SourceMips > 1;
    public bool PartialMips => BaseMipLevel > 0 || Mips < SourceMips;
    public bool CanExtract => Limitation is null && DdsSize > 0;
    public string? Limitation { get; init; }
    public string? PreviewNote { get; init; }
    public int PreviewMipLevel => BaseMipLevel + RageTextureDecoder.ChoosePreviewMip(ExportWidth, ExportHeight, Mips);
    public int PreviewWidth => Math.Max(1, Width >> PreviewMipLevel);
    public int PreviewHeight => Math.Max(1, Height >> PreviewMipLevel);
    public bool CanPreview => CanExtract && (long)PreviewWidth * PreviewHeight <= 16 * 1024 * 1024 && DxgiFormat is not (81 or 84);
}

public sealed record TtpfInfo(string SourcePath, long SourceLength, long VirtualSize, int Version,
    IReadOnlyList<TtpfTextureInfo> Textures, IReadOnlyDictionary<string, string> Metadata);











public static class TtpfReader
{
    private const int MaxOutput = 256 * 1024 * 1024;
    private const int MaxIndexBytes = 64 * 1024 * 1024;
    private const int MaxTextures = 250_000;
    internal sealed record Chunk(long Source, int Packed, long Virtual, int Size, int Codec, int Padding);
    internal sealed record Mip(int Width, int Height, int PaddedWidth, int Offset, int RegionSize, int TailX, int TailY);
    internal sealed record Layout(int Size, int BlockWidth, int BlockHeight, int FirstTail, int ElementBytes, IReadOnlyList<Mip> Mips)
    {
        public int TileBytes { get; init; } = 65536;
    }
    internal sealed record Native(TtpfTextureInfo Info, Layout? Layout);
    internal sealed record Index(TtpfInfo Info, IReadOnlyList<Chunk> Chunks, IReadOnlyList<Native> Native);
    private static readonly int[][] Patterns = [
        [1,2,3,-2,-1,-3,4,-4,-5,5,-6,6,-7,7,-8,8],
        [0,1,2,3,-1,-2,-3,4,-4,5,-5,6,-6,7,-7,8],
        [0,0,1,2,-1,-2,-3,3,-4,4,-5,5,-6,6,-7,7],
        [0,0,0,1,-1,2,3,-2,-3,4,-4,5,-5,6,-6,7],
        [0,0,0,0,1,-1,2,-2,-3,3,-4,4,-5,5,-6,6]];
    private static readonly int[][] FourKbPatterns = [
        [1,2,3,4,-1,-2,-3,-4,-5,5,-6,6],
        [0,1,2,3,-1,-2,-3,4,-4,5,-5,6],
        [0,0,1,2,-1,-2,-3,3,-4,4,-5,5],
        [0,0,0,1,-1,-2,2,3,-3,4,-4,5],
        [0,0,0,0,-1,-2,1,2,-3,3,-4,4]];

    public static TtpfInfo Inspect(string path, CancellationToken cancellationToken = default) => ReadIndex(path, cancellationToken).Info;

    public static RageTexturePixels DecodeThumbnail(string path, int index, CancellationToken cancellationToken = default)
        => DecodeTexturePreview(path, index, 192, cancellationToken);

    public static RageTexturePixels DecodeTexturePreview(string path, int index, int maximumDimension = 1024, CancellationToken cancellationToken = default)
    {
        if (maximumDimension is < 32 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumDimension));
        var source = ReadIndex(path, cancellationToken); var native = Select(source, index); var t = native.Info;
        int level = t.BaseMipLevel;
        while (level + 1 < t.BaseMipLevel + t.Mips && (Math.Max(1, t.Width >> level) > maximumDimension || Math.Max(1, t.Height >> level) > maximumDimension)) level++;
        int width = Math.Max(1, t.Width >> level), height = Math.Max(1, t.Height >> level);
        if ((long)width * height > 16 * 1024 * 1024) throw new NotSupportedException("No bounded thumbnail mip is available.");
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != source.Info.SourceLength) throw new IOException("Texture pool changed.");
        byte[] linear = ReadMip(input, source, native, level, new(), cancellationToken);
        return RageTextureDecoder.DecodeLinearTexturePixels(linear, width, height, t.DxgiFormat);
    }

    public static byte[] CreateDds(string path, int index, CancellationToken cancellationToken = default)
    {
        var source = ReadIndex(path, cancellationToken); var native = Select(source, index);
        var t = native.Info; var layout = native.Layout!;
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != source.Info.SourceLength) throw new IOException("The texture pool changed while it was being read.");
        var cache = new Dictionary<int, byte[]>();
        using var output = new MemoryStream(checked((int)t.DdsSize));
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        bool bc = RageTextureDecoder.IsBlockCompressed(t.DxgiFormat);
        writer.Write("DDS "u8); writer.Write(124); writer.Write(bc ? 0xA1007 : 0x2100F);
        writer.Write(t.ExportHeight); writer.Write(t.ExportWidth);
        writer.Write(bc ? LinearBytes(layout.Mips[t.BaseMipLevel], layout.ElementBytes) : t.ExportWidth * layout.ElementBytes);
        writer.Write(0); writer.Write(t.Mips); writer.Write(new byte[44]);
        writer.Write(32); writer.Write(4); writer.Write(0x30315844); writer.Write(new byte[20]);
        writer.Write(t.Mips > 1 ? 0x401008 : 0x1000); writer.Write(new byte[16]);
        writer.Write(t.DxgiFormat); writer.Write(3); writer.Write(0); writer.Write(1); writer.Write(0);
        for (int mip = t.BaseMipLevel; mip < t.BaseMipLevel + t.Mips; mip++) writer.Write(ReadMip(input, source, native, mip, cache, cancellationToken));
        if (output.Length != t.DdsSize) throw new InvalidDataException("Generated DDS length disagrees with the inspected texture.");
        return output.ToArray();
    }

    public static RageTexturePixels DecodeTexturePixels(string path, int index, int slice = 0, CancellationToken cancellationToken = default)
    {
        if (slice != 0) throw new NotSupportedException("This TTPF reader currently previews supported 2D textures only.");
        var source = ReadIndex(path, cancellationToken); var native = Select(source, index); var t = native.Info;
        if (!t.CanPreview) throw new NotSupportedException(t.Limitation ?? "No supported mip fits the preview output limit.");
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != source.Info.SourceLength) throw new IOException("The texture pool changed while it was being read.");
        byte[] linear = ReadMip(input, source, native, t.PreviewMipLevel, new(), cancellationToken);
        return RageTextureDecoder.DecodeLinearTexturePixels(linear, t.PreviewWidth, t.PreviewHeight, t.DxgiFormat);
    }

    internal static Index ReadIndex(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); path = Path.GetFullPath(path);
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long fileSize = input.Length; if (fileSize < 256) throw new InvalidDataException("Truncated RDS5 header.");
        byte[] header = new byte[24]; input.ReadExactly(header);
        if (!header.AsSpan(0, 4).SequenceEqual("RDS5"u8)) throw new InvalidDataException("Not an RDS5 container.");
        uint count = U32(header, 4); ulong sizeWord = U64(header, 8);
        long virtualSize = checked((long)(sizeWord & 0x00FFFFFFFFFFFFFFUL));
        if (count < 3 || count > 1_000_002 || (sizeWord >> 56) != 22 || virtualSize < 4096 || virtualSize > 1L << 34 ||
            U32(header, 16) < (count + 31) / 32 || U32(header, 16) > (count + 31) / 32 + 1)
            throw new InvalidDataException("Unsupported RDS5 index layout.");



        long tableEnd = checked(24L + (count - 1) * 8L), etpf = Align(tableEnd, 256);
        if (tableEnd > fileSize - 16 || (count - 1) * 8L > MaxIndexBytes) throw new InvalidDataException("RDS5 index exceeds its source or limit.");
        byte[] table = new byte[checked(((int)count - 1) * 8)]; input.ReadExactly(table);
        var chunks = new List<Chunk>(checked((int)count - 2));
        long sentinelSource = SourceOffset(U64(table, table.Length - 8)), sentinelVirtual = VirtualOffset(U64(table, table.Length - 8));
        if (sentinelSource != fileSize || sentinelVirtual != virtualSize) throw new InvalidDataException("RDS5 sentinel does not match its source/virtual length.");
        for (int i = 0; i < count - 2; i++)
        {
            ct.ThrowIfCancellationRequested(); ulong value = U64(table, i * 8), next = U64(table, (i + 1) * 8);
            long start = SourceOffset(value), end = SourceOffset(next), v = VirtualOffset(value), ve = VirtualOffset(next);
            int pad = (int)(value >> 52), codec = (int)((value >> 26) & 15);
            long packed = end - start - pad, size = ve - v;
            if (start < etpf + 16 || end > fileSize || packed < 1 || packed > MaxOutput || size < 1 || size > MaxOutput ||
                ve > virtualSize || (codec == 0 && packed != size)) throw new InvalidDataException("RDS5 chunk range is invalid or exceeds the per-chunk limit.");
            chunks.Add(new(start, (int)packed, v, (int)size, codec, pad));
        }
        input.Position = etpf; byte[] eh = new byte[16]; input.ReadExactly(eh);
        if (!eh.AsSpan(0, 4).SequenceEqual("ETPF"u8) || U32(eh, 4) != 634) throw new InvalidDataException("Unsupported embedded ETPF version.");
        int textures = checked((int)U32(eh, 8)), area = checked((int)U32(eh, 12));
        long metadataBytes = checked(16L + area + textures * 32L), paddedMetadata = Align(metadataBytes, 4096);
        uint alignmentWord = U32(header, 20); long sourcePadding = (alignmentWord >> 28) * 256L;
        if (textures < 1 || textures > MaxTextures || area < textures * 16L || metadataBytes > MaxIndexBytes ||
            (alignmentWord & 0x0FFFFFFF) != 0 || paddedMetadata != chunks[0].Virtual ||
            checked(etpf + paddedMetadata + sourcePadding) != chunks[0].Source)
            throw new InvalidDataException("ETPF tables do not match the source/virtual data boundary.");
        byte[] records = new byte[area]; input.ReadExactly(records);
        byte[] ids = new byte[checked(textures * 32)]; input.ReadExactly(ids);
        long keysEnd = checked(textures * 24L);
        if (keysEnd > (long)records.Length + ids.Length) throw new InvalidDataException("ETPF native asset-key array exceeds its metadata.");
        var native = new List<Native>(textures);
        for (int i = 0; i < textures; i++)
        {
            ct.ThrowIfCancellationRequested(); int p = i * 16;
            uint a = U32(records, p), b = U32(records, p + 4), c = U32(records, p + 8), d = U32(records, p + 12);
            int nonTail = (int)(b & 7), packedMips = (int)(a >> 28), mode = (int)(b >> 23);
            int width = (int)(a & 0x3FFF) + 1, height = (int)((a >> 14) & 0x3FFF) + 1, mips = packedMips + nonTail,
                format = (int)((b >> 3) & 255), depth = (int)((b >> 11) & 1023) + 1, dimension = (int)((b >> 21) & 3);
            long baseOffset = (long)(c & 0x1FFFFFUL) * 4096;
            string id = Convert.ToHexString(ids.AsSpan(i * 32, 32));
            int keyAt = checked(textures * 16 + i * 8);
            ulong assetKey = keyAt + 8 <= records.Length ? U64(records, keyAt) : keyAt >= records.Length
                ? U64(ids, keyAt - records.Length) : throw new InvalidDataException("ETPF asset key straddles an unsupported metadata boundary.");
            bool legacyTopOnlyName = LegacyTopOnlyName(width, height, format, packedMips + 1, b, c, d, paddedMetadata, virtualSize, chunks);
            bool auxiliaryMapping = (c >> 21) != 0 || d != 0;
            string? limitation = null; Layout? layout = null; int exported = 0, baseMip = 0;
            bool compactReadOnly = mode is 4 or 6 or 36 || mode == 2 && auxiliaryMapping; long stored = 0, ddsSize = 0;
            try
            {
                if (baseOffset == 0 || (c & 0x1FFFFF) == 0x1FFFFF) throw new NotSupportedException("This catalog record has no mapped allocation in this pool shard.");
                if (baseOffset < paddedMetadata || baseOffset >= virtualSize) throw new InvalidDataException("Texture virtual base exceeds the pool data.");
                if (dimension != 1 || depth != 1) throw new NotSupportedException("This ETPF array/volume layout has not been validated.");
                if (mode is 0 or 2)
                {
                    if (auxiliaryMapping && (mode != 2 || !ValidatedAuxiliaryProfile(width, height, mips, format, nonTail, c >> 21, d)))
                        throw new NotSupportedException("Auxiliary compact mip mapping is not decoded.");
                }
                else if (!compactReadOnly || !ValidatedCompactProfile(width, height, mips, format, nonTail, mode, c >> 21, d))
                    throw new NotSupportedException("This compact mip mapping profile has not been validated.");
                layout = SurfaceLayout(width, height, format, mips, mode == 0 ? 65536 : 4096);
                if (layout.Size > virtualSize - baseOffset) throw new InvalidDataException("Texture allocation exceeds the virtual pool.");
                long nextAllocation = virtualSize;
                for (int following = i + 1; following < textures; following++)
                {
                    long candidate = (long)(U32(records, following * 16 + 8) & 0x1FFFFF) * 4096;
                    if (candidate < baseOffset) throw new InvalidDataException("Texture allocations are not ordered.");
                    if (candidate > baseOffset) { nextAllocation = candidate; break; }
                }
                if (layout.Size != nextAllocation - baseOffset) throw new InvalidDataException("Texture footprint does not match its native allocation span.");
                int usableMipLimit = compactReadOnly ? nonTail : mips;
                if (compactReadOnly)
                {
                    if (nonTail < 1 || nonTail > layout.FirstTail) throw new NotSupportedException("No validated standalone compact mip range is available.");
                    long tailBytes = layout.Size - layout.Mips.Take(nonTail).Sum(m => (long)m.RegionSize);
                    if (tailBytes < 0) throw new InvalidDataException("Compact tail region exceeds its native allocation.");
                    for (int mip = nonTail - 1; mip >= 0; mip--)
                    {
                        if (layout.Mips[mip].Offset != tailBytes) throw new NotSupportedException("Compact standalone mip offsets are ambiguous.");
                        tailBytes += layout.Mips[mip].RegionSize;
                    }
                    if (tailBytes != layout.Size) throw new InvalidDataException("Compact standalone mip ranges do not cover their expected allocation window.");
                }
                var available = new bool[usableMipLimit];
                for (int mip = 0; mip < usableMipLimit; mip++)
                {
                    var level = layout.Mips[mip];
                    available[mip] = ChunksFor(chunks, baseOffset + level.Offset, level.RegionSize).All(x => x.Codec is 0 or 8);
                }
                baseMip = Array.FindIndex(available, usable => usable);
                if (baseMip < 0) throw new NotSupportedException("All source mips require an unavailable Xbox BCPack decoder.");
                if (baseMip == 0 && legacyTopOnlyName) exported = 1;
                else while (baseMip + exported < usableMipLimit && available[baseMip + exported]) exported++;
                var used = Enumerable.Range(baseMip, exported).SelectMany(mip => ChunksFor(chunks,
                    baseOffset + layout.Mips[mip].Offset, layout.Mips[mip].RegionSize)).Distinct().ToArray();
                stored = used.Sum(x => (long)x.Packed); ddsSize = 148;
                for (int mip = baseMip; mip < baseMip + exported; mip++) ddsSize += LinearBytes(layout.Mips[mip], layout.ElementBytes);
                if (ddsSize > MaxOutput) throw new NotSupportedException("DDS exceeds the 256MiB output limit.");
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
            { limitation = ex.Message; exported = baseMip = 0; stored = ddsSize = 0; }
            string formatName = RageTextureDecoder.GetTextureFormatName(format);
            string note = compactReadOnly ? $"Read-only compact profile: {nonTail} standalone mips and {packedMips} packed tail mips. Packed tail decoding and native replacement are unavailable. " : "";
            note += baseMip > 0 ? $"Source is {width} × {height} with {mips} mips. Export starts at source mip {baseMip}: {Math.Max(1, width >> baseMip)} × {Math.Max(1, height >> baseMip)}, with {exported} decoded mip levels. Other source mips remain unavailable. "
                : exported > 0 && exported < mips ? $"Source is {width} × {height} with {mips} mips. Export contains {exported} decoded mip levels starting at source mip 0: {width} × {height}. Other source mips remain unavailable. " : "";
            if (format is 10 or 34 or 41 or 54 or 95 or 96) note += "Floating-point channels clip to 0–1 for display; DDS preserves source values. ";
            if (format == 65) note += "Alpha mask displayed as grayscale. ";
            int previewMip = baseMip + RageTextureDecoder.ChoosePreviewMip(Math.Max(1, width >> baseMip), Math.Max(1, height >> baseMip), exported);
            if (previewMip > 0) note += $"Preview uses mip {previewMip}: {Math.Max(1, width >> previewMip)} × {Math.Max(1, height >> previewMip)}. ";
            string suffix = legacyTopOnlyName ? "_top_mip" : "";
            var info = new TtpfTextureInfo(i, $"texture_{i:D6}_{id[..16]}{suffix}.dds",
                width, height, format, formatName == "Unknown" ? $"DXGI {format}" : formatName, exported, depth, dimension, stored, ddsSize)
            { SourceMips = mips, NonTailMipCount = nonTail, PackedMipCount = packedMips, BaseMipLevel = baseMip,
                SupportsNativeReplacement = !compactReadOnly && limitation == null && baseMip == 0 && exported == mips,
                AssetKey = assetKey, Id = id, VirtualOffset = baseOffset, Limitation = limitation, PreviewNote = note.Length == 0 ? null : note.Trim() };
            native.Add(new(info, layout));
        }
        var metadata = new Dictionary<string, string>
        {
            ["Descriptor format"] = "RDS5 with embedded ETPF634; validated AMD 64KB_D and 4KB_S2D DDS exports",
            ["Texture names"] = "Generated ordinal/opaque-ID names; original filenames are unknown",
            ["Native binding key"] = "Validated64-bit AssetKey array immediately after fixed texture descriptors; Id is an opaque metadata token",
            ["Descriptor count"] = textures.ToString("N0"),
            ["Supported DDS exports"] = native.Count(x => x.Info.CanExtract).ToString("N0"),
            ["Top-mip-only exports"] = native.Count(x => x.Info.TopOnly).ToString("N0"),
            ["Lower-mip-only exports"] = native.Count(x => x.Info.CanExtract && x.Info.BaseMipLevel > 0).ToString("N0"),
            ["Source access"] = "Read-only; requested chunks only; zlib checksums checked on use",
            ["Remaining layouts"] = "Auxiliary compact mapping, arrays/volumes and BCPack-dependent mip levels remain unavailable"
        };
        var result = new TtpfInfo(path, fileSize, virtualSize, 634, native.Select(x => x.Info).ToArray(), metadata);
        return new(result, chunks, native);
    }

    internal static Native Select(Index source, int index)
    {
        if ((uint)index >= source.Native.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var n = source.Native[index]; if (!n.Info.CanExtract || n.Layout is null) throw new NotSupportedException(n.Info.Limitation ?? "Texture layout unavailable."); return n;
    }
    private static bool ValidatedAuxiliaryProfile(int width, int height, int mips, int format, int nonTail, uint meta, uint extra)
    {
        if (mips != 3 || nonTail != 3 || meta != 0x1A8 || extra != 0x01080008) return false;
        return width == 1024 && (height == 512 && format == 61 || height == 1024 &&
            RageTextureDecoder.IsBlockCompressed(format) && RageTextureDecoder.GetTextureElementBytes(format) == 8);
    }
    private static bool ValidatedCompactProfile(int width, int height, int mips, int format, int nonTail, int mode, uint meta, uint extra)
    {
        if (!RageTextureDecoder.IsBlockCompressed(format)) return false;
        int bytes = RageTextureDecoder.GetTextureElementBytes(format);
        return (mode, width, height, mips, nonTail, bytes, meta, extra) switch
        {
            (4, 1024, 1024, 9, 3, 8, 0x503, 0x00B00106) => true,
            (6, 512, 512, 8, 3, 8, 0x041, 0x00B800D4) => true,
            (4, 256, 256, 7, 1, 8, 0x103, 0) => true,
            (4, 512, 512, 8, 3, 16, 0x482, 0x015000F5) => true,
            (4, 1024, 512, 8, 3, 8, 0x482, 0x015000F5) => true,
            (4, 512, 1024, 8, 3, 8, 0x483, 0x013000F5) => true,
            (4, 128, 256, 6, 1, 8, 0x083, 0) => true,
            (4, 256, 512, 7, 1, 8, 0x167, 0) => true,
            (4, 512, 256, 7, 1, 8, 0x166, 0) => true,
            (4, 256, 256, 7, 1, 16, 0x166, 0) => true,
            (4, 1024, 512, 8, 3, 16, 0x504, 0x900106) => true,
            (4, 1024, 256, 7, 3, 16, 0x484, 0x11000F5) => true,
            (6, 1024, 256, 7, 3, 8, 0x42, 0x9800D4) => true,
            (6, 64, 1024, 5, 3, 8, 0x83, 0x3800B4) => true,
            (4, 512, 1024, 8, 3, 16, 0x503, 0xB00106) => true,
            (4, 512, 128, 6, 1, 16, 0x168, 0) => true,
            (6, 256, 1024, 7, 3, 8, 0x83, 0x3800D4) => true,
            (4, 1024, 128, 6, 1, 8, 0x168, 0) => true,
            (4, 2048, 256, 7, 3, 16, 0x508, 0x100106) => true,
            (4, 2048, 512, 8, 3, 8, 0x504, 0x900106) => true,
            (6, 512, 256, 7, 3, 16, 0x42, 0x9800D4) => true,
            (6, 1024, 128, 6, 3, 16, 0x84, 0x1800D4) => true,
            (4, 512, 128, 6, 1, 8, 0x104, 0) => true,
            (4, 256, 1024, 7, 3, 16, 0x483, 0x13000F5) => true,
            (6, 1024, 64, 5, 3, 16, 0x83, 0x3800B4) => true,
            (6, 256, 512, 7, 3, 16, 0x41, 0xB800D4) => true,
            (4, 128, 128, 6, 1, 16, 0x82, 0) => true,
            (4, 512, 64, 5, 1, 16, 0x108, 0) => true,
            (4, 256, 128, 6, 1, 16, 0x104, 0) => true,
            (6, 128, 1024, 6, 3, 16, 0x83, 0x3800D4) => true,
            (4, 512, 64, 5, 1, 8, 0x84, 0) => true,
            (4, 128, 512, 6, 1, 16, 0x167, 0) => true,
            (4, 256, 128, 6, 1, 8, 0x82, 0) => true,
            (4, 1024, 64, 5, 1, 8, 0x108, 0) => true,
            (6, 1024, 32, 4, 3, 16, 0x82, 0x5800B4) => true,
            (4, 128, 512, 6, 1, 8, 0x107, 0) => true,
            (4, 512, 2048, 8, 3, 8, 0x507, 0x300106) => true,
            (4, 256, 2048, 7, 3, 8, 0x507, 0x3000F5) => true,
            (4, 512, 32, 4, 1, 16, 0x107, 0) => true,
            (6, 128, 1024, 6, 3, 8, 0x83, 0x3800B4) => true,
            (6, 64, 1024, 5, 3, 16, 0x83, 0x3800B4) => true,
            (4, 256, 256, 5, 1, 16, 0x166, 0) => true,
            (4, 128, 256, 6, 1, 16, 0x103, 0) => true,
            (4, 256, 64, 5, 1, 16, 0x84, 0) => true,
            (4, 16, 256, 3, 1, 16, 0x83, 0) => true,
            (4, 256, 32, 4, 1, 16, 0x84, 0) => true,
            (4, 64, 512, 5, 1, 16, 0x107, 0) => true,
            (4, 2048, 256, 7, 3, 8, 0x484, 0x11000F5) => true,
            (4, 64, 512, 5, 1, 8, 0x107, 0) => true,
            (4, 64, 256, 5, 1, 8, 0x83, 0) => true,
            (6, 256, 512, 9, 3, 16, 0x41, 0xB800D4) => true,
            (6, 32, 1024, 4, 3, 16, 0x82, 0x5800B4) => true,
            (4, 512, 512, 3, 3, 8, 0x502, 0xD00006) => true,
            (36, 1024, 1024, 9, 3, 8, 0x503, 0xB00106) => true,
            (36, 256, 256, 7, 1, 8, 0x103, 0) => true,
            _ => false
        };
    }
    private static bool LegacyTopOnlyName(int width, int height, int format, int oldMips, uint b, uint c, uint d,
        long metadata, long virtualSize, IReadOnlyList<Chunk> chunks)
    {
        try
        {
            if ((b >> 23) != 0 || ((b >> 11) & 1023) != 0 || ((b >> 21) & 3) != 1 ||
                (c >> 22) != 0 || d != 0 || oldMips <= 1) return false;
            long offset = (long)(c & 0x3FFFFF) * 4096;
            if (offset < metadata || offset >= virtualSize) return false;
            var layout = SurfaceLayout(width, height, format, oldMips);
            if ((b & 7) != layout.FirstTail || layout.Size > virtualSize - offset) return false;
            if (ChunksFor(chunks, offset, layout.Size).All(x => x.Codec is 0 or 8)) return false;
            var top = layout.Mips[0];
            return ChunksFor(chunks, offset + top.Offset, top.RegionSize).All(x => x.Codec is 0 or 8);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or ArgumentException or OverflowException) { return false; }
    }
    internal static List<Chunk> ChunksFor(IReadOnlyList<Chunk> chunks, long start, int count)
    {
        long end = checked(start + count), covered = start; var result = new List<Chunk>();

        int lo = 0, hi = chunks.Count;
        while (lo < hi) { int mid = (lo + hi) / 2; if (chunks[mid].Virtual + chunks[mid].Size <= start) lo = mid + 1; else hi = mid; }
        for (int i = lo; i < chunks.Count && chunks[i].Virtual < end; i++)
        {
            var c = chunks[i]; if (c.Virtual > covered) throw new InvalidDataException("Texture crosses an unmapped virtual range.");
            result.Add(c); covered = Math.Min(end, c.Virtual + c.Size);
        }
        if (covered != end) throw new InvalidDataException("Texture range is not completely mapped."); return result;
    }

    private static byte[] ReadMip(FileStream input, Index source, Native native, int mip, Dictionary<int, byte[]> cache, CancellationToken ct)
    {
        var layout = native.Layout!; var m = layout.Mips[mip];
        long start = native.Info.VirtualOffset + m.Offset; byte[] tiled = new byte[m.RegionSize];
        foreach (var chunk in ChunksFor(source.Chunks, start, m.RegionSize))
        {
            ct.ThrowIfCancellationRequested(); int key = source.Chunks.IndexOfReference(chunk);
            if (!cache.TryGetValue(key, out var decoded)) { decoded = DecodeChunk(input, chunk, ct); cache.Add(key, decoded); }
            long from = Math.Max(start, chunk.Virtual), to = Math.Min(start + m.RegionSize, chunk.Virtual + chunk.Size);
            decoded.AsSpan((int)(from - chunk.Virtual), (int)(to - from)).CopyTo(tiled.AsSpan((int)(from - start)));
        }
        byte[] linear = new byte[LinearBytes(m, layout.ElementBytes)];
        for (int y = 0; y < m.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < m.Width; x++)
            {
                int offset = TiledOffset(layout, m, x, y);
                if (offset > tiled.Length - layout.ElementBytes) throw new InvalidDataException("Tiled address exceeds its source range.");
                tiled.AsSpan(offset, layout.ElementBytes).CopyTo(linear.AsSpan((y * m.Width + x) * layout.ElementBytes));
            }
        }
        return linear;
    }

    internal static byte[] DecodeChunk(FileStream input, Chunk chunk, CancellationToken ct)
    {
        if (chunk.Codec is not (0 or 8)) throw new NotSupportedException("An Xbox BCPack decompression backend is required.");
        byte[] packed = new byte[chunk.Packed]; input.Position = chunk.Source; input.ReadExactly(packed);
        if (chunk.Codec == 0) return packed;
        if (packed.Length < 6) throw new InvalidDataException("Truncated zlib chunk.");
        using var data = new MemoryStream(packed, false); using var zlib = new ZLibStream(data, CompressionMode.Decompress);
        byte[] decoded = new byte[chunk.Size]; int copied = 0; uint a = 1, b = 0;
        while (copied < decoded.Length)
        {
            ct.ThrowIfCancellationRequested(); int n = zlib.Read(decoded.AsSpan(copied, Math.Min(4096, decoded.Length - copied)));
            if (n == 0) throw new InvalidDataException("Zlib output is shorter than its virtual chunk.");
            for (int i = copied; i < copied + n; i++) { a += decoded[i]; b += a; } a %= 65521; b %= 65521; copied += n;
        }
        if (zlib.ReadByte() != -1 || BinaryPrimitives.ReadUInt32BigEndian(packed.AsSpan(packed.Length - 4)) != ((b << 16) | a))
            throw new InvalidDataException("Zlib output length/checksum disagrees with its chunk.");
        return decoded;
    }



    internal static byte[] RetileReplacement(FileStream input, Index source, Native native, byte[] dds, CancellationToken ct)
    {
        var t = native.Info; var layout = native.Layout ?? throw new NotSupportedException("Texture layout is unavailable.");
        if (!t.SupportsNativeReplacement) throw new NotSupportedException("Native replacement is not validated for this texture storage profile or partial mip chain.");
        if (!t.CanExtract || t.BaseMipLevel != 0 || t.Mips != t.SourceMips || t.Dimension != 1 || t.Depth != 1)
            throw new NotSupportedException("Pool replacement requires a complete supported 2D mip chain; top-only exports are unavailable for replacement.");
        var imported = DdsTextureReader.Inspect(dds);
        if (imported.Width != t.Width || imported.Height != t.Height || imported.DxgiFormat != t.DxgiFormat ||
            imported.Mips != t.SourceMips || imported.Depth != 1 || imported.Dimension != 1 ||
            imported.ResourceDimension != 3 || imported.ArraySize != 1 || imported.SliceCount != 1)
            throw new InvalidDataException("DDS dimensions, DXGI format, complete mip count and 2D shape must match the native pool descriptor exactly.");
        if (imported.AlphaMode is not (0 or 1)) throw new NotSupportedException("Replacement DDS requires unknown or straight alpha semantics.");
        byte[] tiled = new byte[layout.Size];
        foreach (var chunk in ChunksFor(source.Chunks, t.VirtualOffset, layout.Size))
        {
            ct.ThrowIfCancellationRequested(); byte[] decoded = DecodeChunk(input, chunk, ct);
            long lo = Math.Max(t.VirtualOffset, chunk.Virtual), hi = Math.Min(t.VirtualOffset + layout.Size, chunk.Virtual + chunk.Size);
            decoded.AsSpan(checked((int)(lo - chunk.Virtual)), checked((int)(hi - lo))).CopyTo(tiled.AsSpan(checked((int)(lo - t.VirtualOffset))));
        }
        for (int mip = 0; mip < t.SourceMips; mip++)
        {
            var m = layout.Mips[mip]; var linear = DdsTextureReader.GetMipPlane(dds, imported, mip, 0);
            for (int y = 0; y < m.Height; y++)
            {
                ct.ThrowIfCancellationRequested();
                for (int x = 0; x < m.Width; x++)
                {
                    int at = checked(m.Offset + TiledOffset(layout, m, x, y));
                    if (at < 0 || at > tiled.Length - layout.ElementBytes) throw new InvalidDataException("Retiled address exceeds the native allocation.");
                    linear.Slice((y * m.Width + x) * layout.ElementBytes, layout.ElementBytes).CopyTo(tiled.AsSpan(at, layout.ElementBytes));
                }
            }
        }
        return tiled;
    }

    private static int TiledOffset(Layout layout, Mip mip, int x, int y)
    {
        int offset = checked(((y / layout.BlockHeight) * (mip.PaddedWidth / layout.BlockWidth) + x / layout.BlockWidth) * layout.TileBytes);
        var pattern = (layout.TileBytes == 4096 ? FourKbPatterns : Patterns)[Log(layout.ElementBytes)];
        for (int bit = 0; bit < pattern.Length; bit++)
        {
            int s = pattern[bit];
            if (s > 0) offset |= (((x + mip.TailX) >> (s - 1)) & 1) << bit;
            else if (s < 0) offset |= (((y + mip.TailY) >> (-s - 1)) & 1) << bit;
        }
        return offset;
    }

    private static Layout SurfaceLayout(int w, int h, int format, int mips, int tileBytes = 65536)
    {
        int levels = 1; for (int v = Math.Max(w, h); v > 1; v >>= 1) levels++;
        if (mips < 1 || mips > levels) throw new InvalidDataException("Invalid source mip count.");
        int eb = RageTextureDecoder.GetTextureElementBytes(format), log = Log(eb), px = RageTextureDecoder.IsBlockCompressed(format) ? 4 : 1;
        int halfBlockBits = tileBytes == 4096 ? 6 : 8, maximumTailLevels = tileBytes == 4096 ? 8 : 12;
        int bw = 1 << (halfBlockBits - log / 2), bh = 1 << (halfBlockBits - (log + 1) / 2);
        (int mw, int mh) = log switch { 0 => (16, 16), 1 => (16, 8), 2 => (8, 8), 3 => (8, 4), _ => (4, 4) };
        int ew = (w + px - 1) / px, eh = (h + px - 1) / px, tail = mips;
        var sizes = new List<int>(); var ms = new List<Mip>();
        for (int i = 0; i < mips; i++)
        {
            int wc = Math.Max(1, (ew + (1 << i) - 1) >> i), hc = Math.Max(1, (eh + (1 << i) - 1) >> i);
            if (mips > 1 && wc <= bw / 2 && hc <= bh && mips - i <= maximumTailLevels) { tail = i; break; }
            int pw = (int)Align(wc, bw), ph = (int)Align(hc, bh), size = checked(pw * ph * eb);
            sizes.Add(size); ms.Add(new(Elements(w, i, px), Elements(h, i, px), pw, 0, size, 0, 0));
        }
        int off = tail < mips ? tileBytes : 0;
        for (int i = tail - 1; i >= 0; i--) { ms[i] = ms[i] with { Offset = off }; off = checked(off + sizes[i]); }
        for (int i = tail; i < mips; i++)
        {
            int entry = maximumTailLevels - 1 - (i - tail), moff = entry > 6 ? 16 << entry : entry << 8, x = 0, y = 0;
            for (int bit = 0; bit < 6; bit++) { x |= ((moff >> (9 + 2 * bit)) & 1) << bit; y |= ((moff >> (8 + 2 * bit)) & 1) << bit; }
            ms.Add(new(Elements(w, i, px), Elements(h, i, px), bw, 0, tileBytes, x * mw, y * mh));
        }
        if (off < 1 || off > MaxOutput) throw new NotSupportedException("Texture exceeds the 256MiB resource limit.");
        return new(off, bw, bh, tail, eb, ms) { TileBytes = tileBytes };
    }
    private static int LinearBytes(Mip mip, int eb) => checked(mip.Width * mip.Height * eb);
    private static int Elements(int pixels, int mip, int px) => (Math.Max(1, pixels >> mip) + px - 1) / px;
    private static int Log(int eb) => eb switch { 1 => 0, 2 => 1, 4 => 2, 8 => 3, 16 => 4, _ => throw new InvalidDataException("Invalid element stride.") };
    private static long Align(long value, int boundary) => checked((value + boundary - 1) / boundary * boundary);
    private static long SourceOffset(ulong value) => (long)(value & 0x3FFFFFF) * 256;
    private static long VirtualOffset(ulong value) => (long)((value >> 30) & 0x3FFFFF) * 4096;
    private static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at, 4));
    private static ulong U64(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(at, 8));
    private static int IndexOfReference(this IReadOnlyList<Chunk> chunks, Chunk chunk)
    {
        int lo = 0, hi = chunks.Count - 1;
        while (lo <= hi) { int m = (lo + hi) / 2; if (chunks[m].Source == chunk.Source) return m; if (chunks[m].Source < chunk.Source) lo = m + 1; else hi = m - 1; }
        throw new InvalidDataException("Chunk is not part of the validated index.");
    }
}
