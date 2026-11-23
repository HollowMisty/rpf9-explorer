using System.Buffers.Binary;
using System.Numerics;

namespace RpfPreview.Core;

public sealed record RageSkinBinding(int InfluencesPerVertex, IReadOnlyList<ushort> BoneIndices, IReadOnlyList<byte> BoneWeights);
public sealed record RageSkinCompatibility(int RequiredBones, int MatchedBones, int SkinnedVertices)
{
    public bool Complete => RequiredBones > 0 && RequiredBones == MatchedBones;
}

public static class RageSkinning
{
    public static RageSkinCompatibility GetCompatibility(RageSkeleton skeleton, IEnumerable<RageMesh> meshes)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(meshes);
        var hashes = skeleton.Joints.GroupBy(j => j.Hash).Where(g => g.Count() == 1).Select(g => g.Key).ToHashSet();
        return GetCompatibility(hashes, meshes);
    }

    public static RageSkinCompatibility GetDrivenCompatibility(RageSkeleton skeleton, IEnumerable<RageMesh> meshes,
        IEnumerable<uint> drivenBones)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentNullException.ThrowIfNull(drivenBones);
        var drivers = drivenBones.Take(65537).ToArray();
        if (drivers.Length > 65536) throw new ArgumentException("The driven bone set exceeds the supported size.", nameof(drivenBones));
        var anchors = drivers.ToHashSet();
        int count = skeleton.Joints.Count;
        var children = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        var active = new bool[count]; var pending = new Queue<int>();
        for (int i = 0; i < count; i++)
        {
            var joint = skeleton.Joints[i];
            if (joint.Parent is int parent)
            {
                if (parent < 0 || parent >= count) return new(1, 0, 0);
                children[parent].Add(i);
            }
            if (anchors.Contains(joint.Hash)) { active[i] = true; pending.Enqueue(i); }
        }
        while (pending.TryDequeue(out int current))
            foreach (int child in children[current])
                if (!active[child]) { active[child] = true; pending.Enqueue(child); }
        var hashes = skeleton.Joints.Select((joint, index) => (joint.Hash, Index: index)).GroupBy(j => j.Hash)
            .Where(g => g.Count() == 1 && active[g.First().Index]).Select(g => g.Key).ToHashSet();
        return GetCompatibility(hashes, meshes);
    }

    private static RageSkinCompatibility GetCompatibility(IReadOnlySet<uint> hashes, IEnumerable<RageMesh> meshes)
    {
        var required = new HashSet<uint>(); int vertices = 0;
        foreach (var mesh in meshes)
        {
            if (mesh.Skin is not { } skin || mesh.BonePaletteHashes is not { } palette ||
                mesh.Positions.Count is < 1 or > 2_000_000 || skin.InfluencesPerVertex is < 1 or > 8 ||
                skin.BoneIndices is null || skin.BoneWeights is null || palette.Count is < 1 or > 4096) continue;
            int count = checked(mesh.Positions.Count * skin.InfluencesPerVertex);
            if (skin.BoneIndices.Count != count || skin.BoneWeights.Count != count || vertices > 2_000_000 - mesh.Positions.Count)
                return new(required.Count + 1, 0, vertices);
            vertices += mesh.Positions.Count;
            for (int i = 0; i < count; i++)
            {
                if (skin.BoneWeights[i] == 0) continue;
                int bone = skin.BoneIndices[i];
                if (bone >= palette.Count) return new(required.Count + 1, 0, vertices);
                required.Add(palette[bone]);
            }
        }
        return new(required.Count, required.Count(hashes.Contains), vertices);
    }

    public static RageSkeleton? SelectCompatibleSkeleton(IEnumerable<RageSkeleton> candidates, IEnumerable<RageMesh> meshes)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(meshes);
        var selected = meshes.Take(1025).ToArray();
        if (selected.Length > 1024) return null;
        foreach (var skeleton in candidates.Take(4096))
            if (GetCompatibility(skeleton, selected).Complete) return skeleton;
        return null;
    }

    public static IReadOnlyList<Matrix4x4>? CreateModelPose(RageSkeleton skeleton,
        IReadOnlyDictionary<uint, RageAnimationBonePose> localPose)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(localPose);
        int count = skeleton.Joints.Count;
        if (count is < 1 or > 4096) return null;
        var local = new Matrix4x4[count];
        for (int i = 0; i < count; i++)
        {
            var joint = skeleton.Joints[i];
            if (!Matrix4x4.Invert(joint.InverseBindMatrix, out var bind)) return null;
            if (joint.Parent is int parent)
            {
                if (parent < 0 || parent >= count) return null;
                bind *= skeleton.Joints[parent].InverseBindMatrix;
            }
            if (localPose.TryGetValue(joint.Hash, out var sampled))
            {
                if (sampled is null || !Matrix4x4.Decompose(bind, out var scale, out var rotation, out var translation)) return null;
                scale = sampled.Scale ?? scale; rotation = sampled.Rotation ?? rotation; translation = sampled.Translation ?? translation;
                float q = rotation.LengthSquared();
                if (!Finite(scale) || !Finite(translation) || !float.IsFinite(q) || q < 1e-12f) return null;
                bind = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rotation)) * Matrix4x4.CreateTranslation(translation);
            }
            if (!Finite(bind)) return null;
            local[i] = bind;
        }
        var world = new Matrix4x4[count];
        var state = new byte[count];
        var chain = new List<int>(count);
        for (int start = 0; start < count; start++)
        {
            if (state[start] == 2) continue;
            chain.Clear(); int at = start;
            while (state[at] == 0)
            {
                state[at] = 1; chain.Add(at);
                if (skeleton.Joints[at].Parent is not int parent) break;
                if (parent < 0 || parent >= count) return null;
                at = parent;
            }
            if (skeleton.Joints[chain[^1]].Parent is int last && state[last] == 1) return null;
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                int joint = chain[i];
                world[joint] = skeleton.Joints[joint].Parent is int parent ? local[joint] * world[parent] : local[joint];
                if (!Finite(world[joint])) return null;
                state[joint] = 2;
            }
        }
        return Array.AsReadOnly(world);
    }

    public static RageMesh? Deform(RageMesh mesh, RageSkeleton skeleton,
        IReadOnlyDictionary<uint, RageAnimationBonePose> localPose)
    {
        var modelPose = CreateModelPose(skeleton, localPose);
        return modelPose is null ? null : Deform(mesh, skeleton, modelPose);
    }

    public static RageMesh? Deform(RageMesh mesh, RageSkeleton skeleton, IReadOnlyList<Matrix4x4> modelPose)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(modelPose);
        if (mesh.Skin is not { } skin || mesh.BonePaletteHashes is not { } palette ||
            mesh.Positions.Count is < 1 or > 2_000_000 || skin.InfluencesPerVertex is < 1 or > 8 ||
            skin.BoneIndices is null || skin.BoneWeights is null || palette.Count is < 1 or > 4096 ||
            modelPose.Count != skeleton.Joints.Count || skeleton.Joints.Count > 4096) return null;
        int expected = checked(mesh.Positions.Count * skin.InfluencesPerVertex);
        if (skin.BoneIndices.Count != expected || skin.BoneWeights.Count != expected) return null;
        var byHash = new Dictionary<uint, int>(skeleton.Joints.Count);
        var transforms = new Matrix4x4[skeleton.Joints.Count];
        for (int i = 0; i < skeleton.Joints.Count; i++)
        {
            uint hash = skeleton.Joints[i].Hash;
            if (!byHash.TryAdd(hash, i)) byHash[hash] = -1;
            transforms[i] = skeleton.Joints[i].InverseBindMatrix * modelPose[i];
            if (!Finite(transforms[i])) return null;
        }
        var mapped = new int[palette.Count];
        for (int i = 0; i < mapped.Length; i++) mapped[i] = byHash.GetValueOrDefault(palette[i], -1);
        var positions = new Vector3[mesh.Positions.Count];
        for (int vertex = 0; vertex < positions.Length; vertex++)
        {
            Vector3 source = mesh.Positions[vertex], value = Vector3.Zero;
            if (!Finite(source)) return null;
            int total = 0;
            for (int lane = 0; lane < skin.InfluencesPerVertex; lane++)
            {
                int at = vertex * skin.InfluencesPerVertex + lane, weight = skin.BoneWeights[at];
                if (weight == 0) continue;
                int local = skin.BoneIndices[at];
                if (local >= mapped.Length || mapped[local] < 0) return null;
                value += Vector3.Transform(source, transforms[mapped[local]]) * weight;
                total += weight;
            }
            if (total == 0) return null;
            positions[vertex] = value / total;
            if (!Finite(positions[vertex])) return null;
        }
        return mesh with { Positions = Array.AsReadOnly(positions) };
    }

    internal static RageSkinBinding? ReadNativeBinding(ReadOnlySpan<byte> system, int declaration,
        ReadOnlySpan<byte> vertex, int count, int boneCount)
    {
        if (count is < 1 or > 1_000_000 || boneCount is < 1 or > 4096 ||
            declaration < 0 || declaration > system.Length - 320 || vertex.Length < 96) return null;
        int weightSize = system[declaration + 0xD0 + 16];
        int weightOffset0 = -1, weightOffset1 = -1, indexOffset0, indexOffset1 = -1;
        int indexSize = system[declaration + 0xD0 + 20], indexFormat = system[declaration + 0x104 + 20];
        int influences;
        if (weightSize == 0)
        {
            if (system[declaration + 0xD0 + 17] != 0 || system[declaration + 0xD0 + 21] != 0 ||
                !((indexSize == 1 && indexFormat == 62) || (indexSize == 2 && indexFormat == 57)) ||
                !Attribute(system, declaration, vertex, 20, indexSize, indexFormat, 8, count, out indexOffset0)) return null;
            influences = 1;
        }
        else
        {
            if (!Attribute(system, declaration, vertex, 16, 4, 28, 6, count, out weightOffset0) ||
                !((indexSize == 4 && indexFormat == 30) || (indexSize == 16 && indexFormat == 3)) ||
                !Attribute(system, declaration, vertex, 20, indexSize, indexFormat, 8, count, out indexOffset0)) return null;
            bool second = system[declaration + 0xD0 + 17] != 0 || system[declaration + 0xD0 + 21] != 0;
            influences = second ? 8 : 4;
            if (second && (!Attribute(system, declaration, vertex, 17, 4, 28, 7, count, out weightOffset1) ||
                !Attribute(system, declaration, vertex, 21, indexSize, indexFormat, 9, count, out indexOffset1))) return null;
        }
        var indices = new ushort[checked(count * influences)];
        var weights = new byte[indices.Length];
        for (int i = 0; i < count; i++)
        {
            int sum = 0;
            for (int lane = 0; lane < influences; lane++)
            {
                int group = lane / 4, component = lane & 3;
                int indexAt = (group == 0 ? indexOffset0 : indexOffset1) + i * indexSize;
                int bone = indexSize switch {
                    1 => vertex[indexAt], 2 => BinaryPrimitives.ReadUInt16LittleEndian(vertex[indexAt..]),
                    4 => vertex[indexAt + component],
                    16 => (int)(BinaryPrimitives.ReadUInt32LittleEndian(vertex[(indexAt + component * 4)..]) & 4095), _ => -1 };
                byte weight = influences == 1 ? (byte)255 : vertex[(group == 0 ? weightOffset0 : weightOffset1) + i * 4 + component];
                if (weight != 0 && (bone < 0 || bone >= boneCount)) return null;
                int output = i * influences + lane;
                indices[output] = (ushort)bone; weights[output] = weight; sum += weight;
            }
            if (sum == 0) return null;
        }
        return new(influences, Array.AsReadOnly(indices), Array.AsReadOnly(weights));
    }

    private static bool Attribute(ReadOnlySpan<byte> system, int declaration, ReadOnlySpan<byte> vertex,
        int slot, int size, int format, int descriptorWord, int count, out int offset)
    {
        offset = 0;
        if (system[declaration + 0xD0 + slot] != size || system[declaration + 0x104 + slot] != format) return false;
        uint at = BinaryPrimitives.ReadUInt32LittleEndian(system[(declaration + slot * 4)..]);
        uint native = BinaryPrimitives.ReadUInt32LittleEndian(vertex[(descriptorWord * 4)..]);
        if (at > vertex.Length || (long)size * count > vertex.Length - at || ((native >> 7) & 0x1FFFFFC) != at) return false;
        offset = (int)at; return true;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) &&
        Math.Abs(v.X) < 10_000_000 && Math.Abs(v.Y) < 10_000_000 && Math.Abs(v.Z) < 10_000_000;
    private static bool Finite(Matrix4x4 m) => float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
        float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
        float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
        float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
}
