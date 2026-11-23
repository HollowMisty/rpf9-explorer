using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using RpfPreview.Core;

FragmentMesh.Run();
Console.WriteLine("Synthetic fragment geometry, UV, readback and cache checks passed.");

internal static class FragmentMesh
{
    private const int Primary = 0x110;
    private const int Child = Primary + 0x110;
    private const int Pages = 0xc0;
    private const int Lod = 0x350;
    private const int Model = 0x370;
    private const int IndexBuffer = 0x620;
    private const int ShaderGroup = 0x6a0;
    private const int IndexData = 160;
    private const int Candidate = 0x1010;

    public static void Run()
    {
        var (system, graphics) = TriangleFragment();
        var extent = RageModelDecoder.ProbeRequiredExtents(system);
        Check(extent is { MetadataComplete: true, IsFragment: true, DrawableOffset: Child },
            "extent probe selects the child");
        Check(extent!.RequiredGraphicsBytes == graphics.Length, "child geometry determines the graphics extent");

        var incomplete = RageModelDecoder.ProbeRequiredExtents(system.AsSpan(0, Child + 64));
        Check(incomplete is { MetadataComplete: false } && incomplete.RequiredSystemBytes >= Child + 176,
            "a partial child requests more system bytes");
        Check(RageModelDecoder.Decode(system.AsSpan(0, Child + 64), graphics) is null,
            "a partial child cannot be decoded");
        Check(RageModelDecoder.Decode(system, graphics.AsSpan(0, graphics.Length - 1)) is null,
            "short graphics cannot be decoded");
        Check(RageModelDecoder.Decode(system, graphics, Primary) is null, "the empty primary is not selectable");
        Check(RageModelDecoder.Decode(system, graphics, Child, 1) is null, "the child has no second LOD");

        var model = RageModelDecoder.Decode(system, graphics, Child);
        Check(model is { IsFragment: true, DrawableOffset: Child, Name: "fixture-triangle",
            Materials: null, EmbeddedTextures: null }, "the child uses the fragment name and has no shader data");
        var mesh = model!.Meshes.Single();
        Check(mesh.Positions.SequenceEqual([Vector3.Zero, Vector3.UnitX, Vector3.UnitY]), "triangle positions");
        Check(mesh.Indices.SequenceEqual([0, 1, 2]), "triangle winding");
        Check(mesh.TexCoords?.SequenceEqual([Vector2.Zero, Vector2.UnitX, Vector2.UnitY]) == true, "triangle UVs");

        var primary = (byte[])system.Clone();
        system.AsSpan(Child, 176).CopyTo(primary.AsSpan(Primary));
        SysPtr(primary, Primary + 16, ShaderGroup);
        Check(RageModelDecoder.Decode(primary, graphics)?.DrawableOffset == Primary, "a valid primary still wins");

        var ambiguous = (byte[])system.Clone();
        U16(ambiguous, 48, 3);
        Check(RageModelDecoder.ProbeRequiredExtents(ambiguous) is null, "ambiguous child selection is rejected");
        Check(RageModelDecoder.Decode(ambiguous, graphics) is null, "an ambiguous fragment is not decoded");

        var badShader = (byte[])system.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(badShader.AsSpan(Child + 16), 1);
        Check(RageModelDecoder.ProbeRequiredExtents(badShader) is null, "an invalid non-null shader pointer is rejected");

        var badPrimary = (byte[])system.Clone();
        SysPtr(badPrimary, Primary + 80, Lod);
        Check(RageModelDecoder.Decode(badPrimary, graphics) is null,
            "a primary with a declared LOD does not fall back to the child");

        var dir = Directory.CreateTempSubdirectory("fragment-mesh-");
        try
        {
            string archive = Path.Combine(dir.FullName, "authored-triangle.rpf");
            WriteArchive(archive, system, graphics);
            var candidates = Rpf9ModelRecovery.FindCandidateOffsets(archive);
            Check(candidates.SequenceEqual([Candidate]), "archive scan finds the fragment");
            var entry = Rpf9ModelRecovery.RecoverAtOffsets(archive, new FileInfo(archive).Length, candidates).Single();
            Check(entry.ResourceSystemBytes == system.Length && entry.ResourceDrawableOffset is null,
                "recovery keeps one standalone entry with the right segment split");
            byte[] bytes = ReadArchiveAndCheckCache(archive, entry);
            var readback = RageModelDecoder.Decode(bytes.AsSpan(0, system.Length), bytes.AsSpan(system.Length));
            Check(readback?.DrawableOffset == Child && readback.Meshes.Single().Indices.SequenceEqual(mesh.Indices),
                "recovery readback selects the same child and triangle");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static byte[] ReadArchiveAndCheckCache(string archive, ArchiveEntry entry)
    {
        string cachePath = RecoveryIndexCache.CachePath("models", archive);
        long length = new FileInfo(archive).Length;
        byte[] header = new byte[64];
        using (var file = File.OpenRead(archive)) file.ReadExactly(header);
        try
        {
            RecoveryIndexCache.Save("models", archive, [entry], "synthetic recovery", complete: true);
            Check(RecoveryIndexCache.Load("models", archive, length, header) is { IsComplete: true },
                "a refreshed model index round-trips");
            var document = ArchiveReader.Open(archive);
            var member = document.Entries.Single(item => item.Offset == entry.Offset && item.Compression == "Recovered model");
            byte[] result = ArchiveReader.ReadResourceData(document, member);
            string json = File.ReadAllText(cachePath);
            foreach (int schema in new[] { 4, 5, 6 })
            {
                File.WriteAllText(cachePath, json.Replace("models/7", $"models/{schema}", StringComparison.Ordinal));
                Check(RecoveryIndexCache.Load("models", archive, length, header) is null,
                    "an older complete index does not skip the refreshed scan");
                var legacy = RecoveryIndexCache.LoadLegacyModels(archive, length, header);
                Check(legacy is { IsComplete: false } && legacy.Entries.Single().Offset == entry.Offset,
                    "an older index can still seed the refreshed scan");
            }
            return result;
        }
        finally
        {
            File.Delete(cachePath);
        }
    }

    private static (byte[] System, byte[] Graphics) TriangleFragment()
    {
        const int Name = 0xf0, Models = 0x360;
        const int Geometries = 0x3b0, Bounds = 0x3c0, Geometry = 0x3e0;
        const int VertexBuffer = 0x480, Format = 0x4d0, Descriptor = 0x610;
        const int Positions = 96, TexCoords = 132, IndexBytes = 48;
        var system = new byte[0x700];
        var graphics = new byte[IndexData + IndexBytes];
        SysPtr(system, 8, Pages);
        SysPtr(system, 24, Primary);
        SysPtr(system, 168, Name);
        U16(system, 48, 2);
        system[Pages + 8] = system[Pages + 9] = 1;
        "fixture-triangle"u8.CopyTo(system.AsSpan(Name));

        SysPtr(system, Child + 80, Lod);
        F32(system, Child + 44, 2);
        F32(system, Child + 64, 1);
        F32(system, Child + 68, 1);
        SysPtr(system, Lod, Models);
        U16(system, Lod + 8, 1);
        U16(system, Lod + 10, 1);
        SysPtr(system, Models, Model);
        SysPtr(system, Model + 8, Geometries);
        SysPtr(system, Model + 24, Bounds);
        U16(system, Model + 16, 1);
        U16(system, Model + 18, 1);
        U16(system, Model + 54, 1);
        F32(system, Bounds + 16, 1);
        F32(system, Bounds + 20, 1);
        SysPtr(system, Geometries, Geometry);
        SysPtr(system, Geometry + 8, VertexBuffer);
        SysPtr(system, Geometry + 64, Descriptor);

        U32(system, VertexBuffer + 8, TexCoords + 3 * 8);
        U16(system, VertexBuffer + 12, 1);
        GfxPtr(system, VertexBuffer + 24, 0);
        SysPtr(system, VertexBuffer + 64, Format);
        U32(system, Format, Positions);
        system[Format + 0xd0] = 12;
        system[Format + 0x104] = 6;
        U32(system, Format + 0x138, (3 << 10) | 1);
        U32(system, Format + 28 * 4, TexCoords);
        system[Format + 0xd0 + 28] = 8;
        system[Format + 0x104 + 28] = 16;
        U32(graphics, 0, 0x63);
        U32(graphics, 12, (TexCoords << 7) | (8 << 3) | 2);
        F32(graphics, TexCoords + 8, 1);
        F32(graphics, TexCoords + 20, 1);
        F32(graphics, Positions + 12, 1);
        F32(graphics, Positions + 28, 1);

        SysPtr(system, Descriptor, IndexBuffer);
        U16(system, Descriptor + 8, 1);
        U16(system, Descriptor + 10, 1);
        U32(system, Descriptor + 12, 3 << 8);
        U32(system, IndexBuffer + 8, IndexBytes / 4);
        U16(system, IndexBuffer + 12, 4);
        GfxPtr(system, IndexBuffer + 24, IndexData);
        U32(graphics, IndexData, 1);
        U32(graphics, IndexData + 4, 1);
        U32(graphics, IndexData + 8, 3);
        U32(graphics, IndexData + 12, 2);
        U32(graphics, IndexData + 28, (2 << 14) | (1 << 7));
        U32(graphics, IndexData + 36, 1);
        U32(graphics, IndexData + 40, 2);
        return (system, graphics);
    }

    private static void WriteArchive(string path, byte[] system, params byte[][] graphicsPages)
    {
        using var file = File.Create(path);
        file.Write("9FPR"u8);
        file.Position = Candidate;
        WriteBlock(file, system);
        foreach (var page in graphicsPages) WriteBlock(file, page);
    }

    private static void WriteBlock(FileStream file, byte[] data)
    {
        using (var zlib = new ZLibStream(file, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);
        file.Write(new byte[(int)((-file.Position) & 15)]);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Fragment mesh test failed: " + name);
    }

    private static void U16(byte[] data, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(at), value);
    private static void U32(byte[] data, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at), value);
    private static void F32(byte[] data, int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(at), value);
    private static void SysPtr(byte[] data, int at, int offset) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(at), 0x50000000UL + (uint)offset);
    private static void GfxPtr(byte[] data, int at, int offset) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(at), 0x60000000UL + (uint)offset);
}
