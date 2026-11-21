using System.Buffers.Binary;
using System.Numerics;
using RpfPreview.Core;

// All bytes are constructed here. No archive or extracted fixture is needed.
const int wrapper = 0x40, header = 0x80, bones = 0x100, inverse = 0x200;
const int parents = 0x2c0, model = 0x300, palette = 0x350;
const uint root = 0x1000, tip = 0x2000, hinge = 0x3000;

byte[] BuildFixture()
{
    var sys = new byte[0x358];
    Pointer(sys, 0x18, wrapper);
    Pointer(sys, wrapper + 0x18, header);
    Pointer(sys, header + 0x28, bones);
    Pointer(sys, header + 0x30, inverse);
    Pointer(sys, header + 0x40, parents);
    U16(sys, header + 0x6c, 3);
    U16(sys, header + 0x6e, 3);
    uint[] hashes = [root, tip, hinge];
    ushort[] links = [ushort.MaxValue, 2, 0];
    for (ushort i = 0; i < hashes.Length; i++)
    {
        int bone = bones + i * 0x50;
        U32(sys, bone + 0x1c, hashes[i]);
        U16(sys, bone + 0x46, links[i]);
        U16(sys, bone + 0x48, i);
        U16(sys, parents + i * 2, links[i]);
        int matrix = inverse + i * 64;
        foreach (int diag in new[] { 0, 5, 10 }) F32(sys, matrix + diag * 4, 1);
        F32(sys, matrix + 0x30, -i);
    }
    U16(sys, model + 0x14, 2);
    Pointer(sys, model + 0x28, palette);
    U32(sys, palette, tip);
    U32(sys, palette + 4, hinge);
    return sys;
}

var fixture = BuildFixture();
var rig = RageSkeleton.ReadForDrawable(fixture, 0) ?? throw new Exception("valid skeleton rejected");
var hashes = RageSkeleton.ReadModelPalette(fixture, model) ?? throw new Exception("valid palette rejected");
Check(rig.Joints.Count == 3 && rig.Joints[0].Parent is null && rig.Joints[1].Parent == 2 && rig.Joints[2].Parent == 0,
    "parent chain and forward parent reference");
Check(rig.TryResolvePalette(hashes, out var mapped) && mapped.SequenceEqual(new[] { 1, 2 }), "preserve palette slot order");
Check(!rig.TryResolvePalette([tip, 0x4000], out mapped) && mapped.Length == 0, "missing hash rejects the full binding");
Matrix4x4 matrix = rig.Joints[2].InverseBindMatrix;
Check(Vector3.Transform(new(3, 0, 0), matrix) == Vector3.UnitX && matrix.M44 == 1, "row-vector inverse bind");

var cyclic = BuildFixture();
U16(cyclic, bones + 2 * 0x50 + 0x46, 1);
U16(cyclic, parents + 4, 1);
Check(RageSkeleton.Read(cyclic, header) is null, "reject a parent cycle");
var inconsistent = BuildFixture();
U16(inconsistent, parents + 2, 0);
Check(RageSkeleton.Read(inconsistent, header) is null, "reject inconsistent parent tables");
var nonFinite = BuildFixture();
F32(nonFinite, inverse, float.NaN);
Check(RageSkeleton.Read(nonFinite, header) is null, "reject a non-finite inverse bind");
var repeated = BuildFixture();
U32(repeated, bones + 2 * 0x50 + 0x1c, tip);
var ambiguous = RageSkeleton.Read(repeated, header) ?? throw new Exception("valid layout rejected");
Check(!ambiguous.TryResolvePalette([tip], out _), "reject ambiguous palette hash");
Check(RageSkeleton.Read(fixture.AsSpan(0, inverse + 63), header) is null, "reject truncated inverse binds");
Check(RageSkeleton.ReadModelPalette(fixture.AsSpan(0, palette + 4), model) is null, "reject truncated palette");
Console.WriteLine("Skeleton and palette smoke tests passed.");

static void Pointer(byte[] sys, int at, int target) => BinaryPrimitives.WriteUInt64LittleEndian(sys.AsSpan(at), 0x50000000UL + (uint)target);
static void U16(byte[] sys, int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(sys.AsSpan(at), value);
static void U32(byte[] sys, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(sys.AsSpan(at), value);
static void F32(byte[] sys, int at, float value) => BinaryPrimitives.WriteSingleLittleEndian(sys.AsSpan(at), value);
static void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
}
