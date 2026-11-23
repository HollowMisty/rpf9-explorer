using System.Globalization;
using System.Text;

namespace RpfPreview.Core;

public enum RageCharacterHeadStatus { Single, NoSeparateHead, UnresolvedHead, Ambiguous }
public sealed record RageCharacterHeadCandidate(ulong DrawableKey, ulong AlbedoKey, string NativeName);
public sealed record RageCharacterOutfit(uint KitId, string Name, int AvailableModels, int TotalModels,
    ulong HeadDrawableKey, ulong HeadAlbedoKey, string HeadName, RageCharacterHeadStatus HeadStatus,
    IReadOnlyList<RageCharacterHeadCandidate> HeadCandidates, IReadOnlyList<string> ComponentNames,
    string SearchText, IReadOnlyList<string> Warnings)
{
    public string Label => Name;
    public bool ModelSetComplete => TotalModels > 0 && AvailableModels == TotalModels && Warnings.Count == 0;
}
public sealed record RageCharacterChoice(string Key, string Name, string NativeHeadName, ulong HeadKey,
    ulong AlbedoKey, IReadOnlyList<RageCharacterOutfit> Outfits, string SearchText, int CompleteOutfitCount);

public sealed class RageCharacterCatalog
{
    private const int MaxOutfits = 65536, MaxSelections = 1000000, MaxIndexedKeys = 1000000;
    public IReadOnlyList<RageCharacterChoice> Characters { get; }
    public IReadOnlyList<RageCharacterOutfit> AdvancedOutfits { get; }

    private RageCharacterCatalog(List<RageCharacterChoice> characters, List<RageCharacterOutfit> advanced)
    { Characters = characters.AsReadOnly(); AdvancedOutfits = advanced.AsReadOnly(); }

    public static RageCharacterCatalog Build(RageWardrobeCatalog wardrobe,
        IReadOnlyDictionary<ulong, IReadOnlyList<string>> namesByDrawableKey)
    {
        ArgumentNullException.ThrowIfNull(wardrobe); ArgumentNullException.ThrowIfNull(namesByDrawableKey);
        if (wardrobe.Kits.Count > MaxOutfits || namesByDrawableKey.Count > MaxIndexedKeys)
            throw new InvalidDataException("The character catalogue exceeds the supported index size.");
        var names = new Dictionary<ulong, string[]>();
        string[] NamesFor(ulong key)
        {
            if (names.TryGetValue(key, out var found)) return found;
            var unique = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (namesByDrawableKey.TryGetValue(key, out var indexed) && indexed != null)
            {
                if (indexed.Count > 4096) throw new InvalidDataException("A drawable has too many indexed native names.");
                foreach (string name in indexed)
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    try
                    {
                        if (RageAssetKeys.DrawableKey(name) != key) continue;
                        string stem = NativeStem(name);
                        if (!unique.ContainsKey(stem)) unique.Add(stem, name);
                    }
                    catch (ArgumentException) { }
                }
            }
            return names[key] = unique.Values.ToArray();
        }
        var outfits = new List<RageCharacterOutfit>(); int selectedCount = 0;
        foreach (var kit in wardrobe.Kits.OrderBy(k => k.Id))
        {
            var resolved = wardrobe.Resolve(kit.Id);
            if (resolved.Selections.Count > MaxSelections - selectedCount)
                throw new InvalidDataException("The character catalogue exceeds the supported selection budget.");
            selectedCount += resolved.Selections.Count;
            var components = resolved.Selections.Where(s => s.DrawableKey != 0).GroupBy(s => s.DrawableKey).ToArray();
            var warnings = resolved.Warnings.Take(128).ToList();
            foreach (var component in components)
                if (component.Select(s => (s.AlbedoKey, s.NormalKey, s.ExtraKey)).Distinct().Skip(1).Any() && warnings.Count < 128)
                    warnings.Add($"Drawable {component.Key:X16} has conflicting selected appearance states.");
            int available = components.Count(g => NamesFor(g.Key).Length > 0);
            var componentNames = components.SelectMany(g => NamesFor(g.Key)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var heads = components.SelectMany(g => NamesFor(g.Key).Where(IsHeadName)
                .SelectMany(name => g.Select(s => new RageCharacterHeadCandidate(g.Key, s.AlbedoKey, name))))
                .Distinct().OrderBy(h => h.DrawableKey).ThenBy(h => h.AlbedoKey).ThenBy(h => h.NativeName, StringComparer.Ordinal).ToArray();
            var status = heads.Length == 1 ? RageCharacterHeadStatus.Single : heads.Length > 1 ? RageCharacterHeadStatus.Ambiguous
                : available < components.Length ? RageCharacterHeadStatus.UnresolvedHead : RageCharacterHeadStatus.NoSeparateHead;
            var head = heads.Length == 1 ? heads[0] : null;
            string label = OutfitLabel(componentNames, kit.Id);
            string search = NormalizeSearchText($"{label} {kit.Id:X8} 0x{kit.Id:X8} " + string.Join(' ', componentNames) + " " +
                string.Join(' ', components.Select(g => g.Key.ToString("X16"))) + " " + string.Join(' ', heads.Select(h => h.AlbedoKey.ToString("X16"))));
            outfits.Add(new(kit.Id, label, available, components.Length, head?.DrawableKey ?? 0, head?.AlbedoKey ?? 0,
                head?.NativeName ?? "", status, Array.AsReadOnly(heads), Array.AsReadOnly(componentNames), search, warnings.AsReadOnly()));
        }
        var groups = outfits.Where(o => o.HeadStatus == RageCharacterHeadStatus.Single)
            .GroupBy(o => (o.HeadDrawableKey, o.HeadAlbedoKey)).ToArray();
        var appearances = groups.GroupBy(g => g.Key.HeadDrawableKey).ToDictionary(g => g.Key,
            g => g.Select(v => v.Key.HeadAlbedoKey).Distinct().Order().ToArray());
        var characters = new List<RageCharacterChoice>();
        foreach (var group in groups)
        {
            var variants = group.OrderByDescending(o => o.ModelSetComplete).ThenByDescending(o => o.AvailableModels)
                .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ThenBy(o => o.KitId).ToArray();
            string nativeName = variants.Select(o => o.HeadName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).First();
            string label = HeadLabel(nativeName); var ids = appearances[group.Key.HeadDrawableKey];
            if (ids.Length > 1 || IsSharedBaseHead(nativeName))
                label += $" · appearance {Array.IndexOf(ids, group.Key.HeadAlbedoKey) + 1:D2}";
            string key = $"{group.Key.HeadDrawableKey:X16}:{group.Key.HeadAlbedoKey:X16}";
            string search = NormalizeSearchText($"{label} {nativeName} {key} " + string.Join(' ', variants.Select(o => o.SearchText)));
            characters.Add(new(key, label, nativeName, group.Key.HeadDrawableKey, group.Key.HeadAlbedoKey,
                Array.AsReadOnly(variants), search, variants.Count(o => o.ModelSetComplete)));
        }
        characters = characters.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.HeadKey).ThenBy(c => c.AlbedoKey).ToList();
        var advanced = outfits.Where(o => o.HeadStatus != RageCharacterHeadStatus.Single || !o.ModelSetComplete)
            .OrderBy(o => o.HeadStatus).ThenByDescending(o => o.AvailableModels).ThenBy(o => o.KitId).ToList();
        return new(characters, advanced);
    }

    public static string NormalizeSearchText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (text.Length > 4 * 1024 * 1024) throw new ArgumentException("Search text exceeds the supported size.", nameof(text));
        var output = new StringBuilder(text.Length); bool separator = true;
        foreach (char character in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character)) { output.Append(char.ToLowerInvariant(character)); separator = false; }
            else if (!separator) { output.Append(' '); separator = true; }
        }
        if (separator && output.Length > 0) output.Length--;
        return output.ToString();
    }

    public static bool MatchesSearch(string searchText, string? query)
    {
        ArgumentNullException.ThrowIfNull(searchText);
        string[] terms = NormalizeSearchText(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return terms.All(term => searchText.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string NativeStem(string name)
    {
        foreach (string suffix in new[] { ".#dr", "._dr", ".#ft", "._ft" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return name[..^suffix.Length];
        return name;
    }
    private static bool IsHeadName(string name)
    {
        string[] tokens = NativeStem(name).Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Any(t => t.Equals("eye", StringComparison.OrdinalIgnoreCase) || t.Equals("eyes", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("eyecap", StringComparison.OrdinalIgnoreCase) || t.StartsWith("teeth", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("lashes", StringComparison.OrdinalIgnoreCase) || t.StartsWith("brows", StringComparison.OrdinalIgnoreCase))) return false;
        return tokens.Any(t => t.Equals("head", StringComparison.OrdinalIgnoreCase) || t.Equals("headcs", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("legacyheadcs", StringComparison.OrdinalIgnoreCase) || t.StartsWith("mannequinhead", StringComparison.OrdinalIgnoreCase));
    }
    private static bool IsSharedBaseHead(string name) => NativeStem(name).StartsWith("head_fr1_base_", StringComparison.OrdinalIgnoreCase) || NativeStem(name).StartsWith("head_mr1_base_", StringComparison.OrdinalIgnoreCase);
    private static string HeadLabel(string name)
    {
        string stem = NativeStem(name); string[] tokens = stem.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (IsSharedBaseHead(name))
        {
            string label = tokens[1].Equals("fr1", StringComparison.OrdinalIgnoreCase) ? "Female base" : "Male base";
            return tokens.Length > 3 && tokens[3] != "000" ? label + " " + tokens[3] : label;
        }
        if (tokens[0].StartsWith("mannequinhead", StringComparison.OrdinalIgnoreCase))
        {
            string gender = tokens.Contains("fr1", StringComparer.OrdinalIgnoreCase) ? "Female " : tokens.Contains("mr1", StringComparer.OrdinalIgnoreCase) ? "Male " : "";
            string shape = tokens[0]["mannequinhead".Length..];
            return gender + "mannequin" + (shape.Length > 0 ? " · " + shape : "");
        }
        int legacy = Array.FindIndex(tokens, t => t.Equals("legacyheadcs", StringComparison.OrdinalIgnoreCase));
        if (legacy > 1 && tokens[0].Equals("cs", StringComparison.OrdinalIgnoreCase)) return Words(tokens[1..legacy]) + " · legacy head";
        return Words(tokens.Where(t => !t.Equals("full", StringComparison.OrdinalIgnoreCase)).ToArray());
    }
    private static string Words(IEnumerable<string> tokens) => string.Join(' ', tokens.Select(token => token.Length == 0 ? token : char.ToUpperInvariant(token[0]) + token[1..]));
    private static readonly (string Prefix, string Label, int Rank)[] Clothing =
    [
        ("tanktopcropped", "Cropped tank top", 0), ("tshirtcrop", "Cropped T-shirt", 0), ("dressjumpsuit", "Jumpsuit", 0),
        ("sweatshirtcrewneck", "Crewneck sweatshirt", 0), ("jacketbiker", "Biker jacket", 0), ("hoodiezip", "Zip hoodie", 0),
        ("dressmini", "Mini dress", 0), ("shirtdress", "Shirt dress", 0), ("cape", "Cape", 0), ("tanktop", "Tank top", 0), ("tshirt", "T-shirt", 0), ("sweatshirt", "Sweatshirt", 0),
        ("jacket", "Jacket", 0), ("hoodie", "Hoodie", 0), ("dress", "Dress", 0), ("shirt", "Shirt", 0), ("coat", "Coat", 0), ("gilet", "Vest", 0), ("vest", "Vest", 0),
        ("pantsjeans", "Jeans", 1), ("shortsjeans", "Denim shorts", 1), ("shortscargo", "Cargo shorts", 1),
        ("pants", "Pants", 1), ("shorts", "Shorts", 1), ("skirt", "Skirt", 1), ("trousers", "Trousers", 1),
        ("shoessneakers", "Sneakers", 2), ("shoessandals", "Sandals", 2), ("bootsankle", "Ankle boots", 2),
        ("boots", "Boots", 2), ("shoes", "Shoes", 2), ("heels", "Heels", 2),
        ("bodysuit", "Bodysuit", 3), ("swimsuit", "Swimsuit", 3), ("undies", "Underwear", 3), ("bra", "Bra", 3),
        ("helmet", "Helmet", 4), ("hat", "Hat", 4), ("cap", "Cap", 4)
    ];
    private static string OutfitLabel(IEnumerable<string> names, uint id)
    {
        var labels = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string name in names)
            foreach (string token in NativeStem(name).Split('_', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith("bracelet", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var clothing in Clothing)
                    if (token.StartsWith(clothing.Prefix, StringComparison.OrdinalIgnoreCase)) { labels.TryAdd(clothing.Label, clothing.Rank); break; }
            }
        string label = labels.Count == 0 ? "Base components" : string.Join(" · ", labels.OrderBy(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Take(4).Select(p => p.Key));
        if (labels.Count > 4) label += $" +{labels.Count - 4}";
        return $"{label} · {id:X8}";
    }
}
