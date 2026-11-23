using Microsoft.Win32;
using RpfPreview.Core;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RpfPreview.App;

internal sealed record CharacterCatalogSource(string Label, RageWardrobeCatalog Catalog);
internal sealed record CharacterPortrait(IReadOnlyList<MeshPreviewPart> Parts, string Note);

public partial class CharacterBrowserWindow : Window
{
    private sealed class CharacterCard(RageCharacterChoice? character, IReadOnlyList<RageCharacterOutfit> outfits) : INotifyPropertyChanged
    {
        public RageCharacterChoice? Character { get; } = character;
        public IReadOnlyList<RageCharacterOutfit> Outfits { get; } = outfits;
        public string Key => Character?.Key ?? "kit:" + Outfits[0].KitId.ToString("X8");
        public string Name => Character?.Name ?? Outfits[0].Name;
        public string NativeName => Character?.NativeHeadName ?? "No single indexed head. Inspect the outfit details before opening.";
        public string Coverage => Outfits.Count(o => o.ModelSetComplete) is int count && count > 0 ? $"{count} outfit{(count == 1 ? "" : "s")} available" : "Incomplete model set";
        private ImageSource? _thumbnail;
        public ImageSource? Thumbnail { get => _thumbnail; set { _thumbnail = value; Changed(); } }
        private string _previewText = character == null ? "No separate face" : "Loading face…";
        public string PreviewText { get => _previewText; set { _previewText = value; Changed(); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    }
    private sealed record OutfitRow(RageCharacterOutfit Outfit)
    {
        public string Name => Outfit.Name;
        public string Coverage => Outfit.ModelSetComplete ? $"{Outfit.TotalModels} model parts indexed" : $"{Outfit.AvailableModels} of {Outfit.TotalModels} model parts indexed";
    }
    private const int PageSize = 9;
    private readonly Func<CancellationToken, Task<IReadOnlyList<CharacterCatalogSource>>> _loadSources;
    private readonly IReadOnlyDictionary<ulong, IReadOnlyList<string>> _names;
    private readonly Func<RageCharacterOutfit, CancellationToken, Task<CharacterPortrait?>> _loadPortrait;
    private readonly Func<RageWardrobeCatalog, uint, bool, bool, Action<string>, CancellationToken, Task<bool>> _openOutfit;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _selectionCancellation, _pageCancellation;
    private Task _selectionTask = Task.CompletedTask, _thumbnailTask = Task.CompletedTask;
    private CharacterCard[] _cards = [], _visible = [];
    private readonly Queue<CharacterCard> _thumbnailOrder = new();
    private CharacterCatalogSource? _source;
    private int _page, _catalogVersion;
    private bool _loaded, _opening, _filtering;

    internal CharacterBrowserWindow(Func<CancellationToken, Task<IReadOnlyList<CharacterCatalogSource>>> loadSources,
        IReadOnlyDictionary<ulong, IReadOnlyList<string>> names,
        Func<RageCharacterOutfit, CancellationToken, Task<CharacterPortrait?>> loadPortrait,
        Func<RageWardrobeCatalog, uint, bool, bool, Action<string>, CancellationToken, Task<bool>> openOutfit, bool attachFace)
    {
        _loadSources = loadSources; _names = names; _loadPortrait = loadPortrait; _openOutfit = openOutfit;
        InitializeComponent(); AttachFaceCheck.IsChecked = attachFace; FacePreview.SetPortraitMode();
        Closed += (_, _) => { _lifetime.Cancel(); _selectionCancellation?.Cancel(); _pageCancellation?.Cancel(); FacePreview.Clear(); };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return; _loaded = true; CatalogSelector.IsEnabled = BrowseCatalogButton.IsEnabled = false;
        try
        {
            var sources = await _loadSources(_lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested(); CatalogSelector.ItemsSource = sources;
            if (sources.Count > 0) CatalogSelector.SelectedIndex = 0;
            else BrowserStatus.Text = "No wardrobe catalogue is indexed. Scan the character archive, then reopen Characters, or open a catalogue file.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ReadFailure(ex)) { BrowserStatus.Text = ex.Message; }
        finally { if (!_lifetime.IsCancellationRequested) { CatalogSelector.IsEnabled = BrowseCatalogButton.IsEnabled = true; BrowserProgress.Visibility = Visibility.Collapsed; CharacterSearch.Focus(); } }
    }

    private async void Catalog_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || CatalogSelector.SelectedItem is not CharacterCatalogSource source) return;
        int version = ++_catalogVersion; _selectionCancellation?.Cancel(); _pageCancellation?.Cancel();
        BrowserProgress.Visibility = Visibility.Visible; BrowserStatus.Text = "Organizing character faces and outfits…";
        try
        {
            var index = await Task.Run(() => RageCharacterCatalog.Build(source.Catalog, _names), _lifetime.Token);
            if (_lifetime.IsCancellationRequested || version != _catalogVersion) return;
            _source = source;
            _cards = index.Characters.Select(c => new CharacterCard(c, c.Outfits))
                .Concat(index.AdvancedOutfits.Select(o => new CharacterCard(null, new[] { o }))).ToArray();
            _thumbnailOrder.Clear(); _page = 0; ApplyFilter();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ReadFailure(ex)) { BrowserStatus.Text = ex.Message; }
        finally { if (!_lifetime.IsCancellationRequested && version == _catalogVersion) BrowserProgress.Visibility = Visibility.Collapsed; }
    }

    private RageCharacterOutfit[] MatchingOutfits(CharacterCard card)
    {
        string common = card.Name + " " + card.NativeName;
        return card.Outfits.Where(o => (IncludeIncomplete.IsChecked == true || card.Character != null && o.ModelSetComplete)
            && RageCharacterCatalog.MatchesSearch(common + " " + o.SearchText, CharacterSearch.Text)).ToArray();
    }
    private void ApplyFilter()
    {
        if (!_loaded || _opening) return;
        _visible = _cards.Where(c => MatchingOutfits(c).Length > 0).ToArray();
        _page = Math.Clamp(_page, 0, Math.Max(0, (_visible.Length - 1) / PageSize)); ShowPage();
    }
    private void ShowPage()
    {
        string? previous = (CharacterList.SelectedItem as CharacterCard)?.Key;
        _pageCancellation?.Cancel(); _pageCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var page = _visible.Skip(_page * PageSize).Take(PageSize).ToArray();
        _filtering = true;
        CharacterList.ItemsSource = page;
        var preferred = page.Where(c => c.Character != null && !c.Name.Contains("mannequin", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Outfits.Count(o => o.ModelSetComplete)).FirstOrDefault();
        CharacterList.SelectedItem = page.FirstOrDefault(c => c.Key == previous) ?? preferred ?? page.FirstOrDefault();
        _filtering = false; UpdateSelectedCharacter();
        int faces = _visible.Count(c => c.Character != null), outfits = _visible.Sum(c => MatchingOutfits(c).Length);
        ResultCount.Text = $"{faces:N0} {(faces == 1 ? "face" : "faces")} · {outfits:N0} {(outfits == 1 ? "outfit" : "outfits")}";
        PageText.Text = _visible.Length == 0 ? "No matching characters" : $"Page {_page + 1} of {(_visible.Length + PageSize - 1) / PageSize}";
        PreviousPageButton.IsEnabled = _page > 0; NextPageButton.IsEnabled = (_page + 1) * PageSize < _visible.Length;
        if (page.Length == 0) { ClearSelectedFace(); BrowserStatus.Text = "No match. Try a native character name, a clothing term, or an outfit ID. Include incomplete outfits to see more results."; }
        _thumbnailTask = LoadThumbnailsAsync(page, _pageCancellation.Token);
    }
    private void Search_Changed(object sender, TextChangedEventArgs e) { _page = 0; ApplyFilter(); }
    private void Filter_Changed(object sender, RoutedEventArgs e) { _page = 0; ApplyFilter(); }
    private void ClearSearch_Click(object sender, RoutedEventArgs e) { CharacterSearch.Clear(); CharacterSearch.Focus(); }
    private void PreviousPage_Click(object sender, RoutedEventArgs e) { if (_page > 0) { _page--; ShowPage(); } }
    private void NextPage_Click(object sender, RoutedEventArgs e) { if ((_page + 1) * PageSize < _visible.Length) { _page++; ShowPage(); } }

    private void Character_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_filtering) UpdateSelectedCharacter();
    }
    private void UpdateSelectedCharacter()
    {
        if (_opening || CharacterList.SelectedItem is not CharacterCard card) return;
        CharacterName.Text = card.Name; var outfits = MatchingOutfits(card).OrderByDescending(o => o.ModelSetComplete).ThenBy(o => o.KitId).ToArray();
        _filtering = true; OutfitList.ItemsSource = outfits.Select(o => new OutfitRow(o)).ToArray(); _filtering = false;
        OutfitCount.Text = outfits.Length == 1 ? "1 choice" : $"{outfits.Length} choices";
        OutfitList.SelectedIndex = outfits.Length == 0 ? -1 : 0;
    }
    private void Outfit_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_filtering || _opening) return;
        OpenStaticButton.Visibility = Visibility.Collapsed;
        _selectionCancellation?.Cancel(); _selectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        if (OutfitList.SelectedItem is not OutfitRow row) { ClearSelectedFace(); return; }
        OpenOutfitButton.IsEnabled = row.Outfit.ModelSetComplete;
        NativeDetails.Text = $"Outfit {row.Outfit.KitId:X8}\n{row.Outfit.HeadName}\nHead {row.Outfit.HeadDrawableKey:X16} · face {row.Outfit.HeadAlbedoKey:X16}";
        BrowserStatus.Text = row.Outfit.ModelSetComplete ? "Open the selected outfit to inspect its parts together. Animation support is checked when opening." : "Some model parts are not indexed. Load or scan the relevant archives before opening this outfit.";
        _selectionTask = LoadSelectedFaceAsync(row.Outfit, _selectionCancellation.Token);
    }
    private void ClearSelectedFace()
    {
        _selectionCancellation?.Cancel(); FacePreview.Clear(); FacePlaceholder.Text = "Select a face on the left.";
        FacePlaceholder.Visibility = Visibility.Visible; CharacterName.Text = "Choose a character"; OutfitList.ItemsSource = null;
        OpenOutfitButton.IsEnabled = false; OpenStaticButton.Visibility = Visibility.Collapsed;
    }
    private async Task LoadSelectedFaceAsync(RageCharacterOutfit outfit, CancellationToken token)
    {
        FacePreview.Clear(); FacePlaceholder.Text = "Loading face…"; FacePlaceholder.Visibility = Visibility.Visible;
        try
        {
            var portrait = await _loadPortrait(outfit, token); token.ThrowIfCancellationRequested();
            if (portrait == null) { FacePlaceholder.Text = "No separate face preview is available. You can still open an available outfit."; return; }
            FacePreview.ShowMeshes(portrait.Parts); FacePlaceholder.Visibility = Visibility.Collapsed; FaceNote.Text = portrait.Note;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ReadFailure(ex)) { if (!token.IsCancellationRequested) { FacePlaceholder.Text = "Face preview unavailable"; FaceNote.Text = ex.Message; } }
    }
    private async Task LoadThumbnailsAsync(CharacterCard[] cards, CancellationToken token)
    {
        foreach (var card in cards)
        {
            if (token.IsCancellationRequested) return;
            if (card.Thumbnail != null || card.Character == null) continue;
            var outfit = MatchingOutfits(card).OrderByDescending(o => o.ModelSetComplete).FirstOrDefault(); if (outfit == null) continue;
            try
            {
                var portrait = await _loadPortrait(outfit, token); token.ThrowIfCancellationRequested();
                if (portrait == null) { card.PreviewText = "Select to inspect outfit"; continue; }
                var view = new ModelViewport(); view.SetPortraitMode(); view.ShowMeshes(portrait.Parts);
                view.Measure(new Size(192, 192)); view.Arrange(new Rect(0, 0, 192, 192)); view.UpdateLayout(); view.FitToView();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background, token);
                var image = new RenderTargetBitmap(192, 192, 96, 96, PixelFormats.Pbgra32); image.Render(view); image.Freeze();
                token.ThrowIfCancellationRequested(); card.Thumbnail = image; card.PreviewText = ""; _thumbnailOrder.Enqueue(card);
                while (_thumbnailOrder.Count > 64) { var old = _thumbnailOrder.Dequeue(); old.Thumbnail = null; old.PreviewText = "Select to preview"; }
                view.Clear();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ReadFailure(ex)) { card.PreviewText = "Select to preview"; }
        }
    }

    private async void BrowseCatalog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Open a character wardrobe catalogue", Filter = "Wardrobe metadata|*.psin;*.pso;*.0drp|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var file = new FileInfo(dialog.FileName);
            if (file.Length is < 96 or > RageWardrobeCatalog.MaxBytes) throw new InvalidDataException("The catalogue exceeds the supported size.");
            byte[] bytes = await File.ReadAllBytesAsync(file.FullName, _lifetime.Token);
            var catalog = RageWardrobeCatalog.Decode(bytes) ?? throw new InvalidDataException("No supported wardrobe catalogue was found in this file.");
            var sources = (CatalogSelector.ItemsSource as IEnumerable<CharacterCatalogSource> ?? []).Append(new(file.Name, catalog)).ToArray();
            CatalogSelector.ItemsSource = sources; CatalogSelector.SelectedIndex = sources.Length - 1;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ReadFailure(ex)) { BrowserStatus.Text = ex.Message; }
    }
    private async void OpenOutfit_Click(object sender, RoutedEventArgs e) => await OpenSelectedAsync(false);
    private async void OpenStatic_Click(object sender, RoutedEventArgs e) => await OpenSelectedAsync(true);
    private async Task OpenSelectedAsync(bool allowStatic)
    {
        if (_opening || _source == null || OutfitList.SelectedItem is not OutfitRow row || !row.Outfit.ModelSetComplete) return;
        _opening = true; _selectionCancellation?.Cancel(); _pageCancellation?.Cancel(); SetOpening(true);
        try
        {
            await Task.WhenAll(_selectionTask, _thumbnailTask);
            bool opened = await _openOutfit(_source.Catalog, row.Outfit.KitId, allowStatic, AttachFaceCheck.IsChecked == true,
                text => BrowserStatus.Text = text, _lifetime.Token);
            if (opened) Close(); else BrowserStatus.Text = "The outfit could not be opened. Check the source files and try again.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ReadFailure(ex))
        {
            BrowserStatus.Text = ex.Data["WardrobeStaticAvailable"] is true ? "This outfit needs animation drivers that are not supported. Open static preview to inspect the complete outfit without animation." : ex.Message;
            OpenStaticButton.Visibility = ex.Data["WardrobeStaticAvailable"] is true ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _opening = false; if (!_lifetime.IsCancellationRequested) SetOpening(false); }
    }
    private void SetOpening(bool opening)
    {
        CharacterSearch.IsEnabled = CharacterList.IsEnabled = OutfitList.IsEnabled = CatalogSelector.IsEnabled = BrowseCatalogButton.IsEnabled = IncludeIncomplete.IsEnabled = AttachFaceCheck.IsEnabled = !opening;
        OpenOutfitButton.IsEnabled = !opening && OutfitList.SelectedItem is OutfitRow row && row.Outfit.ModelSetComplete;
        OpenStaticButton.IsEnabled = !opening; BrowserProgress.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        PreviousPageButton.IsEnabled = !opening && _page > 0; NextPageButton.IsEnabled = !opening && (_page + 1) * PageSize < _visible.Length;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private static bool ReadFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException or InvalidOperationException;
}
