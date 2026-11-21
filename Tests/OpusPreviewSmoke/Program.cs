using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using RpfPreview.Core;

if (args.Length != 1 || !File.Exists(args[0]))
    throw new ArgumentException("Pass the path to the built opus-preview.exe.");

string helper = Path.GetFullPath(args[0]);
const int Opus = 0x0B;
const int Pcm = 0;
var working = Directory.CreateTempSubdirectory("rpf9-opus-test-");
try
{
    var full = CheckPreview("32k", Opus, 32000, 32000, 2, 48000, 48000);
    var capped = CheckPreview("32k-capped", Opus, 32000, 32000, 1, 48000, 32000);
    var normal = CheckPreview("48k", Opus, 48000, 48000, 2, 48000, 48000);
    if (!full.AsSpan().SequenceEqual(normal) || !full.AsSpan(0, capped.Length).SequenceEqual(capped))
        throw new InvalidDataException("The 32 kHz preview changed the decoded audio samples.");
    CheckPreview("other-tag", Opus, 24000, 48000, 2, 48000, 48000);
    CheckPreview("pcm16", Pcm, 44100, 44100, 1, 3, 3);
    Console.WriteLine("32 kHz duration, WAV rate, frame cap, and PCM readback passed. 48 kHz Opus and PCM16 passed.");
}
finally
{
    foreach (var file in working.EnumerateFiles()) file.Delete();
    working.Delete();
}

byte[] CheckPreview(string name, int codec, ushort bankRate, int playbackRate, int seconds, int declaredFrames, int expectedFrames)
{
    string bank = Path.Combine(working.FullName, name + ".awc");
    string wav = Path.Combine(working.FullName, name + ".wav");
    byte[] fixture = MakeBank(codec, bankRate, declaredFrames);
    File.WriteAllBytes(bank, fixture);
    var stream = AwcInfo.Inspect(bank, 0, fixture.Length)?.Streams.Single()
        ?? throw new InvalidDataException($"{name}: the synthetic bank could not be inspected.");
    if (!stream.SupportsPreview || stream.SampleRate != bankRate || stream.DecodedSampleRate != playbackRate ||
        Math.Abs(stream.DurationSeconds - declaredFrames / (double)playbackRate) > 1e-9)
        throw new InvalidDataException($"{name}: wrong playback rate or duration in the bank listing.");

    var start = new ProcessStartInfo(helper) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string argument in new[] { bank, "0", fixture.Length.ToString(CultureInfo.InvariantCulture), "0", wav, seconds.ToString(CultureInfo.InvariantCulture) })
        start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the preview helper.");
    var output = process.StandardOutput.ReadToEndAsync();
    var errors = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(10000))
    {
        process.Kill(entireProcessTree: true);
        process.WaitForExit();
        throw new TimeoutException($"{name}: preview did not finish.");
    }
    if (process.ExitCode != 0)
        throw new InvalidDataException($"{name}: {errors.GetAwaiter().GetResult()}");
    using var report = JsonDocument.Parse(output.GetAwaiter().GetResult());
    if (report.RootElement.GetProperty("sampleRate").GetInt32() != playbackRate ||
        report.RootElement.GetProperty("decodedSamples").GetInt32() != expectedFrames ||
        report.RootElement.GetProperty("previewLimited").GetBoolean() != (expectedFrames < declaredFrames))
        throw new InvalidDataException($"{name}: preview reported the wrong rate, frame count, or cutoff.");

    byte[] wave = File.ReadAllBytes(wav);
    if (wave.Length != 44 + expectedFrames * 2 ||
        !wave.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wave.AsSpan(8, 4).SequenceEqual("WAVE"u8) ||
        BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(24)) != playbackRate ||
        BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(28)) != playbackRate * 2 ||
        BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(40)) != expectedFrames * 2)
        throw new InvalidDataException($"{name}: incorrect WAV header or data length.");
    return wave[44..];
}

static byte[] MakeBank(int codec, ushort rate, int frames)
{
    const int HeaderBytes = 36, FormatBytes = 32, DataOffset = HeaderBytes + FormatBytes;
    int dataBytes = codec == Opus ? 2048 : frames * 2;
    byte[] bank = new byte[DataOffset + dataBytes];
    "ADAT"u8.CopyTo(bank);
    void Put32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bank.AsSpan(offset), value);
    void Tag(int offset, byte kind, int size, int position) =>
        BinaryPrimitives.WriteUInt64LittleEndian(bank.AsSpan(offset), ((ulong)kind << 56) | ((ulong)size << 28) | (uint)position);
    Put32(4, 0xFF000001);
    Put32(8, 1);
    Put32(12, HeaderBytes);
    Put32(16, (2u << 29) | 1u);
    Tag(20, 0x76, FormatBytes, HeaderBytes);
    Tag(28, 0x55, dataBytes, DataOffset);
    Put32(HeaderBytes, (uint)frames);
    BinaryPrimitives.WriteUInt16LittleEndian(bank.AsSpan(HeaderBytes + 8), rate);
    Put32(HeaderBytes + 12, (uint)frames);
    bank[HeaderBytes + 28] = (byte)codec;
    bank[HeaderBytes + 29] = 1;
    if (codec == Opus)
    {
        // A length-prefixed, 20 ms mono silence packet generated with libopus at 48 kHz.
        byte[] packet = [3, 0, 0xF8, 0xFF, 0xFE];
        for (int frame = 0, at = DataOffset; frame < frames; frame += 960, at += packet.Length)
            packet.CopyTo(bank, at);
    }
    return bank;
}
