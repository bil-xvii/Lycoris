using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Monolith.Core.Devices;

namespace Monolith.Modules.Devices.Audio;

/// <summary>What a level test captured: one value per frame (the loudest channel), at the stream's rate.</summary>
public sealed record MicCapture(float[] Samples, int SampleRate, AudioFormat Format);

/// <summary>
/// The microphone level test: a temporary shared-mode capture stream (IAudioClient + IAudioCaptureClient) for a few seconds.
/// Changes nothing: no endpoint setting, no format, no exclusive access; the stream is stopped and released in finally.
/// Windows shows its microphone indicator while it runs. Never started by itself (only the ПРОВЕРИТЬ button).
/// </summary>
public static class MicLevelMeter
{
    private const long BufferDuration = 10_000_000; // 1 s in 100-ns units
    private static readonly TimeSpan PeakInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Captures <paramref name="duration"/> of audio from an active endpoint and reports the 50-ms peak (0..1) to
    /// <paramref name="level"/>. Throws InvalidOperationException with a plain Russian message when it cannot start.
    /// </summary>
    public static Task<MicCapture> CaptureAsync(string endpointId, TimeSpan duration, IProgress<double>? level, CancellationToken ct)
    {
        if (!DeviceTargets.IsCaptureEndpointId(endpointId)) throw new InvalidOperationException("Это не микрофон.");
        if (AudioInventory.ReadPrivacy() == MicPrivacy.Deny)
            throw new InvalidOperationException("Доступ к микрофону запрещён в параметрах конфиденциальности Windows.");
        // A dedicated pool thread (MTA): the whole stream lives and dies on it.
        return Task.Run(() => Capture(endpointId, duration, level, ct), ct);
    }

    private static MicCapture Capture(string endpointId, TimeSpan duration, IProgress<double>? level, CancellationToken ct)
    {
        IMMDeviceEnumerator? en = null;
        IMMDevice? dev = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        var mixFormat = IntPtr.Zero;
        var started = false;
        try
        {
            en = CoreAudio.CreateEnumerator();
            dev = CoreAudio.ActiveDevice(en, endpointId)
                  ?? throw new InvalidOperationException("Микрофон не найден или отключён — подключите его и нажмите «ОБНОВИТЬ».");
            client = CoreAudio.Activate<IAudioClient>(dev, CoreAudio.IidAudioClient)
                     ?? throw new InvalidOperationException("Windows не дала открыть микрофон.");
            Check(client.GetMixFormat(out mixFormat), "формат микрофона не прочитан");
            var format = AudioParsing.ParseWaveFormat(CoreAudio.CopyWaveFormat(mixFormat))
                         ?? throw new InvalidOperationException("Формат микрофона не поддерживается проверкой.");
            Check(client.Initialize(CoreAudio.AudclntSharemodeShared, 0, BufferDuration, 0, mixFormat, IntPtr.Zero), "микрофон занят или недоступен");
            var iid = CoreAudio.IidAudioCaptureClient;
            Check(client.GetService(ref iid, out var service), "поток записи не открылся");
            capture = service as IAudioCaptureClient ?? throw new InvalidOperationException("Поток записи не открылся.");
            Check(client.Start(), "запись не началась");
            started = true;

            var samples = new List<float>(format.SampleRate * Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds)) + format.SampleRate);
            var frame = new MicFrameReader(format);
            var watch = Stopwatch.StartNew();
            var peak = 0f;
            var lastReport = TimeSpan.Zero;
            while (watch.Elapsed < duration)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(10);
                while (capture.GetNextPacketSize(out var packet) >= 0 && packet > 0)
                {
                    Check(capture.GetBuffer(out var data, out var frames, out var flags, out _, out _), "чтение записи прервалось");
                    try
                    {
                        var silent = (flags & CoreAudio.AudclntBufferflagsSilent) != 0 || data == IntPtr.Zero;
                        var before = samples.Count;
                        frame.Read(data, (int)frames, silent, samples);
                        for (var i = before; i < samples.Count; i++) peak = Math.Max(peak, Math.Abs(samples[i]));
                    }
                    finally
                    {
                        capture.ReleaseBuffer(frames);
                    }
                }
                if (watch.Elapsed - lastReport >= PeakInterval)
                {
                    level?.Report(Math.Clamp(peak, 0f, 1f));
                    peak = 0f;
                    lastReport = watch.Elapsed;
                }
            }
            return new MicCapture([.. samples], format.SampleRate, format);
        }
        finally
        {
            if (started)
            {
                try { client?.Stop(); }
                catch { /* the stream is released below anyway */ }
            }
            CoreAudio.Release(capture);
            CoreAudio.Release(client);
            CoreAudio.Release(dev);
            CoreAudio.Release(en);
            if (mixFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFormat);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"Проверка не удалась: {what} (0x{hr:X8}).");
    }
}

/// <summary>Converts captured frames to one float per frame (the channel with the largest magnitude). Pure except for the pointer read.</summary>
internal sealed class MicFrameReader(AudioFormat format)
{
    private readonly int _channels = Math.Max(1, format.Channels);
    private readonly int _bytes = Math.Max(1, (format.ContainerBits > 0 ? format.ContainerBits : format.Bits) / 8);
    private readonly int _block = format.BlockAlign > 0 ? format.BlockAlign
        : Math.Max(1, format.Channels) * Math.Max(1, (format.ContainerBits > 0 ? format.ContainerBits : format.Bits) / 8);
    private byte[] _buffer = [];

    public void Read(IntPtr data, int frames, bool silent, List<float> output)
    {
        if (frames <= 0) return;
        if (silent)
        {
            for (var i = 0; i < frames; i++) output.Add(0f);
            return;
        }
        var size = frames * _block;
        if (_buffer.Length < size) _buffer = new byte[size];
        Marshal.Copy(data, _buffer, 0, size);
        MicSamples.Convert(_buffer.AsSpan(0, size), frames, _block, _channels, _bytes, format.IsFloat, output);
    }
}

/// <summary>Sample conversion helpers (pure).</summary>
public static class MicSamples
{
    public static float FromInt16(short v) => v / 32768f;

    /// <summary>A little-endian packed 24-bit sample (sign-extended).</summary>
    public static float FromInt24(byte b0, byte b1, byte b2)
    {
        var v = b0 | (b1 << 8) | (b2 << 16);
        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
        return v / 8388608f;
    }

    /// <summary>32-bit integer PCM (also 24 valid bits left-justified in a 32-bit container).</summary>
    public static float FromInt32(int v) => (float)(v / 2147483648.0);

    /// <summary>One value per frame: the channel sample with the largest magnitude, sign kept.</summary>
    public static void Convert(ReadOnlySpan<byte> data, int frames, int blockAlign, int channels, int bytesPerSample, bool isFloat, List<float> output)
    {
        for (var f = 0; f < frames; f++)
        {
            var frame = data.Slice(f * blockAlign, Math.Min(blockAlign, data.Length - f * blockAlign));
            var best = 0f;
            for (var c = 0; c < channels; c++)
            {
                var offset = c * bytesPerSample;
                if (offset + bytesPerSample > frame.Length) break;
                var s = frame.Slice(offset, bytesPerSample);
                var v = (isFloat, bytesPerSample) switch
                {
                    (true, 4) => BinaryPrimitives.ReadSingleLittleEndian(s),
                    (true, 8) => (float)BinaryPrimitives.ReadDoubleLittleEndian(s),
                    (false, 1) => (s[0] - 128) / 128f,
                    (false, 2) => FromInt16(BinaryPrimitives.ReadInt16LittleEndian(s)),
                    (false, 3) => FromInt24(s[0], s[1], s[2]),
                    (false, 4) => FromInt32(BinaryPrimitives.ReadInt32LittleEndian(s)),
                    _ => 0f,
                };
                if (!float.IsFinite(v)) v = 0f;
                if (Math.Abs(v) > Math.Abs(best)) best = v;
            }
            output.Add(best);
        }
    }
}

/// <summary>Peaks, RMS and clipping of a capture (pure).</summary>
public sealed record MicLevelStats(double PeakDbfs, double P95WindowPeakDbfs, double RmsDbfs, int ClippedSamples, int SpeechWindows)
{
    /// <summary>Time with speech: 50-ms windows whose peak is above −45 dBFS.</summary>
    public double SpeechSeconds => SpeechWindows * MicLevelAnalysis.WindowSeconds;
}

public enum MicLevelVerdict { NoSpeech, Quiet, Ok, Loud, Clipping }

/// <summary>
/// The level test's analysis and the level suggestion (research devices_audio.md audio-mic-level: speech peaks −12…−6 dBFS,
/// no sample above −1 dBFS, then re-measure). Pure and unit-tested.
/// </summary>
public static class MicLevelAnalysis
{
    public const double WindowSeconds = 0.05;
    public const double SpeechThresholdDbfs = -45;
    /// <summary>Where the loud speech windows (p95) should land.</summary>
    public const double TargetDbfs = -9;
    /// <summary>No sample may go above this after the change.</summary>
    public const double TruePeakLimitDbfs = -1;
    /// <summary>|x| at or above this counts as a sample at the limit (clipped).</summary>
    public const float ClipLevel = 0.999f;
    public const double MinSpeechSeconds = 1.0;
    public const double MinChangeDb = 1.5;

    public static double ToDbfs(double amplitude) => amplitude <= 1e-6 ? -120 : 20 * Math.Log10(amplitude);

    public static MicLevelStats Analyze(float[] samples, int sampleRate)
    {
        if (samples.Length == 0 || sampleRate <= 0) return new MicLevelStats(-120, -120, -120, 0, 0);
        var window = Math.Max(1, (int)Math.Round(sampleRate * WindowSeconds));
        double peak = 0, sumSquares = 0;
        var clipped = 0;
        var windowPeaks = new List<double>(samples.Length / window + 1);
        double current = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var a = Math.Abs((double)samples[i]);
            if (!double.IsFinite(a)) a = 0;
            if (a > peak) peak = a;
            if (a >= ClipLevel) clipped++;
            sumSquares += a * a;
            if (a > current) current = a;
            if ((i + 1) % window == 0)
            {
                windowPeaks.Add(current);
                current = 0;
            }
        }
        if (samples.Length % window != 0) windowPeaks.Add(current);

        var speech = windowPeaks.Where(p => ToDbfs(p) >= SpeechThresholdDbfs).OrderBy(p => p).ToList();
        var basis = speech.Count > 0 ? speech : windowPeaks.OrderBy(p => p).ToList();
        var p95 = basis.Count == 0 ? 0 : basis[Math.Clamp((int)Math.Ceiling(0.95 * basis.Count) - 1, 0, basis.Count - 1)];
        var rms = Math.Sqrt(sumSquares / samples.Length);
        return new MicLevelStats(ToDbfs(peak), ToDbfs(p95), ToDbfs(rms), clipped, speech.Count);
    }

    public static MicLevelVerdict Classify(MicLevelStats s) =>
        s.ClippedSamples > 0 ? MicLevelVerdict.Clipping
        : s.SpeechSeconds < MinSpeechSeconds ? MicLevelVerdict.NoSpeech
        : s.P95WindowPeakDbfs > -6 || s.PeakDbfs > TruePeakLimitDbfs ? MicLevelVerdict.Loud
        : s.P95WindowPeakDbfs < -12 ? MicLevelVerdict.Quiet
        : MicLevelVerdict.Ok;

    /// <summary>
    /// The endpoint level (dB) that puts the speech peaks (p95 of 50-ms windows) at <paramref name="target"/> while no sample
    /// goes above −1 dBFS; with clipped samples only a lower level (at least 3 dB down, the real overshoot is unknown).
    /// Clamped to the endpoint's range and rounded to 0,5 dB. Null when there was too little speech (&lt; 1 s above
    /// −45 dBFS) or the change would be under 1,5 dB.
    /// </summary>
    public static double? SuggestDb(double currentDb, MicLevelStats s, double rangeMin, double rangeMax, double target = TargetDbfs)
    {
        if (!double.IsFinite(currentDb) || !double.IsFinite(rangeMin) || !double.IsFinite(rangeMax) || rangeMax < rangeMin) return null;
        if (s.SpeechSeconds < MinSpeechSeconds) return null;
        var delta = target - s.P95WindowPeakDbfs;
        delta = Math.Min(delta, TruePeakLimitDbfs - s.PeakDbfs);
        if (s.ClippedSamples > 0) delta = Math.Min(delta, -3);
        var wanted = Math.Round((currentDb + delta) * 2, MidpointRounding.AwayFromZero) / 2;
        var next = Math.Clamp(wanted, rangeMin, rangeMax);
        return Math.Abs(next - currentDb) < MinChangeDb ? null : next;
    }

    /// <summary>«тихо: пики −24 дБFS», «в норме: пики −9 дБFS», «громко: …», «перегруз: 12 отсчётов на пределе».</summary>
    public static string Verdict(MicLevelStats s) => Classify(s) switch
    {
        MicLevelVerdict.Clipping => $"перегруз: {s.ClippedSamples.ToString(CultureInfo.InvariantCulture)} {Plural(s.ClippedSamples, "отсчёт", "отсчёта", "отсчётов")} на пределе",
        MicLevelVerdict.NoSpeech => "мало речи — говорите обычным голосом всё время проверки",
        MicLevelVerdict.Loud => $"громко: пики {Dbfs(Math.Max(s.P95WindowPeakDbfs, -60))}, максимум {Dbfs(s.PeakDbfs)}",
        MicLevelVerdict.Quiet => $"тихо: пики {Dbfs(s.P95WindowPeakDbfs)}",
        _ => $"в норме: пики {Dbfs(s.P95WindowPeakDbfs)}",
    };

    /// <summary>«−24 дБFS».</summary>
    public static string Dbfs(double v) => AudioParsing.SignedNumber(Math.Max(v, -120), "0") + " дБFS";

    internal static string Plural(int n, string one, string few, string many)
    {
        var mod100 = n % 100;
        var mod10 = n % 10;
        if (mod100 is >= 11 and <= 14) return many;
        return mod10 == 1 ? one : mod10 is >= 2 and <= 4 ? few : many;
    }
}
