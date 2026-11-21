using System.Buffers.Binary;
using System.IO.Compression;
using RpfPreview.Core;

const int PageSize = 65536;
const long CandidateOffset = 4096 + 16;
const int IndexOffset = PageSize - 16;
const int LastVertexReference = IndexOffset + 36;
var header = new byte[64];
"9FPR"u8.CopyTo(header);
var (system, graphics) = MakeDrawable();
var directory = Directory.CreateTempSubdirectory("rpf9-model-test-");
string path = Path.Combine(directory.FullName, "mixed.rpf");
string cachePath = RecoveryIndexCache.CachePath("models", path);

try
{
    WriteArchive(path);
    long fileSize = new FileInfo(path).Length;
    var candidates = Rpf9ModelRecovery.FindCandidateOffsets(path);
    if (!candidates.SequenceEqual([CandidateOffset]))
        throw new Exception("Could not locate the synthetic drawable header.");

    var mixed = Rpf9ModelRecovery.RecoverAtOffsets(path, fileSize, candidates).Single();
    if (mixed.ResourceSystemBytes != system.Length ||
        !mixed.Segments!.Select(segment => segment.IsRaw ? segment.Size : 0).SequenceEqual([0L, 16L, 0L, 48L]))
        throw new Exception("The resource split and raw tail lengths differ from the fixture.");

    var first = mixed.Segments![0];
    var legacy = mixed with
    {
        Segments = [first], Size = first.Size, StoredSize = first.StoredSize, ResourceSystemBytes = null
    };
    RecoveryIndexCache.Save("models", path, [legacy], "synthetic compressed-only cache");
    var v1 = RecoveryIndexCache.Load("models", path, fileSize, header);
    if (v1?.Schema != "rpfpreview-payload-index/1" || v1.Entries[0].Segments![0].IsRaw)
        throw new Exception("Compressed-only cache did not round-trip.");

    RecoveryIndexCache.Save("models", path, [mixed], "synthetic packed drawable");
    var v2 = RecoveryIndexCache.Load("models", path, fileSize, header);
    if (v2?.Schema != "rpfpreview-payload-index/2" || v2.Entries[0].Segments!.Count(s => s.IsRaw) != 2)
        throw new Exception("Mixed cache did not preserve the block types.");
    var document = ArchiveReader.Open(path);
    var entry = document.Entries.Single(e => e.Path == mixed.Path);
    byte[] resource = ArchiveReader.ReadResourceData(document, entry);
    if (!resource.AsSpan(0, system.Length).SequenceEqual(system) ||
        !resource.AsSpan(system.Length).SequenceEqual(graphics))
        throw new Exception("Mixed-block readback differed from the source bytes.");
    var model = RageModelDecoder.Decode(resource.AsSpan(0, system.Length), resource.AsSpan(system.Length));
    if (model is not { HasBoneBindings: true } || !model.Meshes.Single().Indices.SequenceEqual([0, 1, 2]))
        throw new Exception("The packed triangle or external bone palette did not decode.");

    var segments = mixed.Segments!;
    var damaged = mixed with
    {
        Size = mixed.Size + 1,
        Segments = [segments[0], segments[1] with { Size = 17 }, segments[2], segments[3]]
    };
    try
    {
        RecoveryIndexCache.Save("models", path, [damaged], "synthetic invalid cache");
        throw new Exception("Invalid raw segment length was accepted.");
    }
    catch (InvalidDataException) { }

    graphics[LastVertexReference] = 3;
    WriteArchive(path);
    if (Rpf9ModelRecovery.RecoverAtOffsets(path, new FileInfo(path).Length, candidates).Count != 0)
        throw new Exception("The decoder accepted an out-of-range vertex reference in the raw graphics tail.");

    Console.WriteLine("Recovered the packed drawable and both raw tails, verified cache readback, and rejected malformed inputs.");
}
finally
{
    File.Delete(cachePath);
    File.Delete(path);
    directory.Delete();
}

void WriteArchive(string destination)
{
    using var file = File.Create(destination);
    file.Write(header);
    file.Position = CandidateOffset;
    void WriteCompressed(ReadOnlySpan<byte> data)
    {
        using (var compressed = new ZLibStream(file, CompressionLevel.Optimal, leaveOpen: true))
            compressed.Write(data);
        file.Write(new byte[(int)((-file.Position) & 15)]);
    }
    WriteCompressed(system.AsSpan(0, PageSize));
    file.Write(system.AsSpan(PageSize));
    WriteCompressed(graphics.AsSpan(0, PageSize));
    file.Write(graphics.AsSpan(PageSize));
}

static (byte[] System, byte[] Graphics) MakeDrawable()
{
    const int Pages = 192, Shader = 224, Name = 288, Lod = 304, Models = 320, Part = 336;
    const int Geometries = 392, Bounds = 400, Geometry = 432, VertexBuffer = 592, Descriptor = 672, IndexBuffer = 688;
    const int Fvf = PageSize + 16 - 320;
    const int PositionOffset = 128, VertexCount = 3, IndexLength = 64;
    byte[] system = new byte[PageSize + 16], graphics = new byte[PageSize + 48];
    void S(int at, int address) => BinaryPrimitives.WriteUInt64LittleEndian(system.AsSpan(at), 0x50000000UL + (uint)address);
    void G(int at, int address) => BinaryPrimitives.WriteUInt64LittleEndian(system.AsSpan(at), 0x60000000UL + (uint)address);
    void U16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(system.AsSpan(at), value);
    void U32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    void F32(byte[] bytes, int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), value);

    S(8, Pages); S(16, Shader); S(80, Lod); S(168, Name);
    system[Pages + 8] = system[Pages + 9] = 1;
    "triangle"u8.CopyTo(system.AsSpan(Name));
    F32(system, 44, 2);
    for (int axis = 0; axis < 3; axis++)
    {
        F32(system, 64 + axis * 4, 1);
        F32(system, Bounds + 16 + axis * 4, 1);
    }
    S(Lod, Models); U16(Lod + 8, 1); U16(Lod + 10, 1); S(Models, Part);
    S(Part + 8, Geometries); U16(Part + 16, 1); U16(Part + 18, 1); U16(Part + 54, 1); S(Part + 24, Bounds);
    S(Geometries, Geometry); S(Geometry + 8, VertexBuffer); S(Geometry + 64, Descriptor);

    U32(system, VertexBuffer + 8, PositionOffset + VertexCount * 8); U16(VertexBuffer + 12, 1);
    G(VertexBuffer + 24, 0); S(VertexBuffer + 64, Fvf);
    U32(system, Fvf, PositionOffset);
    system[Fvf + 0xD0] = 8; system[Fvf + 0x104] = 16;
    U32(system, Fvf + 0x138, (VertexCount << 10) | 1);
    U32(graphics, 0, 0x42);
    U32(graphics, 108, 2);
    F32(graphics, 112, 1f / 16777215); F32(graphics, 116, 1f / 16777215); F32(graphics, 120, 1f / 65535);
    // The triangle occupies only part of the [0, 1] quantizer domain on each axis.
    U32(graphics, PositionOffset + 8, 0x80000000);
    U32(graphics, PositionOffset + 20, 0x8000);

    S(Descriptor, IndexBuffer); U16(Descriptor + 8, 1); U16(Descriptor + 10, 1);
    U32(system, Descriptor + 12, 0x80000300);
    U32(system, IndexBuffer + 8, IndexLength / 4); U16(IndexBuffer + 12, 4); G(IndexBuffer + 24, IndexOffset);
    U32(graphics, IndexOffset, 0xC0000001);
    U32(graphics, IndexOffset + 4, 1); U32(graphics, IndexOffset + 8, VertexCount);
    U32(graphics, IndexOffset + 12, VertexCount - 1);
    U32(graphics, IndexOffset + 28, (2u << 14) | (1u << 7));
    graphics[IndexOffset + 34] = 1;
    graphics[LastVertexReference] = 2;
    graphics[IndexOffset + 38] = 7; // External bone index, with no local bone table.
    graphics[IndexOffset + 40] = 0xCD; // Auxiliary index trailer.
    return (system, graphics);
}
