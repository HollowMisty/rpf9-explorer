using System.Buffers.Binary;
using System.IO.Compression;
using RpfPreview.Core;

// Asset-free synthetic RDS5/ETPF634 smoke tests for compact 2D streaming profiles and packed mip tails.
var directory = Directory.CreateTempSubdirectory("rpf9-ttpf-smoke-");
try
{
    RunCompactTailAndTileSmoke(directory.FullName);
    RunNegativeControls(directory.FullName);
    Console.WriteLine("Compact 2D streaming profiles, packed mip tails, BCPack fallback, and negative controls passed.");
}
finally
{
    if (Directory.Exists(directory.FullName)) directory.Delete(true);
}

static void RunCompactTailAndTileSmoke(string dir)
{
    // 1. Compact 4 KiB mode=4, nt=1, 32x32 R8G8B8A8_UNORM (fmt 28), 6 mips (1 standalone + 5 packed tail):
    //    SW_4KB_S eb=4 -> bw=32, bh=32 -> mip 0 = 4096 B, packed tail (mips 1..5) = 4096 B, total = 8192 B (2 chunks of 1 page).
    byte[][] mode4Mips = MakeRgbaMips(32, 32, 6, seed: 0x10);
    byte[] mode4Tiled = Tile2D(32, 32, 4, 6, 4096, mode4Mips);
    Assert(mode4Tiled.Length == 8192, "Unexpected mode=4 tiled size.");

    // 2. Compact 4 KiB mode=4, nt=3, alignGroup=2 (c0+c1+pad=16), 128x128 R8G8B8A8_UNORM (fmt 28), 8 mips:
    //    mip0=64KB (16 pages), mip1=16KB (4 pages), mip2=4KB (1 page), tail(mips3..7)=4KB (1 page). Total=22 pages.
    //    Split into 3 chunks: [2 pages (tail+mip2), 4 pages (mip1), 16 pages (mip0)] -> codes [2, 4, 11], alignGroup=2, pad=10 (2+4+10=16).
    byte[][] mode4Nt3Mips = MakeRgbaMips(128, 128, 8, seed: 0x40);
    byte[] mode4Nt3Tiled = Tile2D(128, 128, 4, 8, 4096, mode4Nt3Mips);
    Assert(mode4Nt3Tiled.Length == 22 * 4096, "Unexpected mode=4 nt=3 tiled size.");

    // 3. Compact 4 KiB mode=6, nt=3, alignGroup=3 (c0+c1+pad=8), 64x64 R8G8B8A8_UNORM (fmt 28), 7 mips:
    //    mip0=16KB (4p), mip1=4KB (1p), tail(mips2..6)=4KB (1p). Total=6 pages.
    //    Split into 3 chunks: [1p, 1p, 4p] -> codes [1, 1, 4], alignGroup=3, pad=6 (1+1+6=8).
    byte[][] mode6Mips = MakeRgbaMips(64, 64, 7, seed: 0x70);
    byte[] mode6Tiled = Tile2D(64, 64, 4, 7, 4096, mode6Mips);
    Assert(mode6Tiled.Length == 6 * 4096, "Unexpected mode=6 tiled size.");

    // 4. 64 KiB tile mode=1 (non-aux, SW_64KB_D), 128x128 R8G8B8A8_UNORM (fmt 28), 8 mips:
    //    SW_64KB_D eb=4 -> bw=128, bh=128 -> mip0=64KB (16p), tail(mips1..7)=64KB (16p). Total=128KB (32p).
    byte[][] mode1Mips = MakeRgbaMips(128, 128, 8, seed: 0x90);
    byte[] mode1Tiled = Tile2D(128, 128, 4, 8, 65536, mode1Mips);
    Assert(mode1Tiled.Length == 32 * 4096, "Unexpected mode=1 tiled size.");

    // 5. Mixed BCPack (mip0) + zlib (lower mips) on mode=4 nt=1 32x32 -> must fall back to BaseMipLevel=1 and export mips 1..5!
    var entries = new List<PoolEntry>
    {
        new(32, 32, 28, 1, 1, Mode: 4, NonTail: 1, PackedMips: 5,
            StreamMeta(codes: [1, 1]),
            [ChunkSpec.Zlib(mode4Tiled.AsSpan(0, 4096)), ChunkSpec.Zlib(mode4Tiled.AsSpan(4096, 4096))]),
        new(128, 128, 28, 1, 1, Mode: 4, NonTail: 3, PackedMips: 5,
            StreamMeta(codes: [2, 4, 11], alignGroup: 2, padPages: 10),
            [ChunkSpec.Zlib(mode4Nt3Tiled.AsSpan(0, 8192)), ChunkSpec.Zlib(mode4Nt3Tiled.AsSpan(8192, 16384)), ChunkSpec.Zlib(mode4Nt3Tiled.AsSpan(24576, 65536))]),
        new(64, 64, 28, 1, 1, Mode: 6, NonTail: 3, PackedMips: 4,
            StreamMeta(codes: [1, 1, 4], alignGroup: 3, padPages: 6),
            [ChunkSpec.Zlib(mode6Tiled.AsSpan(0, 4096)), ChunkSpec.Zlib(mode6Tiled.AsSpan(4096, 4096)), ChunkSpec.Zlib(mode6Tiled.AsSpan(8192, 16384))]),
        new(128, 128, 28, 1, 1, Mode: 1, NonTail: 1, PackedMips: 7, (0u, 0u),
            [ChunkSpec.Zlib(mode1Tiled)]),
        new(32, 32, 28, 1, 1, Mode: 4, NonTail: 1, PackedMips: 5,
            StreamMeta(codes: [1, 1]),
            [ChunkSpec.Zlib(mode4Tiled.AsSpan(0, 4096)), ChunkSpec.BcPack(4096)])
    };

    string path = Path.Combine(dir, "compact_positive.ttpf");
    WritePool(path, entries);
    var pool = TtpfReader.Inspect(path);
    Assert(pool.Textures.Count == 5, "Expected 5 inspected textures.");

    VerifyFullExport(path, pool.Textures[0], mode4Mips, expectedMipsCount: 6, expectNativeReplacement: false);
    VerifyFullExport(path, pool.Textures[1], mode4Nt3Mips, expectedMipsCount: 8, expectNativeReplacement: false);
    VerifyFullExport(path, pool.Textures[2], mode6Mips, expectedMipsCount: 7, expectNativeReplacement: false);
    VerifyFullExport(path, pool.Textures[3], mode1Mips, expectedMipsCount: 8, expectNativeReplacement: false);

    // Entry 4: mip 0 is BCPack, packed tail (mips 1..5) is zlib -> partial fallback starting at BaseMipLevel=1.
    var fallback = pool.Textures[4];
    Assert(fallback.CanExtract && fallback.BaseMipLevel == 1 && fallback.Mips == 5 && fallback.ExportWidth == 16 && fallback.ExportHeight == 16,
        $"Expected partial lower-mip fallback (base=1, mips=5), got base={fallback.BaseMipLevel}, mips={fallback.Mips}, lim={fallback.Limitation}");
    byte[] fallbackDds = TtpfReader.CreateDds(path, 4);
    var fallbackInfo = DdsTextureReader.Inspect(fallbackDds);
    Assert(fallbackInfo.Width == 16 && fallbackInfo.Height == 16 && fallbackInfo.Mips == 5, "Fallback DDS metadata mismatch.");
    int fallbackOff = fallbackInfo.DataOffset;
    for (int m = 0; m < 5; m++)
    {
        byte[] exp = mode4Mips[m + 1];
        Assert(fallbackDds.AsSpan(fallbackOff, exp.Length).SequenceEqual(exp), $"Fallback mip {m + 1} plane bytes differed.");
        fallbackOff += exp.Length;
    }
    Assert(fallbackOff == fallbackDds.Length, "Fallback DDS byte length mismatch.");
    Assert(TtpfReader.DecodeTexturePixels(path, 4).Bgra32.SequenceEqual(DdsTextureReader.DecodeTexturePixels(fallbackDds).Bgra32),
        "Fallback preview pixels disagreed with DDS decode.");

    // Verify ArchiveReader opens the pool without stale limitation text.
    var doc = ArchiveReader.Open(path);
    Assert(doc.Entries.Count == 5 && !doc.Limitation!.Contains("auxiliary mapping", StringComparison.OrdinalIgnoreCase),
        "ArchiveReader limitation still mentions auxiliary mapping.");
}

static void RunNegativeControls(string dir)
{
    byte[][] mips = MakeRgbaMips(32, 32, 6, seed: 0x22);
    byte[] tiled = Tile2D(32, 32, 4, 6, 4096, mips);
    byte[][] nt3Mips = MakeRgbaMips(64, 64, 7, seed: 0x33);
    byte[] nt3Tiled = Tile2D(64, 64, 4, 7, 4096, nt3Mips);

    var entries = new List<PoolEntry>
    {
        // 0: Wrong chunk code sum (declares [2] instead of [1, 1] for 2-chunk allocation).
        new(32, 32, 28, 1, 1, Mode: 4, NonTail: 1, PackedMips: 5,
            StreamMeta(codes: [2]),
            [ChunkSpec.Zlib(tiled.AsSpan(0, 4096)), ChunkSpec.Zlib(tiled.AsSpan(4096, 4096))]),
        // 1: Invalid alignment padding for nt=3 mode=6 (c0+c1+pad = 1+1+5 = 7 != 8).
        new(64, 64, 28, 1, 1, Mode: 6, NonTail: 3, PackedMips: 4,
            StreamMeta(codes: [1, 1, 4], alignGroup: 3, padPages: 5),
            [ChunkSpec.Zlib(nt3Tiled.AsSpan(0, 4096)), ChunkSpec.Zlib(nt3Tiled.AsSpan(4096, 4096)), ChunkSpec.Zlib(nt3Tiled.AsSpan(8192, 16384))]),
        // 2: Reserved bits set in streamFlags ((d >> 9) & 0x3FF != 0).
        new(32, 32, 28, 1, 1, Mode: 4, NonTail: 1, PackedMips: 5,
            (StreamMeta(codes: [1, 1]).Meta, StreamMeta(codes: [1, 1]).Extra | (1u << 10)),
            [ChunkSpec.Zlib(tiled.AsSpan(0, 4096)), ChunkSpec.Zlib(tiled.AsSpan(4096, 4096))]),
        // 3: All BCPack chunks -> must reject with BCPack limitation.
        new(32, 32, 28, 1, 1, Mode: 4, NonTail: 1, PackedMips: 5,
            StreamMeta(codes: [1, 1]),
            [ChunkSpec.BcPack(4096), ChunkSpec.BcPack(4096)]),
        // 4: Valid mode=4 compact texture to assert native replacement is blocked.
        new(32, 32, 28, 1, 1, Mode: 4, NonTail: 1, PackedMips: 5,
            StreamMeta(codes: [1, 1]),
            [ChunkSpec.Zlib(tiled.AsSpan(0, 4096)), ChunkSpec.Zlib(tiled.AsSpan(4096, 4096))])
    };

    string path = Path.Combine(dir, "compact_negative.ttpf");
    WritePool(path, entries);
    var pool = TtpfReader.Inspect(path);
    Assert(!pool.Textures[0].CanExtract, "Mismatched chunk page codes were accepted.");
    Assert(!pool.Textures[1].CanExtract, "Invalid nt=3 alignment padding was accepted.");
    Assert(!pool.Textures[2].CanExtract, "Non-zero reserved streamFlags bits were accepted.");
    Assert(!pool.Textures[3].CanExtract && pool.Textures[3].Limitation!.Contains("BCPack"), "All-BCPack texture was not rejected with BCPack reason.");
    Assert(pool.Textures[4].CanExtract && !pool.Textures[4].SupportsNativeReplacement, "Compact profile unexpectedly enabled native replacement.");

    // Verify native pool replacement refuses compact profile even with matching DDS.
    byte[] validDds = TtpfReader.CreateDds(path, 4);
    bool rejected = false;
    try
    {
        Rds5TextureWriter.ExportReplacementCopy(path, 4, validDds, Path.Combine(dir, "out.ttpf"));
    }
    catch (NotSupportedException) { rejected = true; }
    Assert(rejected, "Rds5TextureWriter did not reject native replacement on compact profile.");
}

static void VerifyFullExport(string path, TtpfTextureInfo info, byte[][] expectedMips, int expectedMipsCount, bool expectNativeReplacement)
{
    Assert(info.CanExtract, $"Entry {info.Index} failed to extract: {info.Limitation}");
    Assert(info.BaseMipLevel == 0 && info.Mips == expectedMipsCount, $"Entry {info.Index} mip count mismatch: base={info.BaseMipLevel}, mips={info.Mips}");
    Assert(info.SupportsNativeReplacement == expectNativeReplacement, $"Entry {info.Index} SupportsNativeReplacement mismatch.");
    byte[] dds = TtpfReader.CreateDds(path, info.Index);
    var parsed = DdsTextureReader.Inspect(dds);
    Assert(parsed.Width == info.Width && parsed.Height == info.Height && parsed.Mips == expectedMipsCount, $"Entry {info.Index} DDS header mismatch.");
    int off = parsed.DataOffset;
    for (int m = 0; m < expectedMipsCount; m++)
    {
        byte[] exp = expectedMips[m];
        Assert(dds.AsSpan(off, exp.Length).SequenceEqual(exp), $"Entry {info.Index} mip {m} linear bytes differed from expected source pattern.");
        off += exp.Length;
    }
    Assert(off == dds.Length, $"Entry {info.Index} DDS payload length mismatch.");
    Assert(TtpfReader.DecodeTexturePixels(path, info.Index).Bgra32.SequenceEqual(DdsTextureReader.DecodeTexturePixels(dds).Bgra32),
        $"Entry {info.Index} preview decode disagreed with DDS decode.");
}

static (uint Meta, uint Extra) StreamMeta(int[] codes, int alignGroup = 0, int padPages = 0)
{
    ulong packed = 0;
    for (int k = 0; k < codes.Length; k++) packed |= ((ulong)codes[k] & 0x1FUL) << (5 * k);
    uint meta = (uint)(packed & 0x7FFUL);
    uint extra = (uint)((packed >> 11) & 0x1FFUL) | ((uint)(alignGroup & 3) << 19) | ((uint)padPages << 21);
    return (meta, extra);
}

static byte[][] MakeRgbaMips(int w, int h, int mips, int seed)
{
    var result = new byte[mips][];
    for (int m = 0; m < mips; m++)
    {
        int mw = Math.Max(1, w >> m), mh = Math.Max(1, h >> m);
        byte[] data = new byte[mw * mh * 4];
        for (int y = 0; y < mh; y++)
            for (int x = 0; x < mw; x++)
            {
                int o = (y * mw + x) * 4;
                data[o] = (byte)((seed + m * 29 + x * 5) & 0xFF);
                data[o + 1] = (byte)((seed + m * 17 + y * 7) & 0xFF);
                data[o + 2] = (byte)((x ^ y ^ (m * 13) ^ seed) & 0xFF);
                data[o + 3] = 255;
            }
        result[m] = data;
    }
    return result;
}

static byte[] Tile2D(int w, int h, int eb, int mips, int tileBytes, byte[][] linearMips)
{
    int[][] fourKb = [
        [1,2,3,4,-1,-2,-3,-4,-5,5,-6,6],
        [0,1,2,3,-1,-2,-3,4,-4,5,-5,6],
        [0,0,1,2,-1,-2,-3,3,-4,4,-5,5],
        [0,0,0,1,-1,-2,2,3,-3,4,-4,5],
        [0,0,0,0,-1,-2,1,2,-3,3,-4,4]
    ];
    int[][] sixtyFourKb = [
        [1,2,3,-2,-1,-3,4,-4,-5,5,-6,6,-7,7,-8,8],
        [0,1,2,3,-1,-2,-3,4,-4,5,-5,6,-6,7,-7,8],
        [0,0,1,2,-1,-2,-3,3,-4,4,-5,5,-6,6,-7,7],
        [0,0,0,1,-1,2,3,-2,-3,4,-4,5,-5,6,-6,7],
        [0,0,0,0,1,-1,2,-2,-3,3,-4,4,-5,5,-6,6]
    ];
    int log = eb switch { 1 => 0, 2 => 1, 4 => 2, 8 => 3, _ => 4 };
    int halfBits = tileBytes == 4096 ? 6 : 8, maxTail = tileBytes == 4096 ? 8 : 12;
    int bw = 1 << (halfBits - log / 2), bh = 1 << (halfBits - (log + 1) / 2);
    (int mw, int mh) = log switch { 0 => (16, 16), 1 => (16, 8), 2 => (8, 8), 3 => (8, 4), _ => (4, 4) };
    int tail = mips;
    var mipInfo = new List<(int W, int H, int Pw, int Off, int Size, int Tx, int Ty)>();
    var sizes = new List<int>();
    for (int i = 0; i < mips; i++)
    {
        int wc = Math.Max(1, w >> i), hc = Math.Max(1, h >> i);
        if (mips > 1 && wc <= bw / 2 && hc <= bh && mips - i <= maxTail) { tail = i; break; }
        int pw = (wc + bw - 1) / bw * bw, ph = (hc + bh - 1) / bh * bh, size = pw * ph * eb;
        sizes.Add(size); mipInfo.Add((wc, hc, pw, 0, size, 0, 0));
    }
    int off = tail < mips ? tileBytes : 0;
    for (int i = tail - 1; i >= 0; i--) { var cur = mipInfo[i]; mipInfo[i] = cur with { Off = off }; off += sizes[i]; }
    for (int i = tail; i < mips; i++)
    {
        int entry = maxTail - 1 - (i - tail), moff = entry > 6 ? 16 << entry : entry << 8, tx = 0, ty = 0;
        for (int bit = 0; bit < 6; bit++) { tx |= ((moff >> (9 + 2 * bit)) & 1) << bit; ty |= ((moff >> (8 + 2 * bit)) & 1) << bit; }
        mipInfo.Add((Math.Max(1, w >> i), Math.Max(1, h >> i), bw, 0, tileBytes, tx * mw, ty * mh));
    }
    byte[] tiled = new byte[off];
    var pattern = (tileBytes == 4096 ? fourKb : sixtyFourKb)[log];
    for (int m = 0; m < mips; m++)
    {
        var mi = mipInfo[m];
        for (int y = 0; y < mi.H; y++)
            for (int x = 0; x < mi.W; x++)
            {
                int addr = ((y / bh) * (mi.Pw / bw) + x / bw) * tileBytes;
                for (int bit = 0; bit < pattern.Length; bit++)
                {
                    int s = pattern[bit];
                    if (s > 0) addr |= (((x + mi.Tx) >> (s - 1)) & 1) << bit;
                    else if (s < 0) addr |= (((y + mi.Ty) >> (-s - 1)) & 1) << bit;
                }
                linearMips[m].AsSpan((y * mi.W + x) * eb, eb).CopyTo(tiled.AsSpan(mi.Off + addr, eb));
            }
    }
    return tiled;
}

static void WritePool(string path, List<PoolEntry> entries)
{
    int textures = entries.Count;
    int area = textures * 24;
    long metadataBytes = 16L + area + textures * 32L;
    long paddedMetadata = (metadataBytes + 4095) / 4096 * 4096;

    var chunks = new List<(byte[] Payload, int VirtualSize, int Codec, int Pad)>();
    var entryBases = new long[textures];
    long cursor = paddedMetadata;
    for (int i = 0; i < textures; i++)
    {
        entryBases[i] = cursor;
        foreach (var ch in entries[i].Chunks)
        {
            byte[] payload = ch.Codec == 8 ? CompressZlib(ch.Data) : ch.Data;
            int alignedLen = (payload.Length + 255) / 256 * 256;
            int pad = alignedLen - payload.Length;
            byte[] padded = new byte[alignedLen];
            payload.CopyTo(padded, 0);
            chunks.Add((padded, ch.VirtualSize, ch.Codec, pad));
            cursor += ch.VirtualSize;
        }
    }
    long virtualSize = cursor;
    uint count = (uint)(chunks.Count + 2);
    long tableEnd = 24L + (count - 1) * 8L;
    long etpf = (tableEnd + 255) / 256 * 256;
    long chunkStart = etpf + paddedMetadata;

    byte[] records = new byte[area];
    byte[] ids = new byte[textures * 32];
    for (int i = 0; i < textures; i++)
    {
        var e = entries[i];
        uint a = (uint)(e.Width - 1) | ((uint)(e.Height - 1) << 14) | ((uint)e.PackedMips << 28);
        uint b = (uint)(e.NonTail & 7) | ((uint)(e.Format & 0x7F) << 3) | ((uint)((e.Depth - 1) & 0x7FF) << 10) | ((uint)(e.Dimension & 3) << 21) | ((uint)e.Mode << 23);
        uint c = (uint)(entryBases[i] / 4096) | (e.Stream.Meta << 21);
        uint d = e.Stream.Extra;
        int p = i * 16;
        BinaryPrimitives.WriteUInt32LittleEndian(records.AsSpan(p), a);
        BinaryPrimitives.WriteUInt32LittleEndian(records.AsSpan(p + 4), b);
        BinaryPrimitives.WriteUInt32LittleEndian(records.AsSpan(p + 8), c);
        BinaryPrimitives.WriteUInt32LittleEndian(records.AsSpan(p + 12), d);
        BinaryPrimitives.WriteUInt64LittleEndian(records.AsSpan(textures * 16 + i * 8), 0x1000UL + (ulong)i);
        ids[i * 32] = (byte)(i + 1);
    }

    using var fs = File.Create(path);
    byte[] header = new byte[24];
    "RDS5"u8.CopyTo(header);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), count);
    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), (22UL << 56) | (ulong)virtualSize);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (count + 31) / 32);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 0);
    fs.Write(header);

    long phys = chunkStart, virt = paddedMetadata;
    byte[] table = new byte[(count - 1) * 8];
    for (int i = 0; i < chunks.Count; i++)
    {
        var ch = chunks[i];
        ulong word = ((ulong)(phys / 256)) | ((ulong)ch.Codec << 26) | ((ulong)(virt / 4096) << 30) | ((ulong)ch.Pad << 52);
        BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(i * 8), word);
        phys += ch.Payload.Length;
        virt += ch.VirtualSize;
    }
    ulong sentinel = ((ulong)(phys / 256)) | ((ulong)(virt / 4096) << 30);
    BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(chunks.Count * 8), sentinel);
    fs.Write(table);
    fs.Write(new byte[etpf - tableEnd]);

    byte[] etpfHeader = new byte[16];
    "ETPF"u8.CopyTo(etpfHeader);
    BinaryPrimitives.WriteUInt32LittleEndian(etpfHeader.AsSpan(4), 634);
    BinaryPrimitives.WriteUInt32LittleEndian(etpfHeader.AsSpan(8), (uint)textures);
    BinaryPrimitives.WriteUInt32LittleEndian(etpfHeader.AsSpan(12), (uint)area);
    fs.Write(etpfHeader);
    fs.Write(records);
    fs.Write(ids);
    fs.Write(new byte[paddedMetadata - metadataBytes]);

    foreach (var ch in chunks) fs.Write(ch.Payload);
}

static byte[] CompressZlib(byte[] raw)
{
    using var ms = new MemoryStream();
    using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
    return ms.ToArray();
}

static void Assert(bool cond, string message) { if (!cond) throw new Exception(message); }

sealed record ChunkSpec(byte[] Data, int VirtualSize, int Codec)
{
    public static ChunkSpec Zlib(ReadOnlySpan<byte> raw) => new(raw.ToArray(), raw.Length, 8);
    public static ChunkSpec BcPack(int virtualSize) => new(new byte[256], virtualSize, 1);
}

sealed record PoolEntry(int Width, int Height, int Format, int Depth, int Dimension, int Mode, int NonTail, int PackedMips,
    (uint Meta, uint Extra) Stream, List<ChunkSpec> Chunks);
