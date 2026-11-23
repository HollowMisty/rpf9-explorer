using RpfPreview.Core;
using System.IO;
using System.Windows.Media;

namespace RpfPreview.App;

public partial class MainWindow
{
    private readonly SemaphoreSlim _characterPortraitGate = new(1, 1);
    private readonly Dictionary<string, CharacterPortrait> _characterPortraitCache = new();
    private StreamingTextureCatalog? _characterPortraitPools;
    private string? _characterPortraitPoolRoot;
    private DateTime _characterPortraitPoolTime;

    private async Task<CharacterPortrait?> LoadCharacterPortraitAsync(RageCharacterOutfit outfit,
        IReadOnlyDictionary<ulong, List<WorkspaceAsset>> lookup, CancellationToken token)
    {
        if (outfit.HeadStatus != RageCharacterHeadStatus.Single || !lookup.TryGetValue(outfit.HeadDrawableKey, out var candidates)) return null;
        await _characterPortraitGate.WaitAsync(token);
        try
        {
            string stamp = string.Join('|', candidates.Select(a => $"{a.SourcePath}:{File.GetLastWriteTimeUtc(a.SourcePath).Ticks}"));
            string key = $"{outfit.HeadDrawableKey:X16}:{outfit.HeadAlbedoKey:X16}:{stamp}";
            if (_characterPortraitCache.TryGetValue(key, out var cached)) return cached;
            foreach (var asset in candidates.OrderBy(a => a.Entry.Size is >= 131072 and <= 4194304 ? 0 : 1).ThenBy(a => a.Entry.Size).Take(32))
            {
                token.ThrowIfCancellationRequested(); if (asset.Entry.Size > 64L * 1024 * 1024) continue;
                try
                {
                    var resource = await Task.Run(() =>
                    {
                        byte[] bytes = ArchiveReader.ReadResourceData(asset.Document, asset.Entry, token);
                        int split = asset.Entry.ResourceSystemBytes ?? checked((int)asset.Entry.Segments![0].Size);
                        if (split <= 0 || split >= bytes.Length) throw new InvalidDataException("Invalid face resource boundary.");
                        RageModel? model = null;
                        foreach (int lod in new[] { 0, 1, 2, 3 })
                        {
                            model = RageModelDecoder.Decode(bytes.AsSpan(0, split), bytes.AsSpan(split), asset.Entry.ResourceDrawableOffset, lod);
                            if (model != null && model.Meshes.Sum(m => m.Indices.Count / 3) >= 64) break;
                        }
                        if (model == null || RageAssetKeys.DrawableKey(model.Name) != outfit.HeadDrawableKey) throw new InvalidDataException("The indexed head does not match its native model name.");
                        return (Bytes: bytes, Split: split, Model: model);
                    }, token);
                    var model = resource.Model;
                    var used = model.Meshes.Where(m => m.ShaderIndex.HasValue).Select(m => m.ShaderIndex!.Value).Distinct().ToArray();
                    var targets = (model.Materials ?? []).Where(m => used.Contains(m.ShaderIndex) && ModelTextureBindings.ColorTextures(m).Count > 0).Select(m => m.ShaderIndex).ToArray();
                    if (targets.Length == 0 && used.Length == 1) targets = used;
                    var images = new Dictionary<int, ImageSource>(); var colors = new Dictionary<int, Color>();
                    foreach (var material in (model.Materials ?? []).Where(m => used.Contains(m.ShaderIndex)))
                    {
                        var keys = ModelTextureBindings.ColorTextures(material).Select(c => c.Reference.OpaqueId64).ToList();
                        if (targets.Length == 1 && targets[0] == material.ShaderIndex && outfit.HeadAlbedoKey != 0) keys.Insert(0, outfit.HeadAlbedoKey);
                        foreach (ulong imageKey in keys.Distinct())
                        {
                            var image = await LoadPortraitImageAsync(asset, model, resource.Bytes, resource.Split, imageKey, token);
                            if (image == null) continue;
                            images[material.ShaderIndex] = IsOpaqueMaterial(material) ? OpaqueModelImage(image) : image; break;
                        }
                        if (!images.ContainsKey(material.ShaderIndex) && targets.Length == 1 && targets[0] == material.ShaderIndex)
                        {
                            await LoadWardrobeAppearanceAssetsAsync(token);
                            if (_appearanceAssets.TryGetValue(outfit.HeadAlbedoKey, out var appearance) && appearance.CanPreviewSlodColor)
                            {
                                uint rgba = appearance.SlodRgba;
                                colors[material.ShaderIndex] = Color.FromArgb((byte)(rgba >> 24), (byte)rgba, (byte)(rgba >> 8), (byte)(rgba >> 16));
                            }
                        }
                    }
                    var parts = model.Meshes.Select(m => new MeshPreviewPart(m.Name, m.Positions, m.Indices)
                    {
                        TexCoords = m.TexCoords, VertexColors = m.VertexColors,
                        Texture = m.ShaderIndex is int shader && images.TryGetValue(shader, out var image) ? image : null,
                        SlodColor = m.ShaderIndex is int native && colors.TryGetValue(native, out var color) ? color : null
                    }).ToArray();
                    string note = images.Count > 0 ? "Native face preview. Open an outfit to inspect its body parts and accessories."
                        : colors.Count > 0 ? "Face preview uses a native SLOD color approximation. The full face texture is unavailable."
                        : "Geometry-only face preview. The color image is not available in the loaded or sibling sources.";
                    var portrait = new CharacterPortrait(parts, note);
                    if (images.Count > 0)
                    {
                        if (_characterPortraitCache.Count >= 32) _characterPortraitCache.Clear();
                        _characterPortraitCache[key] = portrait;
                    }
                    return portrait;
                }
                catch (Exception ex) when (IsTextureReadFailure(ex)) { }
            }
            return null;
        }
        finally { _characterPortraitGate.Release(); }
    }

    private async Task<System.Windows.Media.Imaging.BitmapSource?> LoadPortraitImageAsync(WorkspaceAsset asset, RageModel model, byte[] bytes, int split, ulong key, CancellationToken token)
    {
        if (key == 0) return null;
        var embedded = model.EmbeddedTextures?.FirstOrDefault(t => t.AssetKey == key);
        if (embedded != null && model.EmbeddedTextureDictionaryOffset is int dictionary)
        {
            try
            {
                int mip = ModelMip(embedded, 384);
                var pixels = await Task.Run(() => RageTextureDecoder.DecodeEmbeddedTexturePixels(bytes.AsSpan(0, split), bytes.AsSpan(split), dictionary, embedded.Index, mip), token);
                return PixelBitmap(pixels);
            }
            catch (Exception ex) when (IsTextureReadFailure(ex)) { }
        }
        var locations = _loadedTextureLocations.TryGetValue(key, out var loaded) ? loaded.ToList() : [];
        string? root = StreamingTextureCatalog.FindContentDirectory(asset.SourcePath);
        if (root != null)
        {
            if (_characterPortraitPools == null || root != _characterPortraitPoolRoot || DateTime.UtcNow - _characterPortraitPoolTime > TimeSpan.FromSeconds(20))
            {
                _characterPortraitPools = await Task.Run(() => StreamingTextureCatalog.Build(root, token), token);
                _characterPortraitPoolRoot = root; _characterPortraitPoolTime = DateTime.UtcNow;
            }
            locations.AddRange(_characterPortraitPools.Find(key));
        }
        foreach (var location in locations.Where(l => l.Texture.CanPreview).DistinctBy(l => (l.SourcePath, l.Texture.Index)).OrderBy(l => l.Texture.BaseMipLevel))
        {
            try { return PixelBitmap(await Task.Run(() => TtpfReader.DecodeTexturePreview(location.SourcePath, location.Texture.Index, 384, token), token)); }
            catch (Exception ex) when (IsTextureReadFailure(ex)) { }
        }
        return null;
    }
}
