namespace Lycoris.Modules.Devices.Audio;

/// <summary>A finding about one endpoint: shown on its card and in the АВТОНАСТРОЙКА findings, with the sound panel tab to open.</summary>
public sealed record AudioIssue(string EndpointId, string Text, int SoundPanelTab);

/// <summary>
/// What Lycoris recommends for audio (research devices_audio.md, binding). Everything here is INFO-ONLY except the
/// ducking preference (the Optimize tweak «audio-ducking») and the microphone level: Lycoris explains and opens the exact
/// Windows page, it never writes the default device, the format, enhancements or spatial sound (IPolicyConfig and
/// FxProperties writes are DO-NOT-INCLUDE). Pure and unit-tested.
/// </summary>
public static class AudioAdvice
{
    /// <summary>
    /// The default-device findings: render roles split between devices (a chat «plays from the monitor»), a monitor
    /// (HDMI/DP) as the default output while headphones are connected, a Bluetooth headset in its hands-free profile as the
    /// default output, and capture roles split between microphones. Flag, never force: it may be intentional.
    /// </summary>
    public static IReadOnlyList<AudioIssue> DefaultRolesIssues(IReadOnlyList<EndpointInfo> endpoints)
    {
        var issues = new List<AudioIssue>();
        var render = endpoints.Where(e => e.Flow == AudioFlow.Render).ToList();
        var capture = endpoints.Where(e => e.Flow == AudioFlow.Capture).ToList();

        var console = render.FirstOrDefault(e => e.IsDefaultConsole) ?? render.FirstOrDefault(e => e.IsDefaultMultimedia);
        var comms = render.FirstOrDefault(e => e.IsDefaultCommunications);
        var headphones = render.FirstOrDefault(e => e.IsHeadphones && !e.IsDisplayAudio);

        // The monitor finding is the more specific one: it replaces the «split» one when both apply.
        if (console is { IsDisplayAudio: true } && headphones is not null)
            issues.Add(new AudioIssue(headphones.Id,
                Loc.Tf("По умолчанию звук идёт на монитор «{0}», хотя подключены «{1}».", Short(console), Short(headphones)), 0));
        else if (console is not null && comms is not null && !SameId(console, comms))
            issues.Add(new AudioIssue(console.Id,
                Loc.Tf("Игры звучат через «{0}», а голосовой чат — через «{1}». Если так не задумано, назначьте одно устройство и по умолчанию, и для связи.", Short(console), Short(comms)), 0));

        if (console is { IsBluetoothHandsFree: true })
            issues.Add(new AudioIssue(console.Id,
                Loc.T("Гарнитура Bluetooth выбрана в режиме разговора — звук в низком качестве. Выберите по умолчанию её стереовыход."), 0));

        var micConsole = capture.FirstOrDefault(e => e.IsDefaultConsole) ?? capture.FirstOrDefault(e => e.IsDefaultMultimedia);
        var micComms = capture.FirstOrDefault(e => e.IsDefaultCommunications);
        if (micConsole is not null && micComms is not null && !SameId(micConsole, micComms))
            issues.Add(new AudioIssue(micComms.Id,
                Loc.Tf("Голосовой чат записывает с «{0}», а игры и другие программы — с «{1}».", Short(micComms), Short(micConsole)), 1));

        return issues;
    }

    private static bool SameId(EndpointInfo a, EndpointInfo b) => string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase);

    private static string Short(EndpointInfo e) => e.IsDisplayAudio ? AudioParsing.DisplayName(e.Name) : e.Name;

    /// <summary>
    /// 48 kHz is what games and voice chats produce (Discord: Opus at 48 kHz): fine, nothing to say. 44,1 kHz adds a
    /// resampling step; above 48 kHz gives nothing in games. Never recommends 96/192 kHz (DO-NOT-INCLUDE as a «boost»).
    /// </summary>
    public static string? FormatAdvice(AudioFormat? f) => f?.SampleRate switch
    {
        null => null,
        48000 => null,
        44100 => Loc.T("Игры и голосовые чаты работают в 48 кГц — выберите 48 кГц в свойствах устройства, чтобы звук не пересчитывался."),
        > 48000 => Loc.T("Игры и голосовые чаты выводят 48 кГц — более высокая частота ничего не добавляет. Достаточно 48 кГц."),
        _ => null,
    };

    /// <summary>
    /// Gaming headsets whose vendor software does its own processing (EQ, virtual surround): Sony INZONE Hub, Razer Synapse,
    /// SteelSeries GG, Logitech G HUB, Corsair iCUE, HyperX NGENUITY.
    /// </summary>
    private static readonly HashSet<string> VendorDspVids = new(StringComparer.OrdinalIgnoreCase) { "054C", "1532", "1038", "046D", "1B1C", "0951", "03F0" };

    public static bool HasVendorProcessing(EndpointInfo e) => e.Vid is { } vid && VendorDspVids.Contains(vid);

    /// <summary>Windows effects stacked on the vendor's own processing (output only; a microphone is left as the user likes it).</summary>
    public static string? EnhancementsAdvice(EndpointInfo e, bool vendorDsp) =>
        e.Flow == AudioFlow.Render && e.EnhancementsOff == false && vendorDsp
            ? Loc.T("Если эквалайзер или объёмный звук настроены в программе производителя, выключите улучшения Windows — иначе обработка двойная.")
            : null;

    /// <summary>Windows spatial sound on top of the headset's own virtual surround: HRTF applied twice.</summary>
    public static string? SpatialAdvice(EndpointInfo e, bool hasVendorSpatial) =>
        e.Flow == AudioFlow.Render && e.Spatial is SpatialState.WindowsSonic or SpatialState.Other && hasVendorSpatial
            ? Loc.T("Пространственный звук Windows включён поверх объёмного звука гарнитуры — оставьте что-то одно, иначе направление искажается.")
            : null;

    // ───────────────────────────── Ducking (the Optimize tweak «audio-ducking») ─────────────────────────────

    public const int DuckingDefault = 1;
    public const int DuckingRecommended = 3;
    public const RecommendPolicy DuckingPolicy = RecommendPolicy.Suggest;
    public static string DuckingReason => Loc.T("игровой звук не будет приглушаться, когда открыт голосовой чат");

    /// <summary>The four choices of mmsys.cpl → «Связь», in the dialog's order (UserDuckingPreference 0–3).</summary>
    public static IReadOnlyList<DeviceOption> DuckingOptions { get; } =
    [
        new(DeviceValue.Of(0), Loc.T("Отключать все другие звуки")),
        new(DeviceValue.Of(1), Loc.T("Уменьшать громкость на 80 %")),
        new(DeviceValue.Of(2), Loc.T("Уменьшать громкость на 50 %")),
        new(DeviceValue.Of(3), Loc.T("Ничего не делать")),
    ];

    /// <summary>The live preference in a few words («тише на 80 %»); absent = the Windows default.</summary>
    public static string DuckingShort(int? preference) => (preference ?? DuckingDefault) switch
    {
        0 => Loc.T("выключать другие звуки"),
        1 => Loc.T("тише на 80 %"),
        2 => Loc.T("тише на 50 %"),
        3 => Loc.T("ничего не делать"),
        _ => Loc.T("нестандартное значение"),
    };
}
