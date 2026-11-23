using RpfPreview.Core;
using System.IO;
using System.Windows;

namespace RpfPreview.App;

public partial class MainWindow
{
    private CharacterBrowserWindow CreateCharacterBrowserWindow()
    {
        var models = (_assetIndex?.Assets ?? []).Where(a => a.Kind == "model" && a.Available).ToArray();
        var lookup = BuildWardrobeLookup(models);
        var names = lookup.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value.Select(a =>
            RageAssetKeys.TryGetNativeNameFromRecoveredEntry(a.Entry.Path, out string name) ? name : "").Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToArray());
        return new CharacterBrowserWindow(LoadCharacterCataloguesAsync, names,
            (outfit, token) => LoadCharacterPortraitAsync(outfit, lookup, token),
            async (catalog, kit, allowStatic, attachFace, progress, token) =>
            {
                await AssembleWardrobeKitAsync(catalog, kit, lookup, allowStatic, progress, token, attachFace);
                return _modelAssemblyActive && _decodedModel?.Name == $"wardrobe_{kit:X8}";
            }, _characterRigidFacialAttachment) { Owner = this };
    }

    private void OpenWardrobe_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_wardrobeWindow is { } existing) { existing.Activate(); return; }
        var window = CreateCharacterBrowserWindow(); _wardrobeWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_wardrobeWindow, window)) _wardrobeWindow = null; };
        window.ShowDialog();
    }

    private async Task<IReadOnlyList<CharacterCatalogSource>> LoadCharacterCataloguesAsync(CancellationToken token)
    {
        var documents = _documents.Values.ToArray();
        return await Task.Run(() =>
        {
            var choices = new List<CharacterCatalogSource>(); int examined = 0;
            foreach (var document in documents)
            foreach (var entry in document.Entries.Where(e => e.CanExtract && (e.Extension is ".psin" or ".pso" || e.Path.Contains("metadata_PSIN", StringComparison.OrdinalIgnoreCase))))
            {
                token.ThrowIfCancellationRequested(); if (++examined > 4096) return choices;
                try
                {
                    byte[] prefix = document.ReadPreview(entry, 8192, token);
                    if (prefix.AsSpan().IndexOf(new byte[] { 0xf5, 0x4c, 0xcb, 0x8d }) < 0) continue;
                    var catalog = RageWardrobeCatalog.Decode(ArchiveReader.ReadRecoveredPayload(document, entry, token));
                    if (catalog != null) choices.Add(new(Path.GetFileName(document.SourcePath) + " · " + entry.Offset.ToString("X"), catalog));
                }
                catch (Exception ex) when (IsTextureReadFailure(ex)) { }
            }
            return choices;
        }, token);
    }
}
