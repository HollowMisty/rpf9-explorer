using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RpfPreview.Core;


public static class RecoveryIndexCache
{
    public sealed record CachedIndex(string Schema, string Kind, string Source, long SourceLength,
        long SourceWriteTicks, string HeaderSha256, string Coverage, IReadOnlyList<ArchiveEntry> Entries)
    {
        public bool IsComplete { get; init; }
    }
    private const string Schema = "rpfpreview-payload-index/1";
    private const string ModelSchema = "rpfpreview-payload-index/models/2";

    public static void Save(string kind, string source, IReadOnlyList<ArchiveEntry> entries, string coverage, bool complete = false)
    {
        source = Path.GetFullPath(source);
        var file = new FileInfo(source);
        var header = ReadHeader(source);
        Validate(kind, file.Length, entries);
        var index = new CachedIndex(CurrentSchema(kind), kind, source, file.Length, file.LastWriteTimeUtc.Ticks,
            Convert.ToHexString(SHA256.HashData(header)), coverage, entries) { IsComplete = complete };
        var path = CachePath(kind, source);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                JsonSerializer.Serialize(output, index, new JsonSerializerOptions { IgnoreReadOnlyProperties = true });
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, path, true); break; }
                catch (Exception ex) when (attempt < 8 && ex is IOException or UnauthorizedAccessException)
                { Thread.Sleep(Math.Min(50 << attempt, 500)); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static CachedIndex? Load(string kind, string source, long length, byte[] header, int firstIndex = 0)
        => LoadIndex(kind, source, length, header, firstIndex, false);

    public static CachedIndex? LoadLegacyModels(string source, long length, byte[] header, int firstIndex = 0)
        => LoadIndex("models", source, length, header, firstIndex, true);

    private static string CurrentSchema(string kind) => kind == "models" ? ModelSchema : Schema;

    private static CachedIndex? LoadIndex(string kind, string source, long length, byte[] header, int firstIndex, bool legacyModels)
    {
        try
        {
            source = Path.GetFullPath(source);
            var path = CachePath(kind, source);
            long maximum = kind == "models" ? 512L * 1024 * 1024 : 256L * 1024 * 1024;
            if (!File.Exists(path) || new FileInfo(path).Length > maximum) return null;
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var index = JsonSerializer.Deserialize<CachedIndex>(input);
            if (index == null || index.Schema != (legacyModels ? Schema : CurrentSchema(kind)) || index.Kind != kind
                || !string.Equals(index.Source, source, StringComparison.OrdinalIgnoreCase)
                || index.SourceLength != length || index.SourceWriteTicks != File.GetLastWriteTimeUtc(source).Ticks
                || index.HeaderSha256 != Convert.ToHexString(SHA256.HashData(header.AsSpan(0, Math.Min(64, header.Length))))) return null;
            Validate(kind, length, index.Entries, legacyModels);
            return index with
            {
                Entries = index.Entries.Select((e, i) => e with { Index = checked(firstIndex + i) }).ToArray(),
                Coverage = legacyModels ? "Legacy model index; drawable dictionaries require refresh. " + index.Coverage : index.Coverage,
                IsComplete = !legacyModels && index.IsComplete
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or OverflowException)
        { return null; }
    }

    public static string CachePath(string kind, string source)
    {
        if (kind is not ("models" or "textures" or "audio" or "shaders")) throw new ArgumentException("Unknown payload index kind.");
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(source).ToUpperInvariant())));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RpfPreview", "Indexes", kind + "-" + identity[..24] + ".json");
    }
    private static byte[] ReadHeader(string source)
    {
        using var file = File.OpenRead(source);
        byte[] data = new byte[(int)Math.Min(64, file.Length)]; file.ReadExactly(data); return data;
    }
    private static void Validate(string kind, long length, IReadOnlyList<ArchiveEntry> entries, bool legacyModels = false)
    {
        string compression = kind switch { "models" => "Recovered model", "textures" => "Recovered texture dictionary", "audio" => "Recovered AWC", "shaders" => "Recovered raw deflate shader", _ => throw new InvalidDataException("Unknown cache kind.") };
        int maximum = kind == "models" ? Rpf9ModelRecovery.MaxRecoveredModels : 250_000;
        if (entries == null || entries.Count > maximum) throw new InvalidDataException("Invalid payload cache count.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.Compression != compression || entry.IsDirectory || entry.IsEncrypted || entry.Limitation != null
                || entry.Offset < 0 || entry.Offset >= length || entry.StoredSize < 1 || entry.Size < 1
                || !paths.Add(ArchiveReader.ValidateMemberPath(entry.Path))) throw new InvalidDataException("Invalid cached payload.");
            if (entry.Segments is { Count: > 0 } segments)
            {
                if (segments.Count > 4096 || segments.Sum(s => s.Size) != entry.Size || segments.Sum(s => s.StoredSize) != entry.StoredSize)
                    throw new InvalidDataException("Invalid cached resource segments.");
                foreach (var segment in segments)
                    if (segment.Offset < entry.Offset || segment.StoredSize < 6 || segment.Size < 1 || segment.Offset > length || segment.StoredSize > length - segment.Offset)
                        throw new InvalidDataException("Cached resource segment exceeds its source.");
            }
            else if (entry.StoredSize > length - entry.Offset || kind is not ("audio" or "shaders")) throw new InvalidDataException("Cached payload exceeds its source.");
            if (kind == "shaders" && (entry.Size > Rpf9ShaderRecovery.MaxShaderBytes || entry.StoredSize > Rpf9ShaderRecovery.MaxStoredBytes))
                throw new InvalidDataException("Cached shader exceeds the validated decoder limits.");
            if (kind == "models")
            {
                if (entry.ResourceSystemBytes is not int systemBytes || systemBytes < 176 ||
                    systemBytes > RageModelDecoder.MaxSystemBytes || systemBytes >= entry.Size ||
                    entry.Size - systemBytes > RageModelDecoder.MaxGraphicsBytes ||
                    entry.Segments is not { Count: > 0 } modelSegments || modelSegments[0].Offset != entry.Offset)
                    throw new InvalidDataException("Invalid cached model resource boundary.");
                long prefix = 0, previousEnd = entry.Offset; bool boundary = false;
                foreach (var segment in modelSegments)
                {
                    if (segment.Offset < previousEnd) throw new InvalidDataException("Cached model resource segments overlap.");
                    previousEnd = checked(segment.Offset + segment.StoredSize);
                    prefix = checked(prefix + segment.Size); boundary |= prefix == systemBytes;
                }
                if (!boundary || entry.ResourceDrawableOffset is int drawableOffset &&
                    (legacyModels || drawableOffset < 64 || drawableOffset > systemBytes - 176))
                    throw new InvalidDataException("Invalid cached model drawable selection.");
            }
            else if (entry.ResourceDrawableOffset is not null)
                throw new InvalidDataException("Only cached models can select a drawable.");
        }
    }
}
