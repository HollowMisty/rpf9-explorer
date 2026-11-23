using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json.Nodes;
using RpfPreview.Core;

const int CandidateOffset = 0x1010;
const int SystemSize = 0x1100;
const int FragmentDrawable = 0x100;
const int Fvf = SystemSize - 320;
const int Pages = 0x200, Name = 0x240, Shader = 0x280, Lod = 0x2c0;
const int Models = 0x2d0, Part = 0x2e0, Bounds = 0x320, Geometries = 0x340;
const int Geometry = 0x350, VertexBuffer = 0x3f0, Descriptor = 0x440, IndexBuffer = 0x450;
const int LowLod = 0x600, LowModels = 0x610, LowPart = 0x620;
const int VertexStart = 96, IndexStart = 160, IndexLength = 48;

var temp = Directory.CreateTempSubdirectory("rpf9-model-smoke-");
string path = Path.Combine(temp.FullName, "synthetic.rpf");
try
{
    Valid("drawable", fragment: false, []);
    Valid("fragment, one system stream", fragment: true, []);
    Valid("fragment metadata in one continuation", fragment: true, [0x1000]);
    Valid("fragment metadata in two continuations", fragment: true, [0x800, 0x1000]);
    Valid("complete secondary bone palette", fragment: true, [],
        s => AddLowLodPalette(s, 0x700));
    Valid("short secondary palette at declared split", fragment: true, [],
        s => AddLowLodPalette(s, SystemSize - 16), graphicsCut: 144, incompleteProbe: true);
    Valid("short secondary palette with system continuations", fragment: true, [0x800, 0x1000],
        s => AddLowLodPalette(s, SystemSize - 16), graphicsCut: 144, incompleteProbe: true);
    Valid("decodable graphics page must not become system metadata", fragment: true, [],
        s => AddLowLodPalette(s, SystemSize - 16), incompleteProbe: true, duplicateGraphics: true);
    RefreshLegacyCatalog();
    Reject("nonzero alignment padding", s => { }, corrupt: CorruptPadding);
    Reject("truncated continuation", s => { }, corrupt: TruncateContinuation);
    Reject("damaged continuation checksum", s => { }, corrupt: CorruptChecksum);
    Reject("extent exceeds available system bytes", s => Ptr(s, VertexBuffer + 64, 0x900000));
    Reject("extent overflows", s => U32(s, VertexBuffer + 8, uint.MaxValue));
    Reject("fragment signature without a drawable", s => s.AsSpan(FragmentDrawable, 192).Clear());
    Reject("invalid resource header", s => s[0] = 1, found: false);
    Reject("short primary palette", s =>
    {
        U16(s, Part + 20, 16);
        Ptr(s, Part + 40, SystemSize - 16);
    });
    Reject("secondary palette pointer outside the system segment", s => AddLowLodPalette(s, SystemSize));
    Reject("short secondary palette with inconsistent page count", s =>
    {
        AddLowLodPalette(s, SystemSize - 16);
        s[Pages + 8] = 1;
    });
    Reject("short secondary palette with populated page-map slots", s =>
    {
        AddLowLodPalette(s, SystemSize - 16);
        s[Pages + 16] = 1;
    });
    Reject("short secondary palette with missing mandatory metadata", s =>
    {
        AddLowLodPalette(s, SystemSize - 16);
        Ptr(s, LowPart + 24, SystemSize - 4);
    });
    Reject("short secondary palette with invalid meshlet", s => AddLowLodPalette(s, SystemSize - 16),
        mutateGraphics: g => U16(g, IndexStart + 36, 3));
    Reject("bad first graphics page must not be skipped to decode the second", s => AddLowLodPalette(s, SystemSize - 16),
        mutateGraphics: g => U16(g, IndexStart + 36, 3), duplicateGraphics: true);
    Console.WriteLine("Synthetic model recovery tests passed.");
}
finally
{
    File.Delete(path);
    temp.Delete();
}

void Valid(string name, bool fragment, int[] cuts, Action<byte[]>? mutate = null, int? graphicsCut = null,
    bool incompleteProbe = false, bool duplicateGraphics = false)
{
    var (system, graphics) = MakeModel(fragment, cuts.Length + 1);
    if (duplicateGraphics)
    {
        graphicsCut = graphics.Length;
        graphics = [.. graphics, .. graphics];
    }
    if (graphicsCut is not null) system[Pages + 9] = 2;
    mutate?.Invoke(system);
    var first = system.AsSpan(0, cuts.Length == 0 ? system.Length : cuts[0]);
    var extent = RageModelDecoder.ProbeRequiredExtents(first);
    Check(extent?.MetadataComplete == (cuts.Length == 0 && !incompleteProbe), name, "initial extent");
    var blocks = Pack(system, graphics, cuts, graphicsCut);
    var entries = Recover(name, true);
    Check(entries.Count == 1, name, "expected to recover exactly one resource");
    var entry = entries[0];
    Check(entry.ResourceSystemBytes == system.Length && entry.Size == system.Length + graphics.Length,
        name, "system/graphics split");
    Check(entry.Segments!.Select(s => (s.Offset, s.StoredSize, s.Size))
        .SequenceEqual(blocks.Select(b => (b.Offset, b.Packed, b.Decoded))), name, "exact segments");
    var doc = ArchiveReader.Open(path);
    byte[] resource = ArchiveReader.ReadResourceData(doc, doc.Entries.Single(e => e.Path == entry.Path));
    Check(resource.AsSpan(0, system.Length).SequenceEqual(system) &&
        resource.AsSpan(system.Length).SequenceEqual(graphics), name, "readback");
    var model = RageModelDecoder.Decode(resource.AsSpan(0, system.Length), resource.AsSpan(system.Length));
    Check(model is not null && model.IsFragment == fragment &&
        model.Meshes.Single().Indices.SequenceEqual([0, 1, 2]), name, "decoded triangle");
    if (mutate is not null)
    {
        var low = RageModelDecoder.Decode(resource.AsSpan(0, system.Length), resource.AsSpan(system.Length), null, 2);
        Check(low is not null && low.Meshes.Single().Indices.SequenceEqual([0, 1, 2]), name, "decoded lower LOD");
        Check((low!.Meshes.Single().BonePaletteHashes is null) == incompleteProbe,
            name, "a short palette stays unavailable instead of reading graphics bytes as hashes");
    }
}

void RefreshLegacyCatalog()
{
    const string name = "legacy complete index misses the newly recoverable fragment";
    var (system, graphics) = MakeModel(true, 1);
    AddLowLodPalette(system, SystemSize - 16);
    Pack(system, graphics, []);
    string cache = RecoveryIndexCache.CachePath("models", path);
    try
    {
        RecoveryIndexCache.Save("models", path, [], "synthetic prior scan", complete: true);
        var saved = JsonNode.Parse(File.ReadAllText(cache))!.AsObject();
        saved["Schema"] = "rpfpreview-payload-index/models/5";
        File.WriteAllText(cache, saved.ToJsonString());
        var header = new byte[64];
        using (var f = File.OpenRead(path)) f.ReadExactly(header);
        long length = new FileInfo(path).Length;
        Check(RecoveryIndexCache.Load("models", path, length, header) is null &&
            RecoveryIndexCache.LoadLegacyModels(path, length, header) is { IsComplete: false }, name, "migration");
        Check(PayloadIndexBuilder.BuildModels(path) == 1 &&
            RecoveryIndexCache.Load("models", path, length, header) is { IsComplete: true, Entries.Count: 1 },
            name, "refreshed model index");
    }
    finally { File.Delete(cache); }
}

void Reject(string name, Action<byte[]> mutate, Action<Stream, Block[]>? corrupt = null, bool found = true,
    Action<byte[]>? mutateGraphics = null, bool duplicateGraphics = false)
{
    var (system, graphics) = MakeModel(true, 2);
    int? graphicsCut = null;
    if (duplicateGraphics)
    {
        graphicsCut = graphics.Length;
        graphics = [.. graphics, .. graphics];
        system[Pages + 9] = 2;
    }
    mutate(system);
    mutateGraphics?.Invoke(graphics);
    var blocks = Pack(system, graphics, [0x1000], graphicsCut);
    if (corrupt is not null)
    {
        using var f = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
        corrupt(f, blocks);
    }
    Check(Recover(name, found).Count == 0, name, "unexpected recovery");
}

IReadOnlyList<ArchiveEntry> Recover(string name, bool found)
{
    var candidates = Rpf9ModelRecovery.FindCandidateOffsets(path);
    Check(candidates.Contains(CandidateOffset) == found, name, "candidate detection");
    return Rpf9ModelRecovery.RecoverAtOffsets(path, new FileInfo(path).Length, [CandidateOffset]);
}

Block[] Pack(byte[] system, byte[] graphics, int[] cuts, int? graphicsCut = null)
{
    using var f = File.Create(path);
    f.Write("9FPR"u8);
    f.Position = CandidateOffset;
    int preceding = 0;
    var blocks = new List<Block>();
    foreach (int cut in cuts.Append(system.Length))
    {
        Write(system.AsSpan(preceding, cut - preceding));
        preceding = cut;
    }
    if (graphicsCut is int split)
    {
        Write(graphics.AsSpan(0, split));
        Write(graphics.AsSpan(split));
    }
    else Write(graphics);
    return blocks.ToArray();

    void Write(ReadOnlySpan<byte> bytes)
    {
        long offset = f.Position;
        using (var zlib = new ZLibStream(f, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(bytes);
        blocks.Add(new(offset, f.Position - offset, bytes.Length));
        f.Write(new byte[(int)(-f.Position & 15)]);
    }
}

static void CorruptPadding(Stream stream, Block[] blocks)
{
    long end = blocks[0].Offset + blocks[0].Packed;
    Check(end < blocks[1].Offset, "padding fixture", "first block must require padding");
    stream.Position = end;
    stream.WriteByte(1);
}

static void TruncateContinuation(Stream stream, Block[] blocks) =>
    stream.SetLength(blocks[1].Offset + blocks[1].Packed - 2);

static void CorruptChecksum(Stream stream, Block[] blocks)
{
    long at = blocks[1].Offset + blocks[1].Packed - 1;
    stream.Position = at;
    int value = stream.ReadByte();
    stream.Position = at;
    stream.WriteByte((byte)(value ^ 1));
}

static (byte[] System, byte[] Graphics) MakeModel(bool fragment, int systemPages)
{
    byte[] s = new byte[SystemSize], g = new byte[IndexStart + IndexLength];
    Ptr(s, 8, Pages);
    int d = fragment ? FragmentDrawable : 0;
    if (fragment)
    {
        Ptr(s, 24, d);
        U16(s, 48, 1);
    }
    Ptr(s, 168, Name);
    "synthetic"u8.CopyTo(s.AsSpan(Name));
    s[Pages + 8] = (byte)systemPages;
    s[Pages + 9] = 1;
    Ptr(s, d + 16, Shader);
    Ptr(s, d + 80, Lod);
    F32(s, d + 44, 2);
    F32(s, d + 64, 1); F32(s, d + 68, 1);
    Ptr(s, Lod, Models); U16(s, Lod + 8, 1); U16(s, Lod + 10, 1); Ptr(s, Models, Part);
    Ptr(s, Part + 8, Geometries); Ptr(s, Part + 24, Bounds);
    U16(s, Part + 16, 1); U16(s, Part + 18, 1); U16(s, Part + 54, 1);
    F32(s, Bounds + 16, 1); F32(s, Bounds + 20, 1);
    Ptr(s, Geometries, Geometry);
    Ptr(s, Geometry + 8, VertexBuffer); Ptr(s, Geometry + 64, Descriptor);
    U32(s, VertexBuffer + 8, VertexStart + 3 * 12); U16(s, VertexBuffer + 12, 1);
    Ptr(s, VertexBuffer + 24, 0, graphics: true); Ptr(s, VertexBuffer + 64, Fvf);
    U32(s, Fvf, VertexStart); s[Fvf + 0xd0] = 12; s[Fvf + 0x104] = 6;
    U32(s, Fvf + 0x138, (3 << 10) | 1);
    U32(g, 0, 0x63);
    F32(g, VertexStart + 12, 1); F32(g, VertexStart + 28, 1);
    Ptr(s, Descriptor, IndexBuffer); U16(s, Descriptor + 8, 1); U16(s, Descriptor + 10, 1);
    U32(s, Descriptor + 12, 3 << 8);
    U32(s, IndexBuffer + 8, IndexLength / 4); U16(s, IndexBuffer + 12, 4);
    Ptr(s, IndexBuffer + 24, IndexStart, graphics: true);
    U32(g, IndexStart, 0x80000001); U32(g, IndexStart + 4, 1); U32(g, IndexStart + 8, 3);
    U32(g, IndexStart + 12, 2); U32(g, IndexStart + 28, (2 << 14) | (1 << 7));
    U16(g, IndexStart + 34, 1); U16(g, IndexStart + 36, 2);
    return (s, g);
}

static void AddLowLodPalette(byte[] system, int palette)
{
    Ptr(system, FragmentDrawable + 96, LowLod);
    Ptr(system, LowLod, LowModels);
    U16(system, LowLod + 8, 1);
    U16(system, LowLod + 10, 1);
    Ptr(system, LowModels, LowPart);
    system.AsSpan(Part, 56).CopyTo(system.AsSpan(LowPart));
    U16(system, LowPart + 20, 16);
    Ptr(system, LowPart + 40, palette);
}

static void Ptr(byte[] data, int at, int offset, bool graphics = false) =>
    BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(at), (graphics ? 0x60000000UL : 0x50000000UL) + (uint)offset);
static void U16(byte[] data, int at, ushort n) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at), n);
static void U32(byte[] data, int at, uint n) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), n);
static void F32(byte[] data, int at, float n) => BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at), n);
static void Check(bool passed, string name, string test)
{
    if (!passed) throw new InvalidOperationException($"{name}: {test}");
}

record Block(long Offset, long Packed, long Decoded);
