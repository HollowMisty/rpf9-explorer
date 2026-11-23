using Microsoft.Win32;
using RpfPreview.Core;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace RpfPreview.App;

public partial class MainWindow
{
    private sealed record CharacterPreviewBinding(RageMesh Mesh, RageSkeleton? Skeleton, MeshPreviewPart Preview);
    private sealed record CharacterResolvedBinding(CharacterPreviewBinding Binding, RageSkeleton? Skeleton);
    private sealed record CharacterDictionaryChoice(string Key, string Label, ArchiveDocument? Document, ArchiveEntry? Entry, string? FilePath);
    private sealed record CharacterFrame(RageMesh[] Meshes, MeshPreviewPart[] Preview, Vector3 Min, Vector3 Max, int Animated, int Static);
    private CharacterPreviewBinding[] _characterBindings = [];
    private CharacterResolvedBinding[] _characterResolvedBindings = [];
    private readonly List<CharacterDictionaryChoice> _characterImportedDictionaries = [];
    private readonly Dictionary<string, RageAnimationSet> _characterAnimationCache = new(StringComparer.OrdinalIgnoreCase);
    private RageSkeleton? _characterSharedSkeleton;
    private readonly HashSet<uint> _characterJointHashes = [];
    private RageModel? _characterOriginalModel;
    private RageAnimationClip? _characterCurrentClip;
    private DispatcherTimer? _characterTimer;
    private readonly Stopwatch _characterClock = new();
    private double _characterTimeBase, _characterCurrentTime;
    private double? _characterPendingTime;
    private bool _characterFrameBusy, _characterDictionaryUpdating, _characterClipUpdating, _characterTimelineUpdating, _characterDictionaryLoading;
    private bool _characterRigidFacialAttachment;
    private int _characterSceneVersion, _characterFrameVersion, _characterDictionaryVersion;
    private CancellationTokenSource? _characterPoseCancellation, _characterDictionaryCancellation;
    private string _characterDecodeNote = "Load a clip dictionary to preview its native animation tracks.";

    private void BindCharacterPreview(IReadOnlyList<CharacterPreviewBinding> bindings)
    {
        ClearCharacterPlayback();
        if (bindings.Count is < 1 or > 4096 || bindings.Sum(b => (long)b.Mesh.Positions.Count) > 2_000_000) return;
        _characterBindings = bindings.ToArray(); _characterOriginalModel = _decodedModel;
        _characterPoseCancellation = new CancellationTokenSource();
        ResolveCharacterSkeletons();
        if (CharacterAnimationPanel != null) CharacterAnimationPanel.Visibility = Visibility.Visible;
        RefreshCharacterDictionaries(); UpdateCharacterPlaybackControls();
    }

    private void UseCharacterSkeleton(RageSkeleton skeleton)
    {
        _characterSharedSkeleton = skeleton;
        PauseCharacterPlayback(); _characterFrameVersion++;
        _characterPoseCancellation?.Cancel(); _characterPoseCancellation?.Dispose(); _characterPoseCancellation = new CancellationTokenSource();
        ResolveCharacterSkeletons(); UpdateCharacterPlaybackControls();
        if (_characterCurrentClip != null) _ = RenderCharacterFrameAsync(_characterCurrentTime, true);
    }

    private void ResolveCharacterSkeletons()
    {
        var rigs = new List<RageSkeleton>();
        if (_characterSharedSkeleton != null) rigs.Add(_characterSharedSkeleton);
        rigs.AddRange(_characterBindings.Where(b => b.Skeleton != null).Select(b => b.Skeleton!).Distinct<RageSkeleton>(ReferenceEqualityComparer.Instance));
        bool humanoidScene = rigs.Any(rig => rig.Joints.Any(j => j.Hash == 0x6686685C && j.Parent == null));
        _characterResolvedBindings = _characterBindings.Select(binding => new CharacterResolvedBinding(binding,
            binding.Mesh.Skin == null ? null : rigs.FirstOrDefault(rig => CanAnimateCharacterMesh(rig, binding.Mesh, _characterCurrentClip, humanoidScene, _characterRigidFacialAttachment)))).ToArray();
        _characterJointHashes.Clear();
        foreach (var binding in _characterResolvedBindings)
            if (binding.Skeleton != null) foreach (var joint in binding.Skeleton.Joints) _characterJointHashes.Add(joint.Hash);
    }

    private static uint[] CharacterPreviewDriverAnchors(RageSkeleton rig, bool rigidFacialAttachment)
    {
        if (!rigidFacialAttachment || !rig.Joints.Any(j => j.Hash == 0x6686685C && j.Parent == null) ||
            !RageSkinning.IsRigidAttachmentSupported(rig, 0x34C53E77, 0x54532D85) ||
            !CharacterBoneHasDrivenAncestor(rig, 0x34C53E77, new HashSet<uint> { 0x6686685C })) return [0x6686685C];
        return [0x6686685C, 0x54532D85];
    }

    private static bool CharacterBoneHasDrivenAncestor(RageSkeleton rig, uint hash, IReadOnlySet<uint> anchors)
    {
        int index = -1;
        for (int i = 0; i < rig.Joints.Count; i++)
            if (rig.Joints[i].Hash == hash) { if (index >= 0) return false; index = i; }
        for (int steps = 0; index >= 0 && steps < rig.Joints.Count; steps++)
        {
            var joint = rig.Joints[index];
            if (anchors.Contains(joint.Hash)) return true;
            index = joint.Parent ?? -1;
            if (index >= rig.Joints.Count) return false;
        }
        return false;
    }

    private static bool CanAnimateCharacterMesh(RageSkeleton rig, RageMesh mesh, RageAnimationClip? clip,
        bool humanoidScene, bool rigidFacialAttachment)
    {
        if (!RageSkinning.GetCompatibility(rig, [mesh]).Complete) return false;
        var nativeAnchors = CharacterPreviewDriverAnchors(rig, rigidFacialAttachment);
        if (humanoidScene && !RageSkinning.GetDrivenCompatibility(rig, [mesh], nativeAnchors).Complete) return false;
        if (clip == null) return true;
        var clipAnchors = clip.BoneHashes.ToHashSet();
        if (nativeAnchors.Contains(0x54532D85u) && CharacterBoneHasDrivenAncestor(rig, 0x34C53E77, clipAnchors))
            clipAnchors.Add(0x54532D85);
        return RageSkinning.GetDrivenCompatibility(rig, [mesh], clipAnchors).Complete;
    }

    private void ClearCharacterPlayback()
    {
        _characterTimer?.Stop(); _characterClock.Reset(); _characterTimeBase = _characterCurrentTime = 0;
        _characterSceneVersion++; _characterFrameVersion++; _characterDictionaryVersion++; _characterPendingTime = null;
        _characterPoseCancellation?.Cancel(); _characterPoseCancellation?.Dispose(); _characterPoseCancellation = null;
        _characterDictionaryCancellation?.Cancel(); _characterDictionaryCancellation?.Dispose(); _characterDictionaryCancellation = null;
        _characterBindings = []; _characterResolvedBindings = []; _characterOriginalModel = null; _characterCurrentClip = null;
        _characterJointHashes.Clear();
        _characterDictionaryLoading = false; _characterRigidFacialAttachment = false;
        if (CharacterAnimationPanel != null) CharacterAnimationPanel.Visibility = Visibility.Collapsed;
        if (CharacterClipSelector != null)
        {
            _characterClipUpdating = true; CharacterClipSelector.ItemsSource = null; _characterClipUpdating = false;
        }
        UpdateCharacterPlaybackControls();
    }

    private void RefreshCharacterDictionaries()
    {
        if (AnimationDictionarySelector == null) return;
        string? previous = (AnimationDictionarySelector.SelectedItem as CharacterDictionaryChoice)?.Key;
        var loaded = (_assetIndex?.Assets ?? []).Where(a => a.Kind == "animation" && a.Available)
            .Select(a => new CharacterDictionaryChoice(a.SourcePath + "\0" + a.Entry.Path + "\0" + a.Entry.Offset,
                a.Name + " · " + a.SourceName, a.Document, a.Entry, null));
        var choices = _characterImportedDictionaries.Concat(loaded).DistinctBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ToArray();
        _characterDictionaryUpdating = true;
        AnimationDictionarySelector.ItemsSource = choices;
        AnimationDictionarySelector.SelectedItem = choices.FirstOrDefault(c => c.Key == previous) ?? choices.FirstOrDefault();
        _characterDictionaryUpdating = false;
        if (AnimationDictionarySelector.SelectedItem is CharacterDictionaryChoice choice) _ = LoadCharacterDictionaryAsync(choice);
    }

    private async void LoadCharacterAnimations_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _characterDictionaryLoading) return;
        var dialog = new OpenFileDialog { Title = "Load native character animations", Filter = "Clip dictionaries or RPF archives|*.rpf;*.r9clipdata;*.bin|All files|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        int scene = _characterSceneVersion;
        try
        {
            if (Path.GetExtension(dialog.FileName).Equals(".rpf", StringComparison.OrdinalIgnoreCase))
            {
                _characterDictionaryLoading = true; UpdateCharacterPlaybackControls();
                CharacterAnimationStatus.Text = "Looking for complete clip dictionaries in the archive's initial search range…";
                var document = await Task.Run(() => ArchiveReader.Open(dialog.FileName));
                if (scene != _characterSceneVersion) return;
                var choices = document.Entries.Where(e => e.Compression == "Recovered clip dictionary" && e.CanExtract)
                    .Select(entry => new CharacterDictionaryChoice(document.SourcePath + "\0" + entry.Path + "\0" + entry.Offset,
                        entry.Name + " · " + Path.GetFileName(document.SourcePath), document, entry, null)).ToArray();
                if (choices.Length == 0) throw new InvalidDataException("No complete clip dictionaries were found in the initial archive range. Load a recovered clip dictionary, or scan the archive and select its animation entries.");
                foreach (var choice in choices)
                {
                    _characterAnimationCache.Remove(choice.Key);
                    if (_characterImportedDictionaries.All(c => c.Key != choice.Key)) _characterImportedDictionaries.Add(choice);
                }
                RefreshCharacterDictionaries();
                AnimationDictionarySelector.SelectedItem = AnimationDictionarySelector.Items.OfType<CharacterDictionaryChoice>().FirstOrDefault(c => c.Key == choices[0].Key);
            }
            else
            {
                string path = Path.GetFullPath(dialog.FileName);
                var choice = new CharacterDictionaryChoice("file:" + path, Path.GetFileName(path), null, null, path);
                _characterImportedDictionaries.RemoveAll(c => string.Equals(c.Key, choice.Key, StringComparison.OrdinalIgnoreCase));
                _characterImportedDictionaries.Add(choice); _characterAnimationCache.Remove(choice.Key);
                RefreshCharacterDictionaries();
                AnimationDictionarySelector.SelectedItem = AnimationDictionarySelector.Items.OfType<CharacterDictionaryChoice>().First(c => c.Key == choice.Key);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException)
        { if (scene == _characterSceneVersion) CharacterAnimationStatus.Text = "Could not load animations: " + ex.Message; }
        finally { if (scene == _characterSceneVersion) { _characterDictionaryLoading = false; UpdateCharacterPlaybackControls(); } }
    }

    private async void AnimationDictionary_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_characterDictionaryUpdating && AnimationDictionarySelector.SelectedItem is CharacterDictionaryChoice choice)
            await LoadCharacterDictionaryAsync(choice);
    }

    private async Task LoadCharacterDictionaryAsync(CharacterDictionaryChoice choice)
    {
        PauseCharacterPlayback();
        _characterDictionaryCancellation?.Cancel(); _characterDictionaryCancellation?.Dispose();
        var cancellation = _characterDictionaryCancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        int version = ++_characterDictionaryVersion, scene = _characterSceneVersion;
        _characterFrameVersion++; _characterPendingTime = null; _characterCurrentClip = null;
        _characterClipUpdating = true; CharacterClipSelector.ItemsSource = null; _characterClipUpdating = false;
        _characterDictionaryLoading = true; UpdateCharacterPlaybackControls();
        CharacterAnimationStatus.Text = "Decoding native animation tracks…";
        try
        {
            if (_characterBindings.Length > 0 && _characterOriginalModel != null)
            {
                ModelPreview.ShowMeshes(_characterBindings.Select(b => b.Preview), false);
                _decodedModel = _characterOriginalModel;
            }
            if (!_characterAnimationCache.TryGetValue(choice.Key, out var set))
            {
                set = await Task.Run(() =>
                {
                    byte[] bytes;
                    if (choice.Document != null && choice.Entry != null)
                        bytes = ArchiveReader.ReadRecoveredPayload(choice.Document, choice.Entry, token);
                    else
                    {
                        using var file = new FileStream(choice.FilePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        if (file.Length is < 0xB0 or > 64 * 1024 * 1024) throw new InvalidDataException("A decoded clip resource must fit within 64 MiB.");
                        bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
                    }
                    token.ThrowIfCancellationRequested();
                    return RageAnimationDecoder.Decode(bytes, token) ?? throw new InvalidDataException("This is not a supported complete native clip dictionary.");
                }, token);
                if (version != _characterDictionaryVersion || scene != _characterSceneVersion) return;
                if (_characterAnimationCache.Count >= 4) _characterAnimationCache.Clear();
                if (set.Clips.Count > 0) _characterAnimationCache[choice.Key] = set;
            }
            if (version != _characterDictionaryVersion || scene != _characterSceneVersion || token.IsCancellationRequested) return;
            _characterDecodeNote = $"{set.Clips.Count:N0} playable clips from {set.Name}" + (set.Diagnostics.Count > 0 ? $" · {set.Diagnostics.Count:N0} unsupported records" : "");
            _characterClipUpdating = true;
            CharacterClipSelector.ItemsSource = set.Clips;
            CharacterClipSelector.SelectedIndex = set.Clips.Count > 0 ? 0 : -1;
            _characterClipUpdating = false;
            SelectCharacterClip();
            if (set.Clips.Count == 0) CharacterAnimationStatus.Text = _characterDecodeNote + ". " + string.Join(" ", set.Diagnostics.Take(2));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException or InvalidOperationException)
        { if (version == _characterDictionaryVersion && scene == _characterSceneVersion) { _characterCurrentClip = null; CharacterAnimationStatus.Text = "Animation decode unavailable: " + ex.Message; } }
        finally { if (version == _characterDictionaryVersion) { _characterDictionaryLoading = false; UpdateCharacterPlaybackControls(); } }
    }

    private void CharacterClip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_characterClipUpdating) SelectCharacterClip(); }

    private void SelectCharacterClip()
    {
        PauseCharacterPlayback(); _characterFrameVersion++; _characterPendingTime = null;
        _characterCurrentClip = CharacterClipSelector.SelectedItem as RageAnimationClip;
        ResolveCharacterSkeletons();
        _characterTimeBase = _characterCurrentTime = 0;
        _characterTimelineUpdating = true;
        CharacterTimeline.Maximum = Math.Max(0.001, _characterCurrentClip?.DurationSeconds ?? 1);
        CharacterTimeline.Value = 0; _characterTimelineUpdating = false;
        UpdateCharacterPlaybackControls();
        if (_characterCurrentClip != null) _ = RenderCharacterFrameAsync(0, true);
    }

    private void CharacterPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_characterClock.IsRunning) { PauseCharacterPlayback(); return; }
        if (_busy || _characterCurrentClip == null || !_characterCurrentClip.BoneHashes.Any(_characterJointHashes.Contains)) return;
        if (_characterCurrentTime >= _characterCurrentClip.DurationSeconds) _characterCurrentTime = 0;
        _characterTimeBase = _characterCurrentTime; _characterClock.Restart();
        if (_characterTimer == null)
        {
            _characterTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromSeconds(1.0 / 30) };
            _characterTimer.Tick += CharacterAnimationTick;
            Closed += (_, _) => ClearCharacterPlayback();
        }
        _characterTimer.Start(); UpdateCharacterPlaybackControls();
    }

    private void PauseCharacterPlayback()
    {
        if (_characterClock.IsRunning) _characterTimeBase = _characterCurrentTime;
        _characterTimer?.Stop(); _characterClock.Reset(); UpdateCharacterPlaybackControls();
    }

    private void CharacterStop_Click(object sender, RoutedEventArgs e)
    {
        PauseCharacterPlayback(); _characterTimeBase = _characterCurrentTime = 0;
        if (_characterCurrentClip != null) _ = RenderCharacterFrameAsync(0, true);
    }

    private async void CharacterAnimationTick(object? sender, EventArgs e)
    {
        if (_busy || _characterCurrentClip == null || _characterFrameBusy) return;
        double time = _characterTimeBase + _characterClock.Elapsed.TotalSeconds;
        if (time >= _characterCurrentClip.DurationSeconds)
        {
            if (CharacterLoopCheckBox.IsChecked == true) time %= _characterCurrentClip.DurationSeconds;
            else { time = _characterCurrentClip.DurationSeconds; PauseCharacterPlayback(); }
        }
        await RenderCharacterFrameAsync(time, false);
    }

    private void CharacterTimeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_characterTimelineUpdating || _characterCurrentClip == null || _characterBindings.Length == 0) return;
        _characterTimeBase = _characterCurrentTime = e.NewValue;
        if (_characterClock.IsRunning) _characterClock.Restart();
        _ = RenderCharacterFrameAsync(e.NewValue, true);
    }

    private async Task RenderCharacterFrameAsync(double time, bool seek)
    {
        if (_characterCurrentClip is not { } clip || _characterOriginalModel == null || _characterBindings.Length == 0 || _characterPoseCancellation == null) return;
        if (_characterFrameBusy) { if (seek) { _characterFrameVersion++; _characterPendingTime = time; } return; }
        _characterFrameBusy = true; int scene = _characterSceneVersion, version = ++_characterFrameVersion;
        var bindings = _characterResolvedBindings; var token = _characterPoseCancellation.Token;
        bool rigidFacialAttachment = _characterRigidFacialAttachment;
        try
        {
            var frame = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested(); var pose = clip.Sample(time);
                var poses = new Dictionary<RageSkeleton, IReadOnlyList<Matrix4x4>?>(ReferenceEqualityComparer.Instance);
                var meshes = new RageMesh[bindings.Length]; var preview = new MeshPreviewPart[bindings.Length];
                Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity); int animated = 0, kept = 0;
                for (int i = 0; i < bindings.Length; i++)
                {
                    token.ThrowIfCancellationRequested(); var item = bindings[i]; var mesh = item.Binding.Mesh;
                    if (item.Skeleton is { } skeleton)
                    {
                        if (!poses.TryGetValue(skeleton, out var modelPose))
                        {
                            modelPose = skeleton.Joints.Any(j => pose.ContainsKey(j.Hash)) ? RageSkinning.CreateModelPose(skeleton, pose) : null;
                            if (modelPose != null && rigidFacialAttachment && CharacterPreviewDriverAnchors(skeleton, true).Contains(0x54532D85u) &&
                                CharacterBoneHasDrivenAncestor(skeleton, 0x34C53E77, pose.Keys.ToHashSet()))
                                modelPose = RageSkinning.ApplyRigidAttachment(skeleton, modelPose, 0x34C53E77, 0x54532D85);
                            poses[skeleton] = modelPose;
                        }
                        var deformed = modelPose == null ? null : RageSkinning.Deform(mesh, skeleton, modelPose);
                        if (deformed != null) { mesh = deformed; animated++; } else kept++;
                    }
                    else kept++;
                    meshes[i] = mesh; preview[i] = item.Binding.Preview with { Positions = mesh.Positions };
                    foreach (var position in mesh.Positions) { min = Vector3.Min(min, position); max = Vector3.Max(max, position); }
                }
                return new CharacterFrame(meshes, preview, min, max, animated, kept);
            }, token);
            if (token.IsCancellationRequested || scene != _characterSceneVersion || version != _characterFrameVersion || _characterCurrentClip != clip || _characterOriginalModel == null) return;
            ModelPreview.ShowMeshes(frame.Preview, false);
            _decodedModel = _characterOriginalModel with { Meshes = Array.AsReadOnly(frame.Meshes), BoundsMin = frame.Min, BoundsMax = frame.Max };
            _characterCurrentTime = time;
            _characterTimelineUpdating = true; CharacterTimeline.Value = time; _characterTimelineUpdating = false;
            CharacterTimeText.Text = $"{time:0.00} / {clip.DurationSeconds:0.00} s";
            CharacterAnimationStatus.Text = $"{clip.Name} · {frame.Animated:N0} animated mesh parts · {frame.Static:N0} static or incompatible parts · {clip.FrameRate:0.##} fps source" +
                (rigidFacialAttachment ? " · rigid facial attachment preview" : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException or InvalidOperationException)
        { if (scene == _characterSceneVersion) { PauseCharacterPlayback(); CharacterAnimationStatus.Text = "Character pose unavailable: " + ex.Message; } }
        finally
        {
            _characterFrameBusy = false;
            if (_characterPendingTime is double pending && _characterCurrentClip != null)
            { _characterPendingTime = null; _ = RenderCharacterFrameAsync(pending, true); }
        }
    }

    private void UpdateCharacterPlaybackControls()
    {
        if (CharacterPlayButton == null || CharacterClipSelector == null) return;
        bool ready = _characterCurrentClip != null && _characterCurrentClip.BoneHashes.Any(_characterJointHashes.Contains);
        CharacterPlayButton.IsEnabled = !_busy && !_characterDictionaryLoading && ready;
        CharacterPlayButton.Content = _characterClock.IsRunning ? "Pause" : "Play";
        CharacterStopButton.IsEnabled = !_busy && ready;
        CharacterTimeline.IsEnabled = !_busy && ready;
        AnimationDictionarySelector.IsEnabled = !_busy && !_characterDictionaryLoading;
        CharacterClipSelector.IsEnabled = !_busy && !_characterDictionaryLoading && CharacterClipSelector.Items.Count > 0;
        if (CharacterTimeText != null && _characterCurrentClip == null) CharacterTimeText.Text = "0.00 / 0.00 s";
        if (CharacterAnimationStatus == null || _characterBindings.Length == 0 || _characterDictionaryLoading || _characterClock.IsRunning) return;
        int weighted = _characterBindings.Count(b => b.Mesh.Skin != null), compatible = _characterResolvedBindings.Count(b => b.Skeleton != null);
        CharacterAnimationStatus.Text = compatible == 0 && weighted > 0
            ? "The mesh weights need a compatible skeleton and tracked native bone ancestry. Detached procedural drivers stay static."
            : weighted == 0 ? "These parts have no decoded skin weights. Select supported character components to animate them."
            : $"{_characterDecodeNote} · {compatible:N0} of {weighted:N0} weighted mesh parts have a compatible skeleton.";
        if (_characterRigidFacialAttachment) CharacterAnimationStatus.Text += " · rigid facial attachment preview";
    }
}
