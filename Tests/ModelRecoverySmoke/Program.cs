using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
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

    RecoveryIndexCache.Save("models", path, [mixed], "synthetic packed drawable", complete: true);
    var current = RecoveryIndexCache.Load("models", path, fileSize, header);
    if (current?.Schema != "rpfpreview-payload-index/models/3" || !current.IsComplete ||
        current.Entries[0].Segments!.Count(segment => segment.IsRaw) != 2)
        throw new Exception("Current model cache did not preserve the mixed block types and completion marker.");
    if (RecoveryIndexCache.LoadLegacyModels(path, fileSize, header) is not null)
        throw new Exception("A current model cache was mistaken for a legacy index.");
    var publicRaw = current with { Schema = "rpfpreview-payload-index/2" };
    File.WriteAllText(cachePath, JsonSerializer.Serialize(publicRaw));
    if (RecoveryIndexCache.Load("models", path, fileSize, header) is not null ||
        RecoveryIndexCache.LoadLegacyModels(path, fileSize, header) is not { IsComplete: false } rawMigration ||
        rawMigration.Entries[0].Segments!.Count(segment => segment.IsRaw) != 2)
        throw new Exception("Public mixed-block cache migration did not preserve the validated entry.");
    RecoveryIndexCache.Save("models", path, [mixed], "synthetic packed drawable", complete: true);
    CheckCompressedLegacySchemas();
    var document = ArchiveReader.Open(path);
    var entry = document.Entries.Single(e => e.Path == mixed.Path);
    byte[] resource = ArchiveReader.ReadResourceData(document, entry);
    if (!resource.AsSpan(0, system.Length).SequenceEqual(system) ||
        !resource.AsSpan(system.Length).SequenceEqual(graphics))
        throw new Exception("Mixed-block readback differed from the source bytes.");
    var model = RageModelDecoder.Decode(resource.AsSpan(0, system.Length), resource.AsSpan(system.Length));
    if (model is not { HasBoneBindings: true } || !model.Meshes.Single().Indices.SequenceEqual([0, 1, 2]))
        throw new Exception("The packed triangle or external bone palette did not decode.");

    byte[] invalidDomain = (byte[])graphics.Clone();
    BinaryPrimitives.WriteSingleLittleEndian(invalidDomain.AsSpan(96), 0.25f);
    if (RageModelDecoder.Decode(system, invalidDomain) is not null)
        throw new Exception("A packed quantizer outside the recorded mesh bounds was accepted.");

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

    foreach (var invalid in new[] {
        mixed with { ResourceSystemBytes = PageSize },
        mixed with { Segments = [segments[0], segments[1] with { Offset = segments[1].Offset + 1 }, segments[2], segments[3]] },
        mixed with { Segments = [null!] } })
    {
        try
        {
            RecoveryIndexCache.Save("models", path, [invalid], "synthetic invalid framing");
            throw new Exception("Invalid mixed resource framing was accepted.");
        }
        catch (InvalidDataException) { }
    }
    try
    {
        RecoveryIndexCache.Save("models", path, [null!], "synthetic null entry");
        throw new Exception("A null cached model entry was accepted.");
    }
    catch (InvalidDataException) { }
    foreach (string schema in new[] { "rpfpreview-payload-index/1", "rpfpreview-payload-index/models/2" })
    {
        File.WriteAllText(cachePath, JsonSerializer.Serialize(current! with { Schema = schema }));
        if (RecoveryIndexCache.LoadLegacyModels(path, fileSize, header) is not null)
            throw new Exception("A legacy schema without raw-block capability accepted raw segments.");
    }
    RecoveryIndexCache.Save("models", path, [mixed], "synthetic valid framing");
    if (RecoveryIndexCache.Load("models", path, fileSize, header) is not { IsComplete: false })
        throw new Exception("A partial model index incorrectly reported completion.");

    graphics[LastVertexReference] = 3;
    WriteArchive(path);
    if (Rpf9ModelRecovery.RecoverAtOffsets(path, new FileInfo(path).Length, candidates).Count != 0)
        throw new Exception("The decoder accepted an out-of-range vertex reference in the raw graphics tail.");

    graphics[LastVertexReference] = 2;
    WriteArchive(path, dictionary: true);
    var dictionaryEntry = Rpf9ModelRecovery.RecoverAtOffsets(path, new FileInfo(path).Length, candidates).Single();
    if (dictionaryEntry.ResourceDrawableOffset != 1024 ||
        !dictionaryEntry.Segments!.Select(segment => segment.IsRaw ? segment.Size : 0).SequenceEqual([0L, 16L, 0L, 48L]))
        throw new Exception("Dictionary-owned mixed tails lost the selected child or block types.");
    RecoveryIndexCache.Save("models", path, [dictionaryEntry], "synthetic dictionary mixed tails", complete: true);
    var dictionaryDocument = ArchiveReader.Open(path);
    var selected = dictionaryDocument.Entries.Single(item => item.Path == dictionaryEntry.Path);
    byte[] dictionaryResource = ArchiveReader.ReadResourceData(dictionaryDocument, selected);
    var selectedModel = RageModelDecoder.Decode(dictionaryResource.AsSpan(0, system.Length),
        dictionaryResource.AsSpan(system.Length), selected.ResourceDrawableOffset);
    if (selectedModel is not { IsDictionaryChild: true, DrawableOffset: 1024 } ||
        RageModelDecoder.Decode(dictionaryResource.AsSpan(0, system.Length), dictionaryResource.AsSpan(system.Length), 1025) is not null)
        throw new Exception("Mixed dictionary readback did not enforce table-owned selection.");
    Console.WriteLine("Recovered standalone and dictionary packed drawables with both raw tails; verified current/legacy cache readback and malformed-input rejection.");
}
finally
{
    File.Delete(cachePath);
    File.Delete(path);
    directory.Delete();
}


void CheckCompressedLegacySchemas()
{
    string legacyPath = Path.Combine(directory.FullName, "legacy.rpf");
    string legacyCache = RecoveryIndexCache.CachePath("models", legacyPath);
    try
    {
        foreach (bool dictionary in new[] { false, true })
        {
            WriteArchive(legacyPath, mixedTails: false, dictionary: dictionary);
            long length = new FileInfo(legacyPath).Length;
            var entry = Rpf9ModelRecovery.RecoverAtOffsets(legacyPath, length, [CandidateOffset]).Single();
            if (entry.Segments!.Any(segment => segment.IsRaw) || (entry.ResourceDrawableOffset.HasValue != dictionary))
                throw new Exception("The compressed legacy fixture has invalid resource ownership.");
            string schema = dictionary ? "rpfpreview-payload-index/models/2" : "rpfpreview-payload-index/1";
            var legacy = new RecoveryIndexCache.CachedIndex(schema, "models", Path.GetFullPath(legacyPath), length,
                File.GetLastWriteTimeUtc(legacyPath).Ticks, Convert.ToHexString(SHA256.HashData(header)),
                "synthetic legacy index", [entry]) { IsComplete = true };
            File.WriteAllText(legacyCache, JsonSerializer.Serialize(legacy));
            if (RecoveryIndexCache.Load("models", legacyPath, length, header) is not null ||
                RecoveryIndexCache.LoadLegacyModels(legacyPath, length, header) is not { IsComplete: false } loaded ||
                loaded.Entries[0].ResourceDrawableOffset != entry.ResourceDrawableOffset)
                throw new Exception("Legacy model cache did not retain the entry and selected child.");
        }
    }
    finally { File.Delete(legacyCache); File.Delete(legacyPath); }
}

static byte[] MakeDictionarySystem(byte[] source)
{
    byte[] data = (byte[])source.Clone();
    const int Child = 1024, Hashes = 1248, Pointers = 1264;
    data.AsSpan(0, 192).CopyTo(data.AsSpan(Child));
    data.AsSpan(0, 192).Clear();
    void S(int at, int address) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(at), 0x50000000UL + (uint)address);
    void U16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at), value);
    S(8, 192); S(32, Hashes); S(48, Pointers); S(Pointers, Child);
    U16(40, 1); U16(42, 1); U16(56, 1); U16(58, 1);
    data.AsSpan(Child + 8, 8).Clear();
    return data;
}

void WriteArchive(string destination, bool mixedTails = true, bool dictionary = false)
{
    byte[] cpu = dictionary ? MakeDictionarySystem(system) : system;
    using var file = File.Create(destination);
    file.Write(header);
    file.Position = CandidateOffset;
    void WriteCompressed(ReadOnlySpan<byte> data)
    {
        using (var compressed = new ZLibStream(file, CompressionLevel.Optimal, leaveOpen: true))
            compressed.Write(data);
        file.Write(new byte[(int)((-file.Position) & 15)]);
    }
    if (mixedTails)
    {
        WriteCompressed(cpu.AsSpan(0, PageSize));
        file.Write(cpu.AsSpan(PageSize));
        WriteCompressed(graphics.AsSpan(0, PageSize));
        file.Write(graphics.AsSpan(PageSize));
    }
    else
    {
        WriteCompressed(cpu);
        WriteCompressed(graphics);
    }
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
    graphics[IndexOffset + 38] = 7;
    graphics[IndexOffset + 40] = 0xCD;
    return (system, graphics);
}
