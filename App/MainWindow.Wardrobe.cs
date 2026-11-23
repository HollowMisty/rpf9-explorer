using Microsoft.Win32;
using RpfPreview.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace RpfPreview.App;

public partial class MainWindow
{
    private sealed record WardrobeModelOption(WorkspaceAsset Asset, RageModel Model, int Lod, int Triangles);
    private Window? _wardrobeWindow;
    private readonly Dictionary<(string Source, string Path, long Offset, int? Drawable), ulong> _wardrobeAlbedos = new();
    private readonly Dictionary<(string Source, string Path, long Offset, int? Drawable), (uint Palette, byte Index)> _wardrobeTints = new();

    private ulong? WardrobeAlbedoKey(ArchiveDocument document, ArchiveEntry entry)
        => _wardrobeAlbedos.TryGetValue((document.SourcePath, entry.Path, entry.Offset, entry.ResourceDrawableOffset), out ulong key) ? key : null;

    private static Dictionary<ulong, List<WorkspaceAsset>> BuildWardrobeLookup(IEnumerable<WorkspaceAsset> assets)
    {
        var result = new Dictionary<ulong, List<WorkspaceAsset>>();
        foreach (var asset in assets)
        {
            if (!RageAssetKeys.TryGetNativeNameFromRecoveredEntry(asset.Entry.Path, out string candidate)) continue;
            ulong key = RageAssetKeys.DrawableKey(candidate);
            if (!result.TryGetValue(key, out var matches)) result[key] = matches = [];
            matches.Add(asset);
        }
        return result;
    }

    private async Task AssembleWardrobeKitAsync(RageWardrobeCatalog catalog, uint kitId,
        Dictionary<ulong, List<WorkspaceAsset>> lookup, bool allowStatic, Action<string> progress, CancellationToken token, bool rigidFacialAttachment = false)
    {
        var resolution = catalog.Resolve(kitId);
        if (resolution.Warnings.Count > 0) throw new InvalidDataException("The authored kit has unresolved references: " + string.Join(" ", resolution.Warnings.Take(4)));
        var groups = resolution.Selections.Where(s => s.DrawableKey != 0).GroupBy(s => s.DrawableKey).ToArray();
        if (groups.Length is < 1 or > 96) throw new InvalidDataException("Choose a kit with 1 to 96 model components.");
        progress("Reading shared character skeletons…");
        var masters = new List<RageSkeleton>();
        foreach (var asset in (_assetIndex?.Assets ?? []).Where(a => a.Kind == "model" && a.Available && a.Entry.ResourceDrawableOffset == null && a.Name.Contains("body_head", StringComparison.OrdinalIgnoreCase)).Take(64))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                byte[] system = await Task.Run(() => ArchiveReader.ReadResourceSystemData(asset.Document, asset.Entry, token), token);
                int? offset = RageModelDecoder.ProbeRequiredExtents(system)?.DrawableOffset;
                if (offset is int drawable && RageSkeleton.ReadForDrawable(system, drawable) is { } rig && rig.Joints.Any(j => j.Hash == 0x6686685C && j.Parent == null)) masters.Add(rig);
            }
            catch (Exception ex) when (IsTextureReadFailure(ex)) { }
        }
        var compatible = masters.Select(_ => new WardrobeModelOption?[groups.Length]).ToArray();
        var connected = masters.Select(_ => new bool[groups.Length]).ToArray();
        var staticOptions = new WardrobeModelOption?[groups.Length]; var stateAlbedos = new ulong[groups.Length];
        var stateTints = new (uint Palette, byte Index)?[groups.Length];
        for (int component = 0; component < groups.Length; component++)
        {
            var group = groups[component]; token.ThrowIfCancellationRequested();
            var states = group.Select(s => (s.AlbedoKey, s.NormalKey, s.ExtraKey, s.PaletteHash, s.Tint0, s.Tint1, s.Tint2)).Distinct().ToArray();
            if (states.Length > 1) throw new InvalidDataException($"Kit {kitId:X8} selects conflicting appearance states for drawable {group.Key:X16}.");
            stateAlbedos[component] = states[0].AlbedoKey;
            if (states[0].PaletteHash != 0 && states[0].Tint0 == states[0].Tint1 && states[0].Tint0 == states[0].Tint2)
                stateTints[component] = (states[0].PaletteHash, states[0].Tint0);
            progress($"Validating component {component + 1} / {groups.Length}, native LODs 0 to 3…");
            if (!lookup.TryGetValue(group.Key, out var candidates)) throw new InvalidDataException($"Drawable {group.Key:X16} is not indexed in the loaded model sources.");
            if (candidates.Count > 128) throw new InvalidDataException($"Drawable {group.Key:X16} exceeds the supported physical variant limit.");
            foreach (var candidate in candidates.Take(128))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var models = await Task.Run(() =>
                    {
                        byte[] bytes = ArchiveReader.ReadResourceData(candidate.Document, candidate.Entry, token);
                        int split = candidate.Entry.ResourceSystemBytes ?? checked((int)candidate.Entry.Segments![0].Size);
                        var decoded = new List<WardrobeModelOption>();
                        if (split <= 0 || split >= bytes.Length) return decoded;
                        for (int lod = 0; lod < 4; lod++)
                        {
                            token.ThrowIfCancellationRequested();
                            var model = RageModelDecoder.Decode(bytes.AsSpan(0, split), bytes.AsSpan(split), candidate.Entry.ResourceDrawableOffset, lod);
                            if (model != null && RageAssetKeys.DrawableKey(model.Name) == group.Key)
                                decoded.Add(new(candidate, model, lod, model.Meshes.Sum(m => m.Indices.Count / 3)));
                        }
                        return decoded;
                    }, token);
                    foreach (var option in models)
                    {
                        if (staticOptions[component] is not { } previous || option.Triangles > previous.Triangles) staticOptions[component] = option;
                        for (int masterIndex = 0; masterIndex < masters.Count; masterIndex++)
                        {
                            if (connected[masterIndex][component] && compatible[masterIndex][component] is { } highest && highest.Triangles >= option.Triangles) continue;
                            var merged = RageSkeleton.Combine(masters[masterIndex], option.Model.Skeleton is { } own ? [own] : []);
                            if (merged == null) continue;
                            var coverage = RageSkinning.GetCompatibility(merged, option.Model.Meshes);
                            if (!coverage.Complete && !(coverage.RequiredBones == 0 && !option.Model.HasBoneBindings)) continue;
                            bool followsBody = RageSkinning.GetDrivenCompatibility(merged, option.Model.Meshes, CharacterPreviewDriverAnchors(merged, rigidFacialAttachment)).Complete;
                            if (compatible[masterIndex][component] is { } current &&
                                (connected[masterIndex][component] && !followsBody || connected[masterIndex][component] == followsBody && current.Triangles >= option.Triangles)) continue;
                            compatible[masterIndex][component] = option; connected[masterIndex][component] = followsBody;
                        }
                    }
                }
                catch (Exception ex) when (IsTextureReadFailure(ex)) { }
            }
            if (staticOptions[component] == null) throw new InvalidDataException($"Drawable {group.Key:X16} has no readable validated model yet. Its RPF download may be incomplete.");
            var retained = staticOptions.Concat(compatible.SelectMany(options => options)).OfType<WardrobeModelOption>().Distinct().ToArray();
            if (retained.Sum(o => (long)o.Model.Meshes.Sum(m => m.Positions.Count)) > 8_000_000 || retained.Sum(o => (long)o.Model.Meshes.Sum(m => m.Indices.Count)) > 24_000_000)
                throw new InvalidDataException("The wardrobe variants exceed the supported validation memory budget.");
        }
        progress("Checking the complete outfit against its shared animation skeleton…");
        RageSkeleton? shared = null; WardrobeModelOption[]? selected = null; long bestTriangles = -1; int bestConnected = -1;
        RageSkinCompatibility? drivenCoverage = null;
        for (int masterIndex = 0; masterIndex < masters.Count; masterIndex++)
        {
            token.ThrowIfCancellationRequested();
            if (compatible[masterIndex].Any(option => option == null)) continue;
            var options = compatible[masterIndex].OfType<WardrobeModelOption>().ToArray();
            var merged = RageSkeleton.Combine(masters[masterIndex], options.Select(o => o.Model.Skeleton).OfType<RageSkeleton>());
            long triangles = options.Sum(option => (long)option.Triangles);
            int connectedParts = connected[masterIndex].Count(value => value);
            if (merged == null || connectedParts < bestConnected || connectedParts == bestConnected && triangles <= bestTriangles || !RageSkinning.GetCompatibility(merged, options.SelectMany(o => o.Model.Meshes)).Complete) continue;
            selected = options; shared = merged; bestTriangles = triangles; bestConnected = connectedParts;
            drivenCoverage = RageSkinning.GetDrivenCompatibility(merged, options.SelectMany(o => o.Model.Meshes), CharacterPreviewDriverAnchors(merged, rigidFacialAttachment));
        }
        bool animationReady = shared != null && bestConnected == groups.Length && drivenCoverage?.Complete == true;
        bool rigidPreview = animationReady && rigidFacialAttachment && CharacterPreviewDriverAnchors(shared!, true).Contains(0x54532D85u);
        string driverStatus = drivenCoverage != null ? $"{drivenCoverage.MatchedBones} / {drivenCoverage.RequiredBones} weighted bone IDs supported by " + (rigidPreview ? "native body ancestry and a rigid facial attachment preview" : "native humanoid ancestry") + $"; {Math.Max(0, bestConnected)} / {groups.Length} components supported." : "Humanoid bone ancestry could not be validated.";
        if (!animationReady && !allowStatic)
        {
            var unavailable = new InvalidDataException(driverStatus + " Detached native driver bones require supported rig operators or their own animation tracks. Enable static preview to view the complete outfit without applying a partial animation.");
            unavailable.Data["WardrobeStaticAvailable"] = true; throw unavailable;
        }
        if (!animationReady) shared = null;
        selected ??= staticOptions.OfType<WardrobeModelOption>().ToArray();
        var selection = new List<(FileRow Row, ArchiveDocument Document)>(); var meshes = new List<RageMesh>();
        var headNames = new HashSet<string>(StringComparer.Ordinal); var albedos = new Dictionary<(string, string, long, int?), ulong>();
        var tints = new Dictionary<(string, string, long, int?), (uint Palette, byte Index)>(); int vertices = 0, indices = 0;
        for (int component = 0; component < selected.Length; component++)
        {
            var option = selected[component]; var best = option.Asset; var model = option.Model;
            int addedVertices = model.Meshes.Sum(m => m.Positions.Count), addedIndices = model.Meshes.Sum(m => m.Indices.Count);
            if (addedVertices > 2_000_000 - vertices || addedIndices > 6_000_000 - indices || model.Meshes.Count > 1024 - meshes.Count) throw new InvalidDataException("The wardrobe exceeds the model preview budget.");
            vertices += addedVertices; indices += addedIndices; meshes.AddRange(model.Meshes);
            if ((model.Name.StartsWith("head_", StringComparison.OrdinalIgnoreCase) || model.Name.Contains("_head_", StringComparison.OrdinalIgnoreCase)) && !model.Name.Contains("_eyes_", StringComparison.OrdinalIgnoreCase)) headNames.Add(model.Name);
            selection.Add((new FileRow(best.Entry) { Asset = best, ModelLod = option.Lod }, best.Document));
            if (stateAlbedos[component] != 0) albedos[(best.SourcePath, best.Entry.Path, best.Entry.Offset, best.Entry.ResourceDrawableOffset)] = stateAlbedos[component];
            if (stateTints[component] is { } tint) tints[(best.SourcePath, best.Entry.Path, best.Entry.Offset, best.Entry.ResourceDrawableOffset)] = tint;
        }
        progress("Reading native appearance colors…");
        await LoadWardrobeAppearanceAssetsAsync(token);
        token.ThrowIfCancellationRequested(); _wardrobeAlbedos.Clear(); foreach (var entry in albedos) _wardrobeAlbedos.Add(entry.Key, entry.Value);
        _wardrobeTints.Clear(); foreach (var entry in tints) _wardrobeTints.Add(entry.Key, entry.Value);
        try
        {
            using var registration = token.Register(() => _workCancellation?.Cancel());
            await PreviewModelAssemblySourcesAsync(selection.ToArray(), animationReady);
        }
        finally { _wardrobeAlbedos.Clear(); _wardrobeTints.Clear(); }
        token.ThrowIfCancellationRequested();
        if (_modelAssemblyActive)
        {
            if (_decodedModel?.Meshes.Count != meshes.Count || ModelPreview.PresentedVertices != vertices || ModelPreview.PresentedTriangles != indices / 3)
                throw new InvalidDataException("The complete authored kit could not be assembled. Some source components may have become unavailable.");
            if (!animationReady) ClearCharacterPlayback();
            PreviewNameText.Text = $"Authored wardrobe kit {kitId:X8}";
            PreviewNameText.ToolTip = $"Native authored kit {kitId:X8}. Every selected drawable ID is matched against its decoded resource name.";
            ModelAssemblyBanner.Text = $"Authored kit {kitId:X8} · {groups.Length} exact model IDs · " + (shared != null ? "compatible native LODs" : "static native LODs");
            if (rigidPreview) ModelAssemblyBanner.Text += " · rigid facial attachment preview";
            if (headNames.Count == 1) ModelAssemblyBanner.Text += " · " + headNames.Single();
            string presentation = shared != null
                ? (rigidPreview ? "Authored wardrobe components use native body animation and a rigid head attachment preview for detached facial parts. Native facial drivers are not decoded. " : "Authored wardrobe components use native LODs whose weighted bones all follow the shared humanoid root. ") + "Load a native clip dictionary to preview supported bone tracks. Native shader effects and unsupported animation channels are not applied."
                : "Authored wardrobe components shown in their native static pose. Some weighted bones are detached native drivers; partial animation is disabled for this preview. Native shader effects are not applied.";
            if (_decodedModel != null) { _decodedModel = _decodedModel with { Name = $"wardrobe_{kitId:X8}", PresentationNote = presentation }; _characterOriginalModel = _decodedModel; }
            PreviewFootnoteText.Text = $"{ModelPreview.PresentedTexturedMeshes:N0} textured mesh parts · {ModelPreview.PresentedSlodColoredMeshes:N0} with SLOD color approximation · {_modelMissingTextureMaterials:N0} unresolved materials · " + (shared != null ? rigidPreview ? "native body animation with rigid facial attachment preview" : "native humanoid ancestry validated; animation controls available" : "static preview selected; detached driver bones are not animated") + " · native shader effects are not applied";
            TextPreview.Text = $"Authored wardrobe kit {kitId:X8}\nThe selected components are resolved by exact 64-bit drawable identifiers and validated native model names.\n{driverStatus}\n\n" + TextPreview.Text;
            _characterRigidFacialAttachment = rigidPreview;
            if (shared != null) UseCharacterSkeleton(shared);
            else StatusText.Text = $"Wardrobe kit {kitId:X8} assembled as an explicit static preview. " + driverStatus;
            ModelPreview.FitToView();
        }
    }
}
