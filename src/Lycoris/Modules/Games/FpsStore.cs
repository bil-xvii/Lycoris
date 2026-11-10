using System.IO;
using System.Text;
using System.Text.Json;
using Lycoris.Core;
using Lycoris.Core.Security;

namespace Lycoris.Modules.Games;

/// <summary>The last «до» and «после» FPS captures. Local only, written only when the user measures.</summary>
public sealed record FpsPair(FpsRecord? Before, FpsRecord? After);

/// <summary>
/// <c>%LocalAppData%\Lycoris\fps-measurements.json</c>. The elevated Lycoris reads it too, so it is untrusted input:
/// <see cref="SafeIo"/> (size limit, no links), fixed types, every value range-checked (<see cref="Parse"/>, hostile cases in
/// <see cref="SecurityCorpus"/>). A bad record is dropped, never shown.
/// </summary>
public sealed class FpsStore
{
    public const int MaxBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 8 };
    private static readonly object Gate = new();
    private readonly string _path;

    public FpsStore() : this(Path.Combine(AppPaths.UserDir, "fps-measurements.json")) { }
    public FpsStore(string path) => _path = Path.GetFullPath(path);

    public FpsPair Read()
    {
        try
        {
            return File.Exists(_path) ? Parse(SafeIo.ReadBytes(_path, MaxBytes)) : new(null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(null, null);
        }
    }

    /// <summary>Saves a capture into its slot. A new «до» clears the old «после» (it belonged to the previous comparison).</summary>
    public FpsPair Save(FpsRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!IsValid(record)) throw new ArgumentException("Invalid FPS record.", nameof(record));
        lock (Gate)
        {
            var old = Read();
            var pair = record.Kind == FpsRecord.BeforeKind ? new FpsPair(record, null) : new FpsPair(old.Before, record);
            Write(pair);
            return pair;
        }
    }

    public void Clear()
    {
        lock (Gate) Write(new(null, null));
    }

    private void Write(FpsPair pair)
    {
        try
        {
            if (!AppPaths.TryEnsureDir(Path.GetDirectoryName(_path)!)) return;
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(pair, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Замер FPS: не удалось сохранить — {0}", ex.Message);
        }
    }

    /// <summary>Parses and validates the file content. Anything malformed gives empty slots. Pure.</summary>
    public static FpsPair Parse(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxBytes) return new(null, null);
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            var pair = JsonSerializer.Deserialize<FpsPair>(text, JsonOptions);
            if (pair is null) return new(null, null);
            var before = pair.Before is { } b && b.Kind == FpsRecord.BeforeKind && IsValid(b) ? b : null;
            var after = pair.After is { } a && a.Kind == FpsRecord.AfterKind && IsValid(a) ? a : null;
            return new(before, after);
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or NotSupportedException or ArgumentException)
        {
            return new(null, null);
        }
    }

    public static bool IsValid(FpsRecord r)
    {
        if (r.Kind is not (FpsRecord.BeforeKind or FpsRecord.AfterKind)) return false;
        if (FpsMeasure.NormalizeProcess(r.Process) != r.Process) return false;
        if (r.RequestedSeconds is < 1 or > 600) return false;
        if (r.RecordedAt < new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero) || r.RecordedAt > DateTimeOffset.UtcNow.AddDays(2)) return false;
        var s = r.Stats;
        static bool Ok(double v, double max) => double.IsFinite(v) && v > 0 && v <= max;
        return s is not null && s.Frames >= FpsMeasure.MinFrames && s.Frames <= FpsMeasure.MaxRows
            && Ok(s.Seconds, 3600) && Ok(s.AvgFps, 10_000) && Ok(s.Low1Fps, 10_000)
            && Ok(s.AvgFrameMs, FpsMeasure.MaxFrameMs) && Ok(s.P99FrameMs, FpsMeasure.MaxFrameMs);
    }
}
