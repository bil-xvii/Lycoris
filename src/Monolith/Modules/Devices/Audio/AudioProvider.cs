using System.Diagnostics;
using System.Globalization;
using Monolith.Core.Devices;

namespace Monolith.Modules.Devices.Audio;

/// <summary>A level test result kept for the app session under "mic:&lt;endpoint id&gt;" (absolute levels, never a delta).</summary>
internal sealed record MicMeasurement(float MeasuredAtScalar, double? MeasuredAtDb, MicLevelStats Stats, double? TargetDb, string Verdict, string? Detail)
{
    /// <summary>The tool that measured it (its result line is reset when the level changes).</summary>
    public DeviceTool? Tool { get; init; }

    /// <summary>The level moved since the test (in Windows, a hardware key, or Lycoris): the suggestion no longer applies.</summary>
    public bool IsStale(EndpointLevel live) =>
        MeasuredAtDb is { } at && live.Db is { } db ? Math.Abs(db - at) > 0.5 : Math.Abs(live.Scalar - MeasuredAtScalar) > 0.01f;
}

/// <summary>
/// The microphone level as a setting value: the percent the slider shows, plus the live dB ("db=5.6") so a recommendation in
/// dB compares within 0,5 dB. A recommendation carries an estimated percent ("db=15;est", shown «≈100 %»).
/// </summary>
internal static class MicLevelValue
{
    private const string Prefix = "db=";
    private const string EstimateSuffix = ";est";

    public static DeviceValue Of(float scalar, float? db) =>
        new((long)Math.Round(Math.Clamp(scalar, 0f, 1f) * 100), db is { } d && float.IsFinite(d) ? Prefix + d.ToString("0.##", CultureInfo.InvariantCulture) : null);

    public static DeviceValue Target(double db, long estimatedPercent) =>
        new(Math.Clamp(estimatedPercent, 0, 100), Prefix + db.ToString("0.##", CultureInfo.InvariantCulture) + EstimateSuffix);

    public static bool TryDb(DeviceValue v, out double db)
    {
        db = 0;
        if (v.Text is not { } t || !t.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var body = t[Prefix.Length..];
        if (body.EndsWith(EstimateSuffix, StringComparison.Ordinal)) body = body[..^EstimateSuffix.Length];
        return double.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out db) && double.IsFinite(db);
    }

    public static bool IsEstimate(DeviceValue v) => v.Text?.EndsWith(EstimateSuffix, StringComparison.Ordinal) == true;

    /// <summary>Two levels in dB within 0,5 dB; otherwise (a slider value has no dB) by percent.</summary>
    public static bool Same(DeviceValue a, DeviceValue b) =>
        TryDb(a, out var da) && TryDb(b, out var dbB) ? Math.Abs(da - dbB) <= 0.5 : a.Number == b.Number;

    public static string Format(DeviceValue v) =>
        (IsEstimate(v) ? "≈" : "") + v.Number.ToString(CultureInfo.InvariantCulture) + " %";

    /// <summary>
    /// The slider position of a dB target, estimated piecewise-linearly through the endpoint's range and its live point
    /// (the driver's scalar curve is not published). Only for display: the change itself is made in dB.
    /// </summary>
    public static long EstimatePercent(double targetDb, double currentDb, long currentPercent, double min, double max)
    {
        if (max <= min) return currentPercent;
        targetDb = Math.Clamp(targetDb, min, max);
        currentDb = Math.Clamp(currentDb, min, max);
        double pct;
        if (targetDb >= currentDb)
            pct = max - currentDb < 1e-6 ? 100 : currentPercent + (100 - currentPercent) * (targetDb - currentDb) / (max - currentDb);
        else
            pct = currentDb - min < 1e-6 ? 0 : currentPercent * (targetDb - min) / (currentDb - min);
        return (long)Math.Round(Math.Clamp(pct, 0, 100));
    }
}

/// <summary>
/// «Звук»: a card per active output (monitor HDMI/DP outputs only when they are a default — otherwise one note) and per
/// active microphone. Outputs are INFO-ONLY (default device, format, enhancements, spatial sound: explained and linked to the
/// exact Windows page, never written); call ducking is the Optimize tweak «audio-ducking» (applied from the plan through
/// TweakEngine, read-only here with «В ОПТИМИЗАЦИИ»). Microphones: level and mute through ChangeSession.SetAudioLevel (two
/// separate journal targets), plus the level test, which recommends a level only after a measurement.
/// </summary>
public sealed class AudioProvider : IDeviceProvider
{
    public const string DuckingTweakId = "audio-ducking";
    internal static readonly TimeSpan TestDuration = TimeSpan.FromSeconds(5);

    public string Name => Loc.T("звук");

    // Seams (tests replace them; the defaults read the real devices, read-only).
    internal Func<AudioSnapshot> ReadSnapshot { get; init; } = AudioInventory.Read;
    internal Func<string, EndpointLevel?> ReadLevel { get; init; } = AudioInventory.ReadLevel;
    internal Func<MicPrivacy> ReadPrivacy { get; init; } = AudioInventory.ReadPrivacy;
    internal Func<string, TimeSpan, IProgress<double>?, CancellationToken, Task<MicCapture>> Capture { get; init; } = MicLevelMeter.CaptureAsync;

    public Task<IReadOnlyList<DeviceCard>> DetectAsync(DeviceScanContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Build(ReadSnapshot(), ctx));
    }

    internal static string ResultKey(string endpointId) => "mic:" + endpointId;
    internal static string ToolId(string endpointId) => "mic-level:" + endpointId;
    internal static string CardKey(string endpointId) => "audio:" + endpointId;

    private static string PrivacyDenyText => Loc.T("Доступ к микрофону запрещён в параметрах конфиденциальности Windows — проверка недоступна.");
    private static string AccountText => Loc.T("Недоступно: Lycoris запущен не от вашей учётной записи (или её не удалось определить).");
    private static string TestStatus => Loc.T("Говорите обычным голосом 5 секунд… (Windows покажет значок микрофона)");
    private static string LevelChangedText => Loc.T("Уровень изменён — проверьте ещё раз.");

    // ───────────────────────────── Cards ─────────────────────────────

    internal IReadOnlyList<DeviceCard> Build(AudioSnapshot snap, DeviceScanContext ctx)
    {
        var issues = AudioAdvice.DefaultRolesIssues(snap.Endpoints);
        var render = snap.Endpoints.Where(e => e.Flow == AudioFlow.Render).ToList();
        var capture = snap.Endpoints.Where(e => e.Flow == AudioFlow.Capture).ToList();

        // A monitor output is a card only when Windows actually uses it; otherwise it is one line on the main output.
        var outputs = render.Where(e => !e.IsDisplayAudio || e.IsDefaultAny)
            .OrderByDescending(e => e.IsDefault).ThenByDescending(e => e.IsDefaultCommunications)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var unusedDisplays = render.Where(e => e.IsDisplayAudio && !e.IsDefaultAny).ToList();
        var mics = capture
            .OrderByDescending(e => e.IsDefaultCommunications).ThenByDescending(e => e.IsDefault)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var mainOutput = outputs.FirstOrDefault();
        var mainMic = mics.FirstOrDefault(e => e.IsDefaultCommunications) ?? mics.FirstOrDefault(e => e.IsDefault) ?? mics.FirstOrDefault();

        var cards = new List<DeviceCard>();
        for (var i = 0; i < outputs.Count; i++)
        {
            var e = outputs[i];
            var isMain = ReferenceEquals(e, mainOutput);
            cards.Add(OutputCard(e, 50 + Math.Min(i, 9), isMain, snap, issues, isMain ? unusedDisplays : []));
        }
        for (var i = 0; i < mics.Count; i++)
        {
            var e = mics[i];
            var isMain = ReferenceEquals(e, mainMic);
            // No output card at all: the ducking row goes to the main microphone.
            cards.Add(MicCard(e, 60 + Math.Min(i, 9), isMain, isMain && mainOutput is null, snap, issues, ctx));
        }
        return cards;
    }

    private static DeviceCard OutputCard(EndpointInfo e, int order, bool isMain, AudioSnapshot snap, IReadOnlyList<AudioIssue> issues,
        IReadOnlyList<EndpointInfo> unusedDisplays)
    {
        var settings = new List<DeviceSetting>
        {
            DefaultRow(e, issues),
            FormatRow(e),
        };
        if (EnhancementsRow(e) is { } fx) settings.Add(fx);
        if (SpatialRow(e) is { } spatial) settings.Add(spatial);
        if (isMain) settings.Add(DuckingRow(snap.DuckingPreference));

        var facts = new List<string>();
        if (AudioParsing.ConnectionText(e) is { } connection) facts.Add(connection);
        if (e.Format is { } format) facts.Add(AudioParsing.ChannelsText(format.Channels));
        if (e.Scalar is { } volume) facts.Add(Loc.Tf("громкость {0} %", Math.Round(volume * 100).ToString("0", CultureInfo.InvariantCulture)));
        if (e.Mute == true) facts.Add(Loc.T("звук выключен"));

        var notes = new List<DeviceNote>();
        if (unusedDisplays.Count > 0)
            notes.Add(new DeviceNote(
                Loc.Tf("Выходы мониторов не используются: {0}. Убрать их из списка — «Не разрешать» в параметрах звука.", string.Join(", ", unusedDisplays.Select(d => AudioParsing.DisplayName(d.Name)))),
                new DeviceLink(DeviceLinkKind.Settings, Loc.T("ПАРАМЕТРЫ ЗВУКА"), "ms-settings:sound")));
        if (isMain && snap.MonoOn)
            notes.Add(new DeviceNote(Loc.T("Включён монофонический звук: в играх пропадает направление звука. Нужен только при слухе на одно ухо."),
                new DeviceLink(DeviceLinkKind.Settings, Loc.T("ПАРАМЕТРЫ"), "ms-settings:easeofaccess-audio")));

        return new DeviceCard
        {
            Key = CardKey(e.Id), Kind = DeviceKind.AudioOutput, Title = e.Name, Subtitle = RoleTags(e), Order = order,
            Hardware = HardwareOf(e), Settings = [.. settings], Facts = facts, Notes = notes,
        };
    }

    private DeviceCard MicCard(EndpointInfo e, int order, bool isMain, bool withDucking, AudioSnapshot snap, IReadOnlyList<AudioIssue> issues,
        DeviceScanContext ctx)
    {
        var deny = snap.Privacy == MicPrivacy.Deny;
        var settings = LevelRows(e, isMain, deny, ctx);
        settings.Add(DefaultRow(e, issues));
        settings.Add(FormatRow(e));
        if (EnhancementsRow(e) is { } fx) settings.Add(fx);
        if (withDucking) settings.Add(DuckingRow(snap.DuckingPreference));

        var facts = new List<string>();
        if (AudioParsing.ConnectionText(e) is { } connection) facts.Add(connection);
        if (e.Format is { } format) facts.Add(AudioParsing.ChannelsText(format.Channels));
        if (e.HardwareSupport is { } hw && (hw & CoreAudio.HardwareVolume) != 0) facts.Add(Loc.T("уровень меняется в самом микрофоне"));

        var notes = new List<DeviceNote>();
        if (deny && isMain)
            notes.Add(new DeviceNote(Loc.T("Доступ к микрофону запрещён в параметрах Windows — Discord, игры и проверка уровня вас не слышат."),
                new DeviceLink(DeviceLinkKind.Settings, Loc.T("КОНФИДЕНЦИАЛЬНОСТЬ"), "ms-settings:privacy-microphone")));

        return new DeviceCard
        {
            Key = CardKey(e.Id), Kind = DeviceKind.AudioInput, Title = e.Name, Subtitle = RoleTags(e), Order = order,
            Hardware = HardwareOf(e), Settings = [.. settings], Facts = facts, Notes = notes,
        };
    }

    private static List<DeviceHardwareRef> HardwareOf(EndpointInfo e) =>
        e.Vid is null && e.ContainerId is null && e.InstanceId is null
            ? []
            : [new DeviceHardwareRef(e.Vid, e.Pid, e.ContainerId, e.InstanceId is { } id ? [id] : [])];

    /// <summary>«по умолчанию · связь».</summary>
    internal static string? RoleTags(EndpointInfo e)
    {
        var tags = new List<string>();
        if (e.IsDefault) tags.Add(Loc.T("по умолчанию"));
        if (e.IsDefaultCommunications) tags.Add(Loc.T("связь"));
        return tags.Count == 0 ? null : string.Join(Loc.T(" · "), tags);
    }

    // ───────────────────────────── Info rows (INFO-ONLY: explained and linked, never written) ─────────────────────────────

    private static DeviceSetting Info(string id, string title, string hint, string value, int order, string? advice, DeviceLink? adviceLink,
        DeviceLink? link = null) => new()
    {
        Id = id, Title = title, Hint = hint, Control = SettingControl.Info, Order = order,
        Current = DeviceValue.Of(value), Format = v => v.Text ?? v.Key,
        Link = advice is not null ? adviceLink ?? link : link,
        Recommended = advice is not null ? new Recommendation(DeviceValue.Of(value), advice, RecommendPolicy.Advice, adviceLink ?? link) : null,
    };

    private static DeviceLink? DeviceSettingsLink(EndpointInfo e) =>
        DeviceTargets.IsEndpointId(e.Id)
            ? new DeviceLink(DeviceLinkKind.Settings, Loc.T("ПАРАМЕТРЫ УСТРОЙСТВА"), "ms-settings:sound-properties?endpointId=" + Uri.EscapeDataString(e.Id))
            : null;

    private static DeviceSetting DefaultRow(EndpointInfo e, IReadOnlyList<AudioIssue> issues)
    {
        var value = (e.IsDefault, e.IsDefaultCommunications) switch
        {
            (true, true) => Loc.T("да, и для связи"),
            (true, false) => Loc.T("да"),
            (false, true) => Loc.T("только для связи"),
            _ => Loc.T("нет"),
        };
        var issue = issues.FirstOrDefault(i => string.Equals(i.EndpointId, e.Id, StringComparison.OrdinalIgnoreCase));
        var hint = e.Flow == AudioFlow.Render
            ? Loc.T("Куда Windows выводит игры (по умолчанию) и голосовой чат (связь)")
            : Loc.T("С какого микрофона записывают игры (по умолчанию) и голосовой чат (связь)");
        var link = issue is null ? null : new DeviceLink(DeviceLinkKind.SoundPanel, Loc.T("ПАНЕЛЬ ЗВУКА"), issue.SoundPanelTab.ToString(CultureInfo.InvariantCulture));
        return Info("audio-default", Loc.T("Устройство по умолчанию"), hint, value, 30, issue?.Text, link);
    }

    private static DeviceSetting FormatRow(EndpointInfo e) =>
        Info("audio-format", Loc.T("Формат"), Loc.T("Частота и разрядность, в которых Windows передаёт звук устройству"),
            e.Format is { } f ? AudioParsing.RateBitsText(f) : Loc.T("—"), 40, AudioAdvice.FormatAdvice(e.Format), null, DeviceSettingsLink(e));

    private static DeviceSetting? EnhancementsRow(EndpointInfo e)
    {
        if (e.EnhancementsOff is not { } off) return null; // no effects on this endpoint (or unreadable): nothing to show
        var advice = AudioAdvice.EnhancementsAdvice(e, AudioAdvice.HasVendorProcessing(e));
        return Info("audio-enhancements", Loc.T("Улучшения звука"),
            e.Flow == AudioFlow.Render ? Loc.T("Эффекты Windows и драйвера: эквалайзер, объёмный звук")
                                       : Loc.T("Эффекты записи: шумоподавление, Voice Clarity и другие"),
            off ? Loc.T("выкл") : Loc.T("вкл"), 50, advice, advice is null ? null : DeviceSettingsLink(e));
    }

    private static DeviceSetting? SpatialRow(EndpointInfo e)
    {
        if (e.SpatialSupported != true || e.Spatial is not { } state) return null;
        var advice = AudioAdvice.SpatialAdvice(e, AudioAdvice.HasVendorProcessing(e));
        var value = state switch
        {
            SpatialState.Off => Loc.T("выкл"),
            SpatialState.WindowsSonic => "Windows Sonic",
            _ => Loc.T("другой формат"),
        };
        return Info("audio-spatial", Loc.T("Пространственный звук"), Loc.T("Объёмный звук для наушников: Windows Sonic, Dolby Atmos, DTS"),
            value, 60, advice, advice is null ? null : DeviceSettingsLink(e));
    }

    /// <summary>
    /// «Приглушение во время звонков» is the Optimize tweak «audio-ducking» (research: Windows settings as tweaks live on
    /// Optimize): the plan applies it through TweakEngine with its own journal source, ВРУЧНУЮ shows it read-only.
    /// </summary>
    internal static DeviceSetting DuckingRow(int? preference) => new()
    {
        Id = "tweak:" + DuckingTweakId,
        Title = Loc.T("Приглушение во время звонков"),
        Hint = Loc.T("Игровой звук не будет приглушаться, когда открыт голосовой чат"),
        Control = SettingControl.Info,
        TweakId = DuckingTweakId,
        TweakPolicy = AudioAdvice.DuckingPolicy,
        Order = 70,
        Format = v => v.Number != 0 ? AudioAdvice.DuckingShort(AudioAdvice.DuckingRecommended) : AudioAdvice.DuckingShort(preference),
        AppliesWhen = Loc.T("ВЫХОД ИЗ СИСТЕМЫ"),
        Link = new DeviceLink(DeviceLinkKind.SoundPanel, Loc.T("ВКЛАДКА «СВЯЗЬ»"), "3"),
    };

    // ───────────────────────────── Microphone: level, mute, level test ─────────────────────────────

    private List<DeviceSetting> LevelRows(EndpointInfo e, bool isMain, bool privacyDeny, DeviceScanContext ctx)
    {
        var results = ctx.Results;
        var key = ResultKey(e.Id);
        var cardKey = CardKey(e.Id);
        EndpointLevel? live = e.Scalar is { } sc && e.Mute is { } mu ? new EndpointLevel(sc, e.Db, e.DbMin, e.DbMax, mu, e.HardwareSupport) : null;
        var accountProblem = ctx.IsInteractiveUser ? null : AccountText;

        // A cached measurement whose level has moved since is dropped: its suggestion would repeat the old delta.
        var measurement = results.Get<MicMeasurement>(key);
        var stale = false;
        if (measurement is not null && (live is null || measurement.IsStale(live)))
        {
            results.Remove(key);
            stale = true;
            var oldTool = measurement.Tool;
            if (oldTool is not null) DeviceUi.Post(() => SetToolResult(oldTool, LevelChangedText, null));
            measurement = null;
        }

        DeviceSetting level = null!;
        var tool = new DeviceTool
        {
            Id = ToolId(e.Id),
            Title = Loc.T("Проверка уровня"),
            ButtonText = Loc.T("ПРОВЕРИТЬ"),
            Description = privacyDeny
                ? Loc.T("Недоступно: доступ к микрофону запрещён в параметрах Windows")
                : Loc.T("5 секунд обычной речи: пики, перегруз и подходящий уровень. Ничего не меняет"),
            Run = (run, ct) => RunTestAsync(run, ct, e, level, isMain),
        };
        if (measurement is not null)
        {
            tool.ResultText = Capitalize(measurement.Verdict);
            tool.ResultDetail = measurement.Detail;
        }
        else if (stale)
        {
            tool.ResultText = LevelChangedText;
        }

        var rangeText = e.DbMin is { } min && e.DbMax is { } max ? AudioParsing.RangeText(min, max) : null;
        level = new DeviceSetting
        {
            Id = "mic-level",
            Title = Loc.T("Уровень микрофона"),
            Hint = rangeText is null
                ? Loc.T("Чувствительность записи: подберите её по проверке уровня ниже")
                : Loc.Tf("Чувствительность записи ({0}): подберите по проверке ниже", rangeText),
            Control = SettingControl.Slider,
            Min = 0,
            Max = 100,
            Step = 1,
            Format = MicLevelValue.Format,
            Same = MicLevelValue.Same,
            JournalTarget = DeviceTargets.AudioLevel(e.Id),
            PerUser = true,
            Order = 10,
            Tool = tool,
            Current = live is null ? null : MicLevelValue.Of(live.Scalar, live.Db),
            Recommended = LevelRecommendation(live, measurement, isMain, stale, cardKey, tool.Id),
            DisabledReason = accountProblem ?? (live is null ? Loc.T("Windows не сообщает уровень этого микрофона.") : null),
            ReadCurrent = () => ReadLevel(e.Id) is { } l ? MicLevelValue.Of(l.Scalar, l.Db) : null,
            Apply = (s, v, ct) =>
            {
                var title = Loc.Tf("Уровень микрофона — {0}", e.Name);
                var before = s.Applied;
                var ok = MicLevelValue.TryDb(v, out var db)
                    ? s.SetAudioLevel(e.Id, e.Name, null, (float)db, null, DeviceTargets.Source, title)
                    : s.SetAudioLevel(e.Id, e.Name, Math.Clamp(v.Number, 0, 100) / 100f, null, null, DeviceTargets.Source, title);
                if (!ok) return Task.FromResult(ApplyOutcome.Failed);
                if (s.Applied == before) return Task.FromResult(ApplyOutcome.NoChange);
                // The measurement was made at the old level: drop it (no stale suggestion), ask for a new test.
                results.Remove(key);
                var again = isMain ? MeasureFirst(LevelChangedText, null, cardKey, tool.Id) : null;
                DeviceUi.Post(() =>
                {
                    SetToolResult(tool, LevelChangedText, null);
                    level.Recommended = again;
                });
                return Task.FromResult(ApplyOutcome.Applied);
            },
        };

        var mute = new DeviceSetting
        {
            Id = "mic-mute",
            Title = Loc.T("Без звука"),
            Hint = Loc.T("Выключает микрофон в Windows: вас не слышно ни в одной программе"),
            Control = SettingControl.Toggle,
            JournalTarget = DeviceTargets.AudioMute(e.Id),
            PerUser = true,
            Order = 20,
            Current = live is null ? null : DeviceValue.Of(live.Mute),
            DisabledReason = accountProblem ?? (live is null ? Loc.T("Windows не сообщает состояние этого микрофона.") : null),
            ReadCurrent = () => ReadLevel(e.Id) is { } l ? DeviceValue.Of(l.Mute) : null,
            Apply = (s, v, ct) =>
            {
                var before = s.Applied;
                var ok = s.SetAudioLevel(e.Id, e.Name, null, null, v.Number != 0, DeviceTargets.Source, Loc.Tf("Отключение микрофона — {0}", e.Name));
                return Task.FromResult(!ok ? ApplyOutcome.Failed : s.Applied == before ? ApplyOutcome.NoChange : ApplyOutcome.Applied);
            },
        };
        return [level, mute];
    }

    private static void SetToolResult(DeviceTool tool, string? result, string? detail)
    {
        if (tool.IsRunning) return; // a new test is under way: its own result follows
        tool.ResultText = result;
        tool.ResultDetail = detail;
    }

    /// <summary>The main microphone asks for a measurement first: the plan says «НУЖНО ИЗМЕРЕНИЕ» and links the tool.</summary>
    private static Recommendation MeasureFirst(string reason, DeviceValue? current, string cardKey, string toolId) =>
        new(current ?? DeviceValue.Of(0), reason, RecommendPolicy.Suggest, new DeviceLink(DeviceLinkKind.Tool, Loc.T("К ПРОВЕРКЕ"), cardKey, toolId))
        {
            NeedsMeasurement = true,
        };

    /// <summary>
    /// No measurement → the main microphone needs one (others: nothing). A fresh measurement → the absolute dB target as a
    /// Suggest (or the current level when it is fine). A level at the end of its range, or too little speech, recommends nothing.
    /// </summary>
    internal static Recommendation? LevelRecommendation(EndpointLevel? live, MicMeasurement? m, bool isMain, bool stale, string cardKey, string toolId)
    {
        if (live is null) return null;
        var current = MicLevelValue.Of(live.Scalar, live.Db);
        if (m is null)
            return isMain
                ? MeasureFirst(stale ? Loc.T("Уровень изменён после проверки — проверьте ещё раз.") : Loc.T("Проверка уровня покажет, слышно ли вас без перегруза: 5 секунд речи."),
                    current, cardKey, toolId)
                : null;

        if (m.TargetDb is { } target && live.Db is { } db && live.DbMin is { } min && live.DbMax is { } max)
            return new Recommendation(
                MicLevelValue.Target(target, MicLevelValue.EstimatePercent(target, db, current.Number, min, max)),
                Loc.Tf("Пики речи будут около −9 дБFS, без перегруза: {0} → {1}.", AudioParsing.DbText(db), AudioParsing.DbText(target)),
                RecommendPolicy.Suggest);

        return MicLevelAnalysis.Classify(m.Stats) switch
        {
            MicLevelVerdict.Ok => new Recommendation(current, Loc.T("По проверке уровень в норме."), RecommendPolicy.Suggest),
            MicLevelVerdict.NoSpeech when isMain => MeasureFirst(Loc.T("В прошлой проверке было мало речи — проверьте ещё раз."), current, cardKey, toolId),
            _ => null, // the level is already at the end of its range: the tool's result says what to do instead
        };
    }

    /// <summary>The verdict, the suggestion (absolute dB) and what to do, from a test at the level <paramref name="at"/>.</summary>
    internal static MicMeasurement Measure(EndpointLevel at, MicLevelStats stats)
    {
        double? target = at.Db is { } db && at.DbMin is { } min && at.DbMax is { } max ? MicLevelAnalysis.SuggestDb(db, stats, min, max) : null;
        var verdict = MicLevelAnalysis.Verdict(stats);
        var kind = MicLevelAnalysis.Classify(stats);
        string? detail;
        if (target is { } t && at.Db is { } now)
        {
            detail = Loc.Tf("Рекомендуемый уровень {0} (сейчас {1}) — ползунок уже на нём, нажмите «ПРИМЕНИТЬ». Потом проверьте ещё раз.", AudioParsing.DbText(t), AudioParsing.DbText(now));
            if (at.DbMax is { } top && t >= top - 0.01 && t - now < MicLevelAnalysis.TargetDbfs - stats.P95WindowPeakDbfs - 3)
                detail += " " + Loc.T("Даже на максимуме будет тихо — придвиньте микрофон.");
        }
        else
        {
            detail = kind switch
            {
                MicLevelVerdict.Ok => Loc.T("Менять ничего не нужно."),
                MicLevelVerdict.NoSpeech => Loc.T("Проверьте ещё раз и говорите всё время, пока идёт проверка."),
                MicLevelVerdict.Quiet => at.Db is null ? Loc.T("Windows не сообщает уровень в децибелах — прибавьте уровень ползунком и проверьте ещё раз.")
                    : Loc.T("Уровень уже максимальный — придвиньте микрофон."),
                _ => at.Db is null ? Loc.T("Windows не сообщает уровень в децибелах — убавьте уровень ползунком и проверьте ещё раз.")
                    : Loc.T("Уровень уже минимальный — отодвиньте микрофон или говорите тише."),
            };
        }
        return new MicMeasurement(at.Scalar, at.Db, stats, target, verdict, detail);
    }

    /// <summary>
    /// ПРОВЕРИТЬ: a 5-second shared-mode capture (no system change), then the verdict, the cached result and — when a
    /// change helps — the recommended level, also staged on the slider (ПРИМЕНИТЬ applies it through the journal).
    /// </summary>
    private async Task RunTestAsync(DeviceToolRun run, CancellationToken ct, EndpointInfo e, DeviceSetting level, bool isMain)
    {
        if (ReadPrivacy() == MicPrivacy.Deny)
        {
            run.Fail(PrivacyDenyText);
            return;
        }
        var before = await Task.Run(() => ReadLevel(e.Id), ct);
        if (before is null)
        {
            run.Fail(Loc.T("Микрофон не найден или отключён — подключите его и нажмите «ОБНОВИТЬ»."));
            return;
        }
        if (before.Mute)
        {
            run.Fail(Loc.T("Микрофон выключен («Без звука») — включите его и проверьте снова."));
            return;
        }

        run.Report(0, TestStatus, 0);
        var watch = Stopwatch.StartNew();
        var progress = new Progress<double>(peak => run.Report(watch.Elapsed.TotalSeconds / TestDuration.TotalSeconds, TestStatus, peak));
        MicCapture captured;
        try
        {
            captured = await Capture(e.Id, TestDuration, progress, ct);
        }
        catch (InvalidOperationException ex)
        {
            run.Fail(ex.Message);
            return;
        }
        var stats = await Task.Run(() => MicLevelAnalysis.Analyze(captured.Samples, captured.SampleRate), ct);
        var measurement = Measure(before, stats) with { Tool = run.Tool };
        run.Results.Set(ResultKey(e.Id), measurement);
        run.Complete(Capitalize(measurement.Verdict), measurement.Detail);

        var rec = LevelRecommendation(before, measurement, isMain, false, run.Card.Key, run.Tool.Id);
        DeviceUi.Post(() =>
        {
            level.Recommended = rec;
            if (measurement.TargetDb is not null && rec is { Policy: RecommendPolicy.Suggest, NeedsMeasurement: false } r) level.Staged = r.Value;
        });
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..];
}
