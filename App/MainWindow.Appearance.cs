using RpfPreview.Core;
using System.IO;
using System.Windows.Media;

namespace RpfPreview.App;

public partial class MainWindow
{
    private readonly Dictionary<int, Color> _modelNativeSlodColors = new();
    private Dictionary<ulong, RageAppearanceAsset> _appearanceAssets = new();
    private IReadOnlyList<RageTintPaletteCatalog> _appearanceTintCatalogs = [];
    private string? _appearanceSourceStamp;

    private async Task LoadWardrobeAppearanceAssetsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var documents = _documents.Values.OrderBy(d => d.SourcePath, StringComparer.OrdinalIgnoreCase).ToArray();
        string SourceStamp() => string.Join('\n', documents.Select(d => $"{d.SourcePath}:{new FileInfo(d.SourcePath).Length}:{File.GetLastWriteTimeUtc(d.SourcePath).Ticks}:{d.Entries.Count}"));
        string stamp = SourceStamp();
        if (_appearanceSourceStamp == stamp) return;
        var found = await Task.Run(() =>
        {
            var assets = new Dictionary<ulong, RageAppearanceAsset>(); var conflicts = new HashSet<ulong>();
            var palettes = new List<RageTintPaletteCatalog>();
            long bytesRead = 0; int examined = 0;
            foreach (var document in documents)
            foreach (var entry in document.Entries.Where(e => e.CanExtract && (e.Extension is ".psin" or ".pso" || e.Path.Contains("metadata_PSIN", StringComparison.OrdinalIgnoreCase))))
            {
                token.ThrowIfCancellationRequested();
                if (++examined > 4096 || bytesRead > 256L * 1024 * 1024) return (Assets: assets, Palettes: palettes, Complete: false);
                try
                {
                    byte[] prefix = document.ReadPreview(entry, 8192, token); bytesRead += prefix.Length;
                    bool appearance = prefix.AsSpan().IndexOf(new byte[] { 0x7e, 0xe4, 0xde, 0x1e }) >= 0;
                    bool tintPalette = prefix.AsSpan().IndexOf(new byte[] { 0xfb, 0xfd, 0x3e, 0x24 }) >= 0;
                    if ((!appearance && !tintPalette) || entry.Size > RageAppearanceCatalog.MaxBytes) continue;
                    byte[] payload = ArchiveReader.ReadRecoveredPayload(document, entry, token); bytesRead += payload.Length;
                    if (bytesRead > 256L * 1024 * 1024) return (Assets: assets, Palettes: palettes, Complete: false);
                    if (tintPalette && palettes.Count < 64 && RageTintPaletteCatalog.Decode(payload) is { } nativePalette) palettes.Add(nativePalette);
                    var catalog = appearance ? RageAppearanceCatalog.Decode(payload) : null;
                    if (catalog == null) continue;
                    foreach (var asset in catalog.Albedos)
                    {
                        if (conflicts.Contains(asset.Key)) continue;
                        if (assets.TryGetValue(asset.Key, out var previous) && previous != asset)
                        { assets.Remove(asset.Key); conflicts.Add(asset.Key); continue; }
                        if (assets.Count >= 250000) return (Assets: assets, Palettes: palettes, Complete: false);
                        assets[asset.Key] = asset;
                    }
                }
                catch (Exception ex) when (IsTextureReadFailure(ex)) { }
            }
            return (Assets: assets, Palettes: palettes, Complete: true);
        }, token);
        token.ThrowIfCancellationRequested();
        _appearanceAssets = found.Assets;
        _appearanceTintCatalogs = found.Palettes.AsReadOnly();
        _appearanceSourceStamp = found.Complete && SourceStamp() == stamp ? stamp : null;
    }

    private Color? WardrobeSlodColor(ulong? key, int material, IReadOnlyList<int> colorMaterials)
    {
        if (key is not > 0 || colorMaterials.Count != 1 || colorMaterials[0] != material ||
            !_appearanceAssets.TryGetValue(key.Value, out var asset) || !asset.CanPreviewSlodColor) return null;
        uint rgba = asset.SlodRgba;
        return Color.FromArgb((byte)(rgba >> 24), (byte)rgba, (byte)(rgba >> 8), (byte)(rgba >> 16));
    }

    private Color? ResolvedWardrobeSlodColor(ArchiveDocument document, ArchiveEntry entry, ulong? key, int material, IReadOnlyList<int> colorMaterials)
    {
        if (WardrobeSlodColor(key, material, colorMaterials) is Color direct) return direct;
        if (key is not > 0 || colorMaterials.Count != 1 || colorMaterials[0] != material ||
            !_appearanceAssets.TryGetValue(key.Value, out var asset) || !asset.CanPreviewPaletteSlodColor ||
            !_wardrobeTints.TryGetValue((document.SourcePath, entry.Path, entry.Offset, entry.ResourceDrawableOffset), out var tint)) return null;
        uint[] matches = _appearanceTintCatalogs.Select(c => c.FindSlodColor(tint.Palette, tint.Index)).Where(c => c.HasValue)
            .Select(c => c!.Value).Distinct().Take(2).ToArray();
        if (matches.Length != 1 || (matches[0] >> 24) != 255) return null;
        uint rgba = matches[0]; return Color.FromArgb((byte)(rgba >> 24), (byte)rgba, (byte)(rgba >> 8), (byte)(rgba >> 16));
    }
}
