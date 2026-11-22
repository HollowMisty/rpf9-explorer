// Optional external BCPack initial-history tables for BC1/BC3/BC4/BC5, read at run time from a locally generated
// JSON file or directory (Leonida's tools/bcpack_history_calib.py writes bc1.json..bc7.json). The schema, lookup and
// merge rules follow Leonida's pure-Python decoder (tools/bcpack_tables_bptc.py load_bc_history and tools/bcpack.py
// _resolve_bc_history), so both decoders give the same output for the same tables.
// Nothing here is game data: the tables are supplied by the user from their own files and never embedded.

using System.Globalization;
using System.Text.Json;

namespace RpfPreview.Core;

/// <summary>
/// External BCPack initial-history tables for codecs 1 (BC1), 3 (BC3), 4 (BC4) and 5 (BC5).
/// <para>
/// <see cref="Load"/> takes a directory holding <c>bc1.json</c>..<c>bc5.json</c> (each optional) or one JSON file
/// with <c>"bc1"</c>..<c>"bc5"</c> sections (or a single section marked with <c>"codec": "bcN"</c>). A section has
/// <c>confirmed</c> and <c>provisional</c> tables (a 256-element list or an index-keyed object of endpoint tuples: 6
/// integers for BC1, 8 for BC3, 2 for BC4, 4 for BC5) and an optional <c>replace</c> flag. Without <c>replace</c> the
/// entries are laid over the built-in tables (an external confirmed entry wins over everything, an external
/// provisional entry wins over a built-in provisional one but never over a confirmed one); with <c>replace</c> they
/// stand alone. A codec without a section keeps the built-in table. BC6H/BC7 sections (<c>bc6h.json</c>,
/// <c>bc7.json</c>) are ignored here because this decoder does not rebuild those codecs yet.
/// </para>
/// </summary>
public sealed class BcpackHistory
{
    private static readonly (int Codec, string Key, int[] Mods)[] Specs =
    [
        (1, "bc1", [32, 64, 32, 32, 64, 32]),
        (3, "bc3", [256, 256, 32, 64, 32, 32, 64, 32]),
        (4, "bc4", [256, 256]),
        (5, "bc5", [256, 256, 256, 256])
    ];

    internal Bc1HistoryEntry[] Bc1 { get; }
    internal Bc3HistoryEntry[] Bc3 { get; }
    internal Bc4HistoryEntry[] Bc4 { get; }
    internal Bc5HistoryEntry[] Bc5 { get; }

    /// <summary>The file or directory the tables came from.</summary>
    public string Source { get; }

    /// <summary>The codec keys (<c>bc1</c>, <c>bc3</c>, <c>bc4</c>, <c>bc5</c>) that had a section.</summary>
    public IReadOnlyList<string> Codecs { get; }

    private BcpackHistory(string source, Bc1HistoryEntry[] bc1, Bc3HistoryEntry[] bc3, Bc4HistoryEntry[] bc4, Bc5HistoryEntry[] bc5, IReadOnlyList<string> codecs)
    {
        Source = source; Bc1 = bc1; Bc3 = bc3; Bc4 = bc4; Bc5 = bc5; Codecs = codecs;
    }

    /// <summary>
    /// Reads every BC1/BC3/BC4/BC5 section from <paramref name="path"/> (a JSON file or a directory of
    /// <c>bcN.json</c> files). A malformed table throws <see cref="InvalidDataException"/>; a missing path throws
    /// <see cref="FileNotFoundException"/>.
    /// </summary>
    public static BcpackHistory Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string full = Path.GetFullPath(path);
        bool directory = Directory.Exists(full);
        if (!directory && !File.Exists(full)) throw new FileNotFoundException($"BCPack history path not found: {path}", path);

        var parsed = new Dictionary<int, Section>();
        JsonElement? file = null;
        foreach (var (codec, key, mods) in Specs)
        {
            JsonElement? section;
            if (directory)
            {
                string candidate = Path.Combine(full, key + ".json");
                section = File.Exists(candidate) ? FindSection(ReadDocument(candidate), key) : null;
            }
            else
            {
                file ??= ReadDocument(full);
                section = FindSection(file.Value, key);
            }
            if (section is { } s) parsed[codec] = ParseSection(s, key, mods);
        }

        return new BcpackHistory(
            full,
            Merge(1, parsed, BcpackTables.Bc1History, h => h.Status, (v, st) => new Bc1HistoryEntry((byte)v[0], (byte)v[1], (byte)v[2], (byte)v[3], (byte)v[4], (byte)v[5], st)),
            Merge(3, parsed, BcpackTables.Bc3History, h => h.Status, (v, st) => new Bc3HistoryEntry((byte)v[0], (byte)v[1], (byte)v[2], (byte)v[3], (byte)v[4], (byte)v[5], (byte)v[6], (byte)v[7], st)),
            Merge(4, parsed, BcpackTables.Bc4History, h => h.Status, (v, st) => new Bc4HistoryEntry((byte)v[0], (byte)v[1], st)),
            Merge(5, parsed, BcpackTables.Bc5History, h => h.Status, (v, st) => new Bc5HistoryEntry((byte)v[0], (byte)v[1], (byte)v[2], (byte)v[3], st)),
            Specs.Where(s => parsed.ContainsKey(s.Codec)).Select(s => s.Key).ToArray());
    }

    private sealed record Section(Dictionary<int, int[]> Confirmed, Dictionary<int, int[]> Provisional, bool Replace);

    private static JsonElement ReadDocument(string file)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            root = doc.RootElement.Clone();
        }
        catch (JsonException e) { throw new InvalidDataException($"{file}: {e.Message}", e); }
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{file}: expected a JSON object");
        return root;
    }

    // A document holds the codec's section under its key, or is that section itself when it says "codec": "<key>".
    private static JsonElement? FindSection(JsonElement data, string key)
    {
        if (data.TryGetProperty(key, out var section)) return section;
        if (data.TryGetProperty("codec", out var codec) && codec.ValueKind == JsonValueKind.String && codec.GetString() == key) return data;
        return null;
    }

    private static Section ParseSection(JsonElement sec, string key, int[] mods)
    {
        if (sec.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{key} history section must be a JSON object");
        if (sec.TryGetProperty("codec", out var codec) && !(codec.ValueKind == JsonValueKind.String && codec.GetString() == key))
            throw new InvalidDataException($"expected codec '{key}', got {(codec.ValueKind == JsonValueKind.String ? $"'{codec.GetString()}'" : codec.GetRawText())}");
        var confirmed = ParseMap(sec, "confirmed", key, mods);
        var provisional = ParseMap(sec, "provisional", key, mods);
        bool replace = sec.TryGetProperty("replace", out var r) && IsTruthy(r);
        return new(confirmed, provisional, replace);
    }

    private static Dictionary<int, int[]> ParseMap(JsonElement sec, string label, string key, int[] mods)
    {
        var map = new Dictionary<int, int[]>();
        if (!sec.TryGetProperty(label, out var table) || table.ValueKind == JsonValueKind.Null) return map;
        foreach (var (index, vals) in Entries(table))
        {
            if (vals.ValueKind != JsonValueKind.Array || vals.GetArrayLength() != mods.Length)
                throw new InvalidDataException($"{key} {label}[{index}] must have {mods.Length} integers");
            int[] row = new int[mods.Length]; int c = 0;
            foreach (var v in vals.EnumerateArray())
            {
                long iv = ToInteger(v, $"{key} {label}[{index}]");
                if (iv < 0 || iv >= mods[c]) throw new InvalidDataException($"{key} {label}[{index}] value {iv} outside 0..{mods[c] - 1}");
                row[c++] = (int)iv;
            }
            map[index] = row;   // a repeated index keeps the last value, as a Python dict does
        }
        return map;
    }

    private static IEnumerable<(int Index, JsonElement Value)> Entries(JsonElement table)
    {
        if (table.ValueKind == JsonValueKind.Array)
        {
            int n = table.GetArrayLength();
            if (n != 256) throw new InvalidDataException($"expected 256 entries, got {n}");
            int i = 0;
            foreach (var v in table.EnumerateArray()) yield return (i++, v);
        }
        else if (table.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in table.EnumerateObject())
            {
                long i = ParseInteger(p.Name) ?? throw new InvalidDataException($"invalid history entry index {p.Name}");
                if (i is < 0 or > 255) throw new InvalidDataException($"history entry index {i} outside 0..255");
                yield return ((int)i, p.Value);
            }
        }
        else throw new InvalidDataException("history table must be a 256-element list or index-keyed dict");
    }

    // Python's int(): integers, floats truncated toward zero, booleans, and integer strings.
    private static long ToInteger(JsonElement v, string where)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                if (v.TryGetInt64(out long l)) return l;
                double d = v.GetDouble();
                if (double.IsFinite(d) && Math.Abs(d) < 9.2e18) return (long)Math.Truncate(d);
                return d < 0 ? long.MinValue : long.MaxValue;   // out of every range below
            case JsonValueKind.True: return 1;
            case JsonValueKind.False: return 0;
            case JsonValueKind.String:
                return ParseInteger(v.GetString()!) ?? throw new InvalidDataException($"{where} value {v.GetRawText()} is not an integer");
            default:
                throw new InvalidDataException($"{where} value {v.GetRawText()} is not an integer");
        }
    }

    // An optionally signed run of ASCII digits; a value too large for a long saturates, which every range rejects.
    private static long? ParseInteger(string text)
    {
        string s = text.Trim();
        string digits = s.Length > 0 && s[0] is '+' or '-' ? s[1..] : s;
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) return null;
        if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v)) return v;
        return s[0] == '-' ? long.MinValue : long.MaxValue;
    }

    // Python's bool(): null, false, 0, "", [] and {} are false.
    private static bool IsTruthy(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined => false,
        JsonValueKind.True => true,
        JsonValueKind.Number => v.GetDouble() != 0,
        JsonValueKind.String => v.GetString()!.Length != 0,
        JsonValueKind.Array => v.GetArrayLength() != 0,
        JsonValueKind.Object => v.EnumerateObject().Any(),
        _ => true
    };

    private static T[] Merge<T>(int codec, Dictionary<int, Section> parsed, T[] builtin, Func<T, BcpackHistoryStatus> status, Func<int[], BcpackHistoryStatus, T> make)
    {
        if (!parsed.TryGetValue(codec, out var s)) return builtin;
        T[] table = s.Replace ? new T[256] : (T[])builtin.Clone();
        foreach (var (i, v) in s.Confirmed) table[i] = make(v, BcpackHistoryStatus.Confirmed);
        foreach (var (i, v) in s.Provisional)
            if (status(table[i]) != BcpackHistoryStatus.Confirmed)   // replaced tables start all Unknown
                table[i] = make(v, BcpackHistoryStatus.Provisional);
        return table;
    }
}
