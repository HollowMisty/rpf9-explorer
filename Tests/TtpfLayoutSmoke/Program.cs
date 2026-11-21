using System.Buffers.Binary;
using System.IO.Compression;
using RpfPreview.Core;

// Asset-free synthetic RDS5/ETPF634 smoke tests for compact 2D profiles, packed mip tails,
// mip-major 2D arrays, streaming cubemaps, and 3D volumes.
var directory = Directory.CreateTempSubdirectory("rpf9-ttpf-smoke-");
try
{
    RunCompactTailAndTileSmoke(directory.FullName);
    RunArrayCubemapVolumeSmoke(directory.FullName);
    RunNegativeControls(directory.FullName);
    Console.WriteLine("Compact 2D profiles, packed tails, 2D arrays, cubemaps, 3D volumes, and negative controls passed.");
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

static void RunArrayCubemapVolumeSmoke(string dir)
{
    // 0. 2-slice 2D array (dim=1, depth=2, mode=1, fmt=28 -> exercises bit 10 = 1 in depth/format boundary):
    //    128x128x2, 8 mips, mip-major SW_64KB_D:
    //    tail (mips 1..7) = 2 * 64 KiB at offset 0 (slice 0 at 0, slice 1 at 64 KiB),
    //    mip 0 = 2 * 64 KiB at offset 128 KiB (slice 0 at 128 KiB, slice 1 at 192 KiB). Total = 256 KiB.
    byte[][][] arraySlices = [MakeRgbaMips(128, 128, 8, seed: 0x11), MakeRgbaMips(128, 128, 8, seed: 0x77)];
    byte[] arraySlice0 = Tile2D(128, 128, 4, 8, 65536, arraySlices[0]);
    byte[] arraySlice1 = Tile2D(128, 128, 4, 8, 65536, arraySlices[1]);
    byte[] arrayMipMajor = new byte[256 * 1024];
    arraySlice0.AsSpan(0, 65536).CopyTo(arrayMipMajor.AsSpan(0));
    arraySlice1.AsSpan(0, 65536).CopyTo(arrayMipMajor.AsSpan(65536));
    arraySlice0.AsSpan(65536, 65536).CopyTo(arrayMipMajor.AsSpan(131072));
    arraySlice1.AsSpan(65536, 65536).CopyTo(arrayMipMajor.AsSpan(196608));

    // 1. Partial-mip 2D array: same mip-major layout, but mip 0 slice 1 is BCPack -> must fall back to BaseMipLevel=1 (mips 1..7).
    // 2. Streaming cubemap (dim=2, depth=6, mode=20, nt=1, 32x32 RGBA8, 6 mips):
    //    Each face SW_4KB_S = 8192 B (2 pages); 6 faces = 12 pages (49152 B, < 36 pages -> no 64 KiB pad).
    //    Chunk 0 = 6-page lower-res snapshot (dummy data), Chunk 1 = 12-page full-res 6-face slice-major data -> codes [6, 10].
    byte[][][] cubeFaces = Enumerable.Range(0, 6).Select(f => MakeRgbaMips(32, 32, 6, seed: 0x20 + f * 19)).ToArray();
    byte[] cubeChunk0 = new byte[6 * 4096];
    byte[] cubeChunk1 = new byte[6 * 8192];
    for (int f = 0; f < 6; f++) Tile2D(32, 32, 4, 6, 4096, cubeFaces[f]).CopyTo(cubeChunk1.AsSpan(f * 8192));

    // 3. 3D volume (dim=3, depth=8, mode=2, mips=1, 32x16x8 RGBA8 SW_4KB_S 3D):
    //    eb=4 -> bw=8, bh=16, bd=8 -> pw=32, ph=16, zBlockStride=32*16*4*8 = 16384 B (4 pages), 1 Z-block.
    byte[][] volSlices = Enumerable.Range(0, 8).Select(z => MakeRgbaMips(32, 16, 1, seed: 0x50 + z * 23)[0]).ToArray();
    byte[] volTiled = Tile3D(32, 16, 8, 4, volSlices);
    Assert(volTiled.Length == 16384, "Unexpected 3D volume tiled size.");

    var entries = new List<PoolEntry>
    {
        new(128, 128, 28, 2, 1, Mode: 1, NonTail: 1, PackedMips: 7, (0u, 0u),
            [ChunkSpec.Zlib(arrayMipMajor.AsSpan(0, 131072)), ChunkSpec.Zlib(arrayMipMajor.AsSpan(131072, 131072))]),
        new(128, 128, 28, 2, 1, Mode: 1, NonTail: 1, PackedMips: 7, (0u, 0u),
            [ChunkSpec.Zlib(arrayMipMajor.AsSpan(0, 131072)), ChunkSpec.Zlib(arrayMipMajor.AsSpan(131072, 65536)), ChunkSpec.BcPack(65536)]),
        new(32, 32, 28, 6, 2, Mode: 20, NonTail: 1, PackedMips: 5,
            StreamMeta(codes: [6, 10]),
            [ChunkSpec.Zlib(cubeChunk0), ChunkSpec.Zlib(cubeChunk1)]),
        new(32, 16, 28, 8, 3, Mode: 2, NonTail: 1, PackedMips: 0, (0u, 0u),
            [ChunkSpec.Zlib(volTiled)]),
        // Negative controls for multi-slice layouts:
        // 4: Multi-mip 3D volume (mips=2) -> must be rejected.
        new(32, 16, 28, 8, 3, Mode: 2, NonTail: 1, PackedMips: 1, (0u, 0u),
            [ChunkSpec.Zlib(volTiled)]),
        // 5: Cubemap with wrong chunk count (declares 1 chunk [10] instead of nt+1=2) -> must be rejected.
        new(32, 32, 28, 6, 2, Mode: 20, NonTail: 1, PackedMips: 5,
            StreamMeta(codes: [10]),
            [ChunkSpec.Zlib(cubeChunk1)]),
        // 6: 2D array with unsupported mode=0 -> must be rejected.
        new(128, 128, 28, 2, 1, Mode: 0, NonTail: 1, PackedMips: 7, (0u, 0u),
            [ChunkSpec.Zlib(arrayMipMajor)])
    };

    string path = Path.Combine(dir, "multislice.ttpf");
    WritePool(path, entries);
    var pool = TtpfReader.Inspect(path);

    // Verify 0: full 2-slice 2D array (and confirm format=28 was not corrupted by depth bit 0).
    var arr = pool.Textures[0];
    Assert(arr.CanExtract && arr.DxgiFormat == 28 && arr.Depth == 2 && arr.Dimension == 1 && arr.Mips == 8 && !arr.SupportsNativeReplacement,
        $"2D array inspect mismatch: fmt={arr.DxgiFormat}, depth={arr.Depth}, mips={arr.Mips}, lim={arr.Limitation}");
    byte[] arrDds = TtpfReader.CreateDds(path, 0);
    var arrInfo = DdsTextureReader.Inspect(arrDds);
    Assert(arrInfo.ArraySize == 2 && arrInfo.SliceCount == 2 && arrInfo.Mips == 8, "2D array DDS header mismatch.");
    int off = arrInfo.DataOffset;
    for (int s = 0; s < 2; s++)
    {
        for (int m = 0; m < 8; m++)
        {
            byte[] exp = arraySlices[s][m];
            Assert(arrDds.AsSpan(off, exp.Length).SequenceEqual(exp), $"2D array slice {s} mip {m} bytes differed.");
            off += exp.Length;
        }
        Assert(TtpfReader.DecodeTexturePixels(path, 0, s).Bgra32.SequenceEqual(DdsTextureReader.DecodeTexturePixels(arrDds, s).Bgra32),
            $"2D array slice {s} preview decode disagreed with DDS.");
    }
    Assert(off == arrDds.Length, "2D array DDS length mismatch.");

    // Verify 1: partial-mip 2D array fallback (BaseMipLevel=1, 7 mips across both slices).
    var partialArr = pool.Textures[1];
    Assert(partialArr.CanExtract && partialArr.BaseMipLevel == 1 && partialArr.Mips == 7,
        $"Partial 2D array fallback mismatch: base={partialArr.BaseMipLevel}, mips={partialArr.Mips}, lim={partialArr.Limitation}");
    byte[] partialArrDds = TtpfReader.CreateDds(path, 1);
    var partialArrInfo = DdsTextureReader.Inspect(partialArrDds);
    Assert(partialArrInfo.Width == 64 && partialArrInfo.Height == 64 && partialArrInfo.Mips == 7 && partialArrInfo.ArraySize == 2,
        "Partial 2D array DDS header mismatch.");
    for (int s = 0; s < 2; s++)
        Assert(TtpfReader.DecodeTexturePixels(path, 1, s).Bgra32.SequenceEqual(DdsTextureReader.DecodeTexturePixels(partialArrDds, s).Bgra32),
            $"Partial 2D array slice {s} decode mismatch.");

    // Verify 2: streaming cubemap (6 faces, DX10 TEXTURECUBE).
    var cube = pool.Textures[2];
    Assert(cube.CanExtract && cube.Dimension == 2 && cube.Depth == 6 && cube.Mips == 6 && !cube.SupportsNativeReplacement,
        $"Cubemap inspect mismatch: lim={cube.Limitation}");
    byte[] cubeDds = TtpfReader.CreateDds(path, 2);
    var cubeInfo = DdsTextureReader.Inspect(cubeDds);
    Assert(cubeInfo.Dimension == 2 && cubeInfo.SliceCount == 6 && cubeInfo.ArraySize == 1 && cubeInfo.Mips == 6,
        "Cubemap DDS header mismatch.");
    off = cubeInfo.DataOffset;
    for (int f = 0; f < 6; f++)
    {
        for (int m = 0; m < 6; m++)
        {
            byte[] exp = cubeFaces[f][m];
            Assert(cubeDds.AsSpan(off, exp.Length).SequenceEqual(exp), $"Cubemap face {f} mip {m} bytes differed.");
            off += exp.Length;
        }
        Assert(TtpfReader.DecodeTexturePixels(path, 2, f).Bgra32.SequenceEqual(DdsTextureReader.DecodeTexturePixels(cubeDds, f).Bgra32),
            $"Cubemap face {f} preview decode disagreed with DDS.");
    }
    Assert(off == cubeDds.Length, "Cubemap DDS length mismatch.");

    // Verify 3: 3D volume (8 Z-slices, DX10 TEXTURE3D).
    var vol = pool.Textures[3];
    Assert(vol.CanExtract && vol.Dimension == 3 && vol.Depth == 8 && vol.ExportDepth == 8 && vol.Mips == 1 && !vol.SupportsNativeReplacement,
        $"Volume inspect mismatch: lim={vol.Limitation}");
    byte[] volDds = TtpfReader.CreateDds(path, 3);
    var volInfo = DdsTextureReader.Inspect(volDds);
    Assert(volInfo.Dimension == 3 && volInfo.ResourceDimension == 4 && volInfo.Depth == 8 && volInfo.Mips == 1,
        "Volume DDS header mismatch.");
    off = volInfo.DataOffset;
    for (int z = 0; z < 8; z++)
    {
        Assert(volDds.AsSpan(off, volSlices[z].Length).SequenceEqual(volSlices[z]), $"Volume slice {z} bytes differed.");
        off += volSlices[z].Length;
        Assert(TtpfReader.DecodeTexturePixels(path, 3, z).Bgra32.SequenceEqual(DdsTextureReader.DecodeTexturePixels(volDds, z).Bgra32),
            $"Volume slice {z} preview decode disagreed with DDS.");
    }
    Assert(off == volDds.Length, "Volume DDS length mismatch.");

    // Negative controls:
    Assert(!pool.Textures[4].CanExtract, "Multi-mip 3D volume was unexpectedly accepted.");
    Assert(!pool.Textures[5].CanExtract, "Cubemap with wrong chunk count was unexpectedly accepted.");
    Assert(!pool.Textures[6].CanExtract, "2D array with unsupported mode was unexpectedly accepted.");

    bool badSliceThrew = false;
    try { TtpfReader.DecodeTexturePixels(path, 2, slice: 6); }
    catch (ArgumentOutOfRangeException) { badSliceThrew = true; }
    Assert(badSliceThrew, "Out-of-range cubemap face did not throw ArgumentOutOfRangeException.");

    foreach (var (idx, ddsBytes) in new[] { (0, arrDds), (2, cubeDds), (3, volDds) })
    {
        bool writeRejected = false;
        try { Rds5TextureWriter.ExportReplacementCopy(path, idx, ddsBytes, Path.Combine(dir, $"reject_{idx}.ttpf")); }
        catch (NotSupportedException) { writeRejected = true; }
        Assert(writeRejected, $"Rds5TextureWriter did not reject multi-slice entry {idx}.");
    }
}

static byte[] Tile3D(int w, int h, int depth, int eb, byte[][] linearSlices)
{
    int[][] volPatterns = [
        [1,2,101,-1,102,-2,3,103,-3,4,104,-4],
        [0,1,101,-1,102,-2,2,103,-3,3,104,-4],
        [0,0,1,-1,101,-2,2,102,-3,3,103,-4],
        [0,0,0,1,101,-1,2,102,-2,3,103,-3],
        [0,0,0,0,101,-1,1,102,-2,2,103,-3]
    ];
    int log = eb switch { 1 => 0, 2 => 1, 4 => 2, 8 => 3, _ => 4 };
    (int bw, int bh, int bd) = log switch { 0 => (16, 16, 16), 1 => (8, 16, 16), 2 => (8, 16, 8), 3 => (8, 8, 8), _ => (4, 8, 8) };
    int pw = (w + bw - 1) / bw * bw, ph = (h + bh - 1) / bh * bh;
    int zStride = pw * ph * eb * bd, zBlocks = (depth + bd - 1) / bd;
    byte[] tiled = new byte[zStride * zBlocks];
    var pattern = volPatterns[log];
    for (int z = 0; z < depth; z++)
    {
        int zBase = (z / bd) * zStride;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int addr = ((y / bh) * (pw / bw) + x / bw) * 4096;
                for (int bit = 0; bit < pattern.Length; bit++)
                {
                    int s = pattern[bit];
                    if (s >= 101) addr |= ((z >> (s - 101)) & 1) << bit;
                    else if (s > 0) addr |= ((x >> (s - 1)) & 1) << bit;
                    else if (s < 0) addr |= ((y >> (-s - 1)) & 1) << bit;
                }
                linearSlices[z].AsSpan((y * w + x) * eb, eb).CopyTo(tiled.AsSpan(zBase + addr, eb));
            }
    }
    return tiled;
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
