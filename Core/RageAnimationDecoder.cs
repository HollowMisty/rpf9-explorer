using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace RpfPreview.Core;

public sealed class RageAnimationClip
{
    private readonly AnimationPlayback[] _animations;
    public string Name { get; }
    public double DurationSeconds { get; }
    public double FrameRate { get; }
    public IReadOnlyList<uint> BoneHashes { get; }
    public int SourceAnimationCount => _animations.Length;

    internal RageAnimationClip(string name, double duration, AnimationPlayback[] animations)
    {
        Name = name; DurationSeconds = duration; _animations = animations;
        FrameRate = animations.Max(a => a.Animation.FrameRate);
        BoneHashes = Array.AsReadOnly(animations.SelectMany(a => a.Animation.Tracks.Select(t => t.Bone)).Distinct().ToArray());
    }

    public IReadOnlyDictionary<uint, RageAnimationBonePose> Sample(double timeSeconds)
    {
        if (!double.IsFinite(timeSeconds)) throw new ArgumentOutOfRangeException(nameof(timeSeconds));
        double time = Math.Clamp(timeSeconds, 0, DurationSeconds);
        var result = new Dictionary<uint, RageAnimationBonePose>();
        foreach (var playback in _animations)
        {
            double animationTime = Math.Clamp(playback.Start + time * playback.Rate, playback.Start, playback.End);
            foreach (var track in playback.Animation.Tracks)
            {
                double frame = Math.Clamp(animationTime * playback.Animation.FrameRate, 0, track.Values.Length - 1);
                int a = (int)frame, b = Math.Min(a + 1, track.Values.Length - 1); float amount = (float)(frame - a);
                var pose = result.GetValueOrDefault(track.Bone) ?? new(null, null, null);
                if (track.Type == RageAnimationDecoder.RotationHash)
                {
                    var x = track.Values[a]; var y = track.Values[b];
                    var rotation = Quaternion.Normalize(Quaternion.Slerp(new(x.X, x.Y, x.Z, x.W), new(y.X, y.Y, y.Z, y.W), amount));
                    result[track.Bone] = pose with { Rotation = rotation };
                }
                else
                {
                    Vector4 value = Vector4.Lerp(track.Values[a], track.Values[b], amount);
                    var vector = new Vector3(value.X, value.Y, value.Z);
                    result[track.Bone] = track.Type == RageAnimationDecoder.TranslationHash ? pose with { Translation = vector } : pose with { Scale = vector };
                }
            }
        }
        return result;
    }
}

public sealed record RageAnimationSet(string Name, IReadOnlyList<RageAnimationClip> Clips, IReadOnlyList<string> Diagnostics);

internal sealed record AnimationTrack(uint Bone, uint Type, Vector4[] Values);
internal sealed record DecodedAnimation(double Duration, double FrameRate, AnimationTrack[] Tracks);
internal sealed record AnimationPlayback(DecodedAnimation Animation, double Start, double End, double Rate);

public static class RageAnimationDecoder
{
    public const uint RotationHash = 0x98E1750C, TranslationHash = 0xF791A95F, ScaleHash = 0x7AAA9F60;
    private const int MaxBytes = 64 * 1024 * 1024, MaxObjects = 65536, MaxFrames = 120000, MaxTracks = 4096, MaxSamples = 4_000_000;

    public static RageAnimationSet? Decode(ReadOnlySpan<byte> system, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (system.Length is < 0xB0 or > MaxBytes) return null;
        try { return new Reader(system.ToArray(), cancellationToken).Decode(); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException) { return null; }
    }

    private sealed class Reader(byte[] bytes, CancellationToken cancellationToken)
    {
        private readonly byte[] _data = bytes;
        private readonly Dictionary<int, DecodedAnimation?> _animations = new();
        private readonly List<string> _diagnostics = [];
        private int _samples;
        private void Require(bool condition) { if (!condition) throw new InvalidDataException("Unsupported or inconsistent animation layout."); }
        private void Fits(int at, int size) => Require(at >= 0 && size >= 0 && at <= _data.Length - size);
        private ushort U16(int at) { Fits(at, 2); return BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(at)); }
        private uint U32(int at) { Fits(at, 4); return BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(at)); }
        private ulong U64(int at) { Fits(at, 8); return BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(at)); }
        private float F32(int at) { float f = BitConverter.UInt32BitsToSingle(U32(at)); Require(float.IsFinite(f)); return f; }
        private int Pointer(int at, int size = 1)
        {
            ulong value = U64(at); Require(value >= 0x50000000 && value - 0x50000000 <= (ulong)_data.Length);
            int offset = checked((int)(value - 0x50000000)); Fits(offset, size); return offset;
        }
        private string Text(int at)
        {
            Fits(at, 16); bool inline = (_data[at + 15] & 0x80) != 0;
            int length = inline ? _data[at + 15] & 0x7f : U16(at + 8);
            Require(length is > 0 and <= 1024 && (!inline || length <= 15));
            int offset = inline ? at : Pointer(at, checked(length + 1));
            if (!inline) Require(_data[offset + length] == 0);
            var text = _data.AsSpan(offset, length); Require(text.IndexOfAnyExceptInRange((byte)32, (byte)126) < 0);
            return Encoding.ASCII.GetString(text);
        }
        private List<int> Map(int at)
        {
            int buckets = checked((int)U32(at + 8)), count = checked((int)U32(at + 16));
            Require(buckets is > 0 and <= MaxObjects && count is >= 0 and <= MaxObjects);
            int array = Pointer(at, checked(buckets * 8)); var seen = new HashSet<int>(); var values = new List<int>(count);
            for (int i = 0; i < buckets; i++)
            {
                int linkAt = array + 8 * i;
                while (U64(linkAt) != 0)
                {
                    int node = Pointer(linkAt, 24); Require(seen.Add(node) && values.Count < count);
                    values.Add(Pointer(node + 8, 0x38)); linkAt = node + 16;
                }
            }
            Require(values.Count == count); return values;
        }

        public RageAnimationSet Decode()
        {
            Require(U64(0) == 0 && _data.AsSpan(0x10, 0x28).IndexOfAnyExcept((byte)0) < 0);
            Pointer(8, 16); string name = Text(0x38); var clips = new List<RageAnimationClip>();
            var packs = new HashSet<int>(); int packCount = U16(0x78); Require(packCount <= 4096 && packCount == U16(0x7a));
            if (packCount > 0)
            {
                int array = Pointer(0x70, checked(packCount * 16));
                for (int i = 0; i < packCount; i++)
                    if (U64(array + 16 * i) != 0)
                    {
                        int pack = Pointer(array + 16 * i, 0x70); Require(packs.Add(pack));
                        foreach (int animation in Map(pack + 0x48)) GetAnimation(animation);
                    }
            }
            foreach (int clip in Map(0x48))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (U32(clip + 0x20) >= 16) { _diagnostics.Add("A motion/blend container is not a directly playable clip."); continue; }
                    string clipName = Text(clip + 0x28); Fits(clip, 0x80);
                    double duration = F32(clip + 0x78); Require(duration is > 0 and <= 3600);
                    int descriptor = Pointer(clip + 0x68, 16), count = U16(descriptor + 8); Require(count is > 0 and <= 512 && count == U16(descriptor + 10));
                    int list = Pointer(descriptor, checked(count * 0x38)); var playbacks = new List<AnimationPlayback>(); bool complete = true;
                    for (int i = 0; i < count; i++)
                    {
                        int entry = list + i * 0x38; int offset = Pointer(entry, 0x70); var animation = GetAnimation(offset);
                        double start = F32(entry + 0x28), end = F32(entry + 0x2c), rate = F32(entry + 0x30);
                        Require(start >= 0 && end > start && rate is > 0 and <= 100 && Math.Abs((end - start) / rate - duration) < Math.Max(0.1, duration * 0.02));
                        if (animation == null) { complete = false; continue; }
                        Require(end <= animation.Duration + 0.1);
                        playbacks.Add(new(animation, start, end, rate));
                    }
                    if (complete && playbacks.Count > 0 && playbacks.Any(p => p.Animation.Tracks.Length > 0)) clips.Add(new(clipName, duration, playbacks.ToArray()));
                    else _diagnostics.Add(clipName + ": one or more animation tracks are unavailable.");
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException)
                { _diagnostics.Add("A clip record was rejected: " + ex.Message); }
            }
            return new(name, Array.AsReadOnly(clips.ToArray()), Array.AsReadOnly(_diagnostics.Distinct().Take(128).ToArray()));
        }

        private DecodedAnimation? GetAnimation(int at)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_animations.TryGetValue(at, out var cached)) return cached;
            Require(_animations.Count < 8192); _animations[at] = null;
            try { return _animations[at] = Animation(at); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException or IndexOutOfRangeException)
            { _diagnostics.Add($"Animation at 0x{at:X}: {ex.Message}"); return null; }
        }

        private DecodedAnimation Animation(int at)
        {
            Fits(at, 0x70); int frames = U16(at + 0x24); double duration = F32(at + 0x28);
            Require(frames is > 1 and <= MaxFrames && duration is > 0 and <= 3600); double rate = (frames - 1) / duration;
            Require(rate is >= 1 and <= 240); int tracks = U16(at + 0x68), sequences = U16(at + 0x58);
            Require(tracks is > 0 and <= MaxTracks && tracks == U16(at + 0x6a) && sequences is > 0 and <= 4096 && sequences == U16(at + 0x5a));
            int trackArray = Pointer(at + 0x60, checked(tracks * 12)), sequenceArray = Pointer(at + 0x50, checked(sequences * 8));
            var output = new Dictionary<int, AnimationTrack>();
            for (int i = 0; i < tracks; i++)
            {
                int record = trackArray + i * 12; uint bone = U32(record), type = U32(record + 4), format = U32(record + 8);
                if (type is not (RotationHash or TranslationHash or ScaleHash)) continue;
                Require(format == (type == RotationHash ? 0u : 2u));
                _samples = checked(_samples + frames); Require(_samples <= MaxSamples);
                output.Add(i, new(bone, type, new Vector4[frames]));
            }
            int frameBase = 0;
            for (int i = 0; i < sequences; i++)
            {
                int sequence = Pointer(sequenceArray + 8 * i, 0x20); int count = U16(sequence + 0x16);
                Require(count is > 0 and <= MaxFrames && count <= frames - frameBase);
                DecodeSequence(sequence, tracks, output, frameBase, count);
                frameBase += count - 1;
            }
            Require(frameBase == frames - 1);
            return new(duration, rate, output.Values.ToArray());
        }

        private void DecodeSequence(int at, int tracks, Dictionary<int, AnimationTrack> output, int frameBase, int frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = checked((int)U32(at + 4)), frameOffset = checked((int)U32(at + 12)), stride = U16(at + 0x18);
            Require(_data[at + 0x1e] == 11 && length is > 0 and <= MaxBytes && frameOffset >= 0 && frameOffset <= length);
            int data = checked(at + 0x20); Fits(data, length); int footer = checked(data + frameOffset + frames * stride); Fits(footer, 22); Require(footer + 22 <= data + length);
            var counts = new int[11]; var maps = new ushort[11][]; int mapping = footer + 22;
            for (int type = 0; type < 11; type++)
            {
                counts[type] = U16(footer + type * 2); Require(counts[type] <= tracks * 4);
                int padded = checked((counts[type] + 3) / 4 * 4); Require(mapping + padded * 2 <= data + length);
                maps[type] = new ushort[counts[type]];
                for (int i = 0; i < counts[type]; i++) { ushort key = U16(mapping + i * 2); Require((key >> 2) < tracks); maps[type][i] = key; }
                mapping += padded * 2;
            }
            Require(counts[6] == 0 && counts[9] == 0);
            var vectors = output.Keys.ToDictionary(k => k, _ => Enumerable.Repeat(new Vector4(float.NaN), frames).ToArray());
            var missing = new Dictionary<int, int>(); var fullQuaternion = new HashSet<int>(); int cursor = data, frameBits = 0, quantizedBits = 0;
            for (int type = 0; type < 11; type++)
                foreach (ushort key in maps[type])
                {
                    int track = key >> 2, component = key & 3; bool wanted = vectors.TryGetValue(track, out var values);
                    if (type is 0 or 1)
                    {
                        Require(component == 0); var value = new Vector4(F32(cursor), F32(cursor + 4), F32(cursor + 8), 0); cursor += 12;
                        if (type == 0) { float norm = value.X * value.X + value.Y * value.Y + value.Z * value.Z; Require(norm <= 1.001f); value.W = MathF.Sqrt(Math.Max(0, 1 - norm)); fullQuaternion.Add(track); }
                        if (wanted) Array.Fill(values!, value); continue;
                    }
                    if (type == 7) { Require(missing.TryAdd(track, component)); continue; }
                    if (type == 8) { Require(component == 0); fullQuaternion.Add(track); continue; }
                    float[]? varying = null; float constant = 0;
                    if (type == 2) { constant = F32(cursor); cursor += 4; }
                    else if (type == 3)
                    {
                        if (wanted) { varying = new float[frames]; for (int frame = 0; frame < frames; frame++) varying[frame] = BitConverter.UInt32BitsToSingle(Bits(data + frameOffset + frame * stride, frameBits, 32, stride)); }
                        frameBits += 32;
                    }
                    else if (type == 4)
                    {
                        int bits = checked((int)U32(cursor)); float quantum = F32(cursor + 4), offset = F32(cursor + 8); cursor += 12; Require(bits is >= 0 and <= 32);
                        if (wanted) { varying = new float[frames]; for (int frame = 0; frame < frames; frame++) varying[frame] = Bits(data + frameOffset + frame * stride, frameBits, bits, stride) * quantum + offset; }
                        frameBits += bits; quantizedBits += bits;
                    }
                    else if (type == 5)
                    {
                        int bits = checked((int)U32(cursor)), valueBits = checked((int)U32(cursor + 4)), words = checked((int)U32(cursor + 8)); float quantum = F32(cursor + 12), offset = F32(cursor + 16); cursor += 20;
                        Require(bits is >= 0 and <= 32 && valueBits is > 0 and <= 32 && words is > 0 and <= 65536); int bytes = checked(words * 4); Fits(cursor, bytes);
                        if (wanted)
                        {
                            varying = new float[frames];
                            for (int frame = 0; frame < frames; frame++) { uint index = Bits(data + frameOffset + frame * stride, frameBits, bits, stride); long bit = (long)index * valueBits; Require(bit <= int.MaxValue); varying[frame] = Bits(cursor, (int)bit, valueBits, bytes) * quantum + offset; }
                        }
                        cursor += bytes; frameBits += bits;
                    }
                    else if (type == 10)
                    {
                        int words = checked((int)U32(cursor)); Require(words is >= 4 and <= 1_000_000); int bytes = checked(words * 4); Fits(cursor, bytes); Require(cursor + bytes <= data + frameOffset);
                        if (wanted) varying = DecodeDelta(cursor, bytes, frames); cursor += bytes;
                    }
                    else throw new InvalidDataException("Unknown animation channel type.");
                    Require(cursor <= data + frameOffset && frameBits <= stride * 8);
                    if (wanted)
                        for (int frame = 0; frame < frames; frame++)
                        {
                            float value = varying == null ? constant : varying[frame]; Require(float.IsFinite(value));
                            var vector = values![frame]; if (component == 0) vector.X = value; else if (component == 1) vector.Y = value; else if (component == 2) vector.Z = value; else vector.W = value; values[frame] = vector;
                        }
                }
            Require(cursor == data + frameOffset && quantizedBits == U16(at + 0x1c));
            foreach (var (track, values) in vectors)
                for (int frame = 0; frame < frames; frame++)
                {
                    if ((frame & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var value = values[frame];
                    if (output[track].Type == RotationHash)
                    {
                        if (missing.TryGetValue(track, out int omitted))
                        {
                            Require(float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z));
                            float missingValue = MathF.Sqrt(Math.Max(0, 1 - value.X * value.X - value.Y * value.Y - value.Z * value.Z));
                            float[] packed = [value.X, value.Y, value.Z]; var complete = new float[4]; int next = 0;
                            for (int component = 0; component < 4; component++) complete[component] = component == omitted ? missingValue : packed[next++];
                            value = new(complete[0], complete[1], complete[2], complete[3]);
                        }
                        else Require(fullQuaternion.Contains(track));
                        Require(Finite(value) && value.LengthSquared() is > 0.5f and < 1.5f); value = Vector4.Normalize(value);
                    }
                    else { Require(float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)); value.W = 0; }
                    output[track].Values[frameBase + frame] = value;
                }
        }

        private float[] DecodeDelta(int at, int bytes, int frames)
        {
            int positionBits = _data[at + 4], startBits = _data[at + 5], laneWords = _data[at + 6];
            Require(positionBits <= 32 && startBits <= 32 && _data[at + 7] == 0 && laneWords > 0);
            float quantum = F32(at + 8), offset = F32(at + 12); int chunks = (frames + 63) / 64;
            int laneBytes = checked(laneWords * 32), laneAt = checked(at + bytes - laneBytes); Require(laneAt >= at + 16);
            int controlBytes = laneAt - at - 16, bit = checked(chunks * positionBits); Require(bit <= controlBytes * 8);
            var starts = new uint[chunks]; for (int i = 0; i < chunks; i++) { starts[i] = Bits(at + 16, bit, startBits, controlBytes); bit += startBits; }
            var widths = new int[(frames + 6) / 8];
            for (int i = 0; i < widths.Length; i++)
            {
                int width = 0; while (Bits(at + 16, bit++, 1, controlBytes) == 0) { width++; Require(width <= 32); } widths[i] = width;
            }
            int[] laneBits = new int[8]; var values = new float[frames]; long current = starts[0], velocity = 0; values[0] = current * quantum + offset;
            for (int frame = 1; frame < frames; frame++)
            {
                if ((frame & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                int delta = frame - 1, lane = delta & 7, width = widths[delta / 8], pos = laneBits[lane]; Require(pos + width <= laneWords * 32);
                uint encoded = InterleavedBits(laneAt, lane, pos, width, laneWords); laneBits[lane] += width;
                long change = (encoded & 1) == 0 ? encoded / 2L : -(encoded + 1L) / 2;
                velocity = checked(velocity + change); current = checked(current + velocity);
                if ((frame & 63) == 0) { Require(current == starts[frame / 64]); current = starts[frame / 64]; velocity = 0; }
                values[frame] = current * quantum + offset; Require(float.IsFinite(values[frame]));
            }
            return values;
        }

        private uint InterleavedBits(int at, int lane, int bit, int count, int words)
        {
            Require(count is >= 0 and <= 32 && bit >= 0 && bit + count <= words * 32);
            if (count == 0) return 0;
            int index = bit / 32, shift = bit & 31; ulong value = U32(at + (index * 8 + lane) * 4);
            if (shift + count > 32) value |= (ulong)U32(at + ((index + 1) * 8 + lane) * 4) << 32;
            return (uint)((value >> shift) & (count == 32 ? uint.MaxValue : (1UL << count) - 1));
        }
        private uint Bits(int at, int bit, int count, int bytes)
        {
            Require(count is >= 0 and <= 32 && bit >= 0 && (long)bit + count <= (long)bytes * 8); Fits(at, bytes);
            uint result = 0; int written = 0;
            while (written < count) { int position = bit + written, take = Math.Min(8 - (position & 7), count - written); result |= (uint)((_data[at + position / 8] >> (position & 7)) & ((1 << take) - 1)) << written; written += take; }
            return result;
        }
        private static bool Finite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
    }
}
