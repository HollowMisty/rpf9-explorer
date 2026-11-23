using RpfPreview.Core;
using System.IO;
using System.Windows.Media;

namespace RpfPreview.App;

public partial class MainWindow
{
    private readonly Dictionary<int, Color> _modelNativeSlodColors = new();
    private Dictionary<ulong, RageAppearanceAsset> _appearanceAssets = new();
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
            long bytesRead = 0; int examined = 0;
            foreach (var document in documents)
            foreach (var entry in document.Entries.Where(e => e.CanExtract && (e.Extension is ".psin" or ".pso" || e.Path.Contains("metadata_PSIN", StringComparison.OrdinalIgnoreCase))))
            {
                token.ThrowIfCancellationRequested();
                if (++examined > 4096 || bytesRead > 256L * 1024 * 1024) return (Assets: assets, Complete: false);
                try
                {
                    byte[] prefix = document.ReadPreview(entry, 8192, token); bytesRead += prefix.Length;
                    if (prefix.AsSpan().IndexOf(new byte[] { 0x7e, 0xe4, 0xde, 0x1e }) < 0 || entry.Size > RageAppearanceCatalog.MaxBytes) continue;
                    byte[] payload = ArchiveReader.ReadRecoveredPayload(document, entry, token); bytesRead += payload.Length;
                    if (bytesRead > 256L * 1024 * 1024) return (Assets: assets, Complete: false);
                    var catalog = RageAppearanceCatalog.Decode(payload);
                    if (catalog == null) continue;
                    foreach (var asset in catalog.Albedos)
                    {
                        if (conflicts.Contains(asset.Key)) continue;
                        if (assets.TryGetValue(asset.Key, out var previous) && previous != asset)
                        { assets.Remove(asset.Key); conflicts.Add(asset.Key); continue; }
                        if (assets.Count >= 250000) return (Assets: assets, Complete: false);
                        assets[asset.Key] = asset;
                    }
                }
                catch (Exception ex) when (IsTextureReadFailure(ex)) { }
            }
            return (Assets: assets, Complete: true);
        }, token);
        token.ThrowIfCancellationRequested();
        _appearanceAssets = found.Assets;
        _appearanceSourceStamp = found.Complete && SourceStamp() == stamp ? stamp : null;
    }

    private Color? WardrobeSlodColor(ulong? key, int material, IReadOnlyList<int> colorMaterials)
    {
        if (key is not > 0 || colorMaterials.Count != 1 || colorMaterials[0] != material ||
            !_appearanceAssets.TryGetValue(key.Value, out var asset) || !asset.CanPreviewSlodColor) return null;
        uint rgba = asset.SlodRgba;
        return Color.FromArgb((byte)(rgba >> 24), (byte)rgba, (byte)(rgba >> 8), (byte)(rgba >> 16));
    }
}
