namespace RpfPreview.Core;

public sealed record PayloadIndexProgress(double Fraction, string Message);
public sealed record PayloadIndexResult(int Models, int TextureDictionaries, int AudioBanks, int Shaders = 0);


public static class PayloadIndexBuilder
{
    public static PayloadIndexResult Build(string source, IProgress<PayloadIndexProgress>? progress = null, CancellationToken token = default)
    {
        source = Path.GetFullPath(source);
        long length = new FileInfo(source).Length;
        if (length < 64) throw new InvalidDataException("The source is too short.");
        using (var file = File.OpenRead(source))
        {
            Span<byte> magic = stackalloc byte[4]; file.ReadExactly(magic);
            if (!magic.SequenceEqual("9FPR"u8)) throw new NotSupportedException("Full payload scanning currently supports the observed little-endian RPF9 format.");
        }
        void Report(int stage, double part, string text) => progress?.Report(new((stage + Math.Clamp(part, 0, 1)) / 4, text));
        var modelOffsets = Rpf9ModelRecovery.FindCandidateOffsets(source, 0, null,
            new InlineProgress<long>(position => Report(0, .45 * position / length, $"Scanning model headers · {position:N0}/{length:N0} bytes")), token);
        var models = new List<ArchiveEntry>();
        for (int i = 0; i < modelOffsets.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            models.AddRange(Rpf9ModelRecovery.RecoverAtOffsets(source, length, new[] { modelOffsets[i] }, models.Count, token));
            if (i % 100 == 0) Report(0, .45 + .55 * i / Math.Max(1, modelOffsets.Count), $"Validating models · {i:N0}/{modelOffsets.Count:N0} candidates · {models.Count:N0} supported");
            if (i > 0 && i % 1000 == 0) RecoveryIndexCache.Save("models", source, models,
                $"Full page+16 scan completed; model validation in progress ({i:N0}/{modelOffsets.Count:N0}). Original RPF9 directory remains undecoded.");
        }
        RecoveryIndexCache.Save("models", source, models, $"Full {length:N0}-byte page+16 scan; {models.Count:N0} supported models; other layouts and original directory remain undecoded.");

        var textureOffsets = Rpf9TextureRecovery.FindCandidateOffsets(source, 0, null,
            new InlineProgress<long>(position => Report(1, .45 * position / length, $"Scanning texture headers · {position:N0}/{length:N0} bytes")), token);
        var textures = new List<ArchiveEntry>();
        for (int i = 0; i < textureOffsets.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            textures.AddRange(Rpf9TextureRecovery.RecoverAtOffsets(source, length, new[] { textureOffsets[i] }, textures.Count, token));
            if (i % 100 == 0) Report(1, .45 + .55 * i / Math.Max(1, textureOffsets.Count), $"Validating textures · {i:N0}/{textureOffsets.Count:N0} candidates · {textures.Count:N0} supported dictionaries");
        }
        RecoveryIndexCache.Save("textures", source, textures, $"Full {length:N0}-byte page+16 scan; {textures.Count:N0} supported dictionaries; original directory remains undecoded.");

        var audio = AwcRecovery.RecoverFull(source,
            new InlineProgress<long>(position => Report(2, (double)position / length, $"Scanning audio banks · {position:N0}/{length:N0} bytes")), token);
        token.ThrowIfCancellationRequested();
        AwcRecovery.WriteCache(source, audio);
        var shaders = Rpf9ShaderRecovery.RecoverRange(source, length, 0, length, 0,
            new InlineProgress<long>(position => Report(3, (double)position / length, $"Scanning native shaders · {position:N0}/{length:N0} bytes")), token);
        token.ThrowIfCancellationRequested();
        RecoveryIndexCache.Save("shaders", source, shaders, $"Full {length:N0}-byte raw-wrapper scan; {shaders.Count:N0} validated native shader containers. Original directory remains undecoded.");
        Report(3, 1, $"Index ready · {models.Count:N0} models · {textures.Count:N0} texture dictionaries · {audio.Count:N0} audio banks · {shaders.Count:N0} shaders");
        return new(models.Count, textures.Count, audio.Count, shaders.Count);
    }
    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
