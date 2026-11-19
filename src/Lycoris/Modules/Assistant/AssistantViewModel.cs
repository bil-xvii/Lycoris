using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Core.Assistant;
using Lycoris.Core.Diagnostics;
using Lycoris.Modules.Dashboard;
using Lycoris.Modules.Devices.Network;
using Lycoris.Modules.Services;
using Lycoris.Shell;
using Lycoris.UI;

namespace Lycoris.Modules.Assistant;

/// <summary>Where the local model stands: shown as one tag and one line of text under the header.</summary>
public enum AssistantState { Checking, NoOllama, NoModels, Ready }

/// <summary>One message of the conversation. The assistant's text grows while the answer streams in.</summary>
public sealed partial class AssistantMessage : ObservableObject
{
    public bool IsUser { get; init; }
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string? _note;
    /// <summary>Places in Lycoris the answer points to: each is a labelled «ОТКРЫТЬ» button (navigation only, never a change).</summary>
    public ObservableCollection<PaletteEntry> Actions { get; } = [];
    public string Author => IsUser ? Loc.T("ВЫ") : Loc.T("ПОМОЩНИК");
}

/// <summary>
/// «Помощник»: health checks, maintenance navigation, and answers from a local model.
/// The model can point to places in Lycoris. The diagnostic cards offer selected fixes through the dashboard's own workflow.
/// </summary>
public sealed partial class AssistantViewModel : PageViewModel
{
    private readonly Func<Task<HealthReportData?>> _refreshContext;
    private readonly Func<LocalModelClient> _createClient;
    private readonly Func<string?> _runningGame;
    private readonly Func<IReadOnlyList<PaletteEntry>> _getPlaces;
    private CancellationTokenSource? _cts;

    public AssistantViewModel(DashboardViewModel dashboard, ServicesViewModel services)
        : this(dashboard.RefreshForAssistantAsync, dashboard.FixFromAssistantAsync, OpenLocation, dashboard.RefreshAssistantContextAsync,
            services.ExecuteAssistantServiceActionAsync) { }

    internal AssistantViewModel(Func<Task<AssistantDiagnosticSnapshot?>> refreshDiagnostics,
        Func<string, Func<string, bool>, Task<AssistantFixResult>> fixDiagnostic, Action<string, string?> open,
        Func<Task<HealthReportData?>> refreshContext,
        Func<ServiceActionRequest, CancellationToken, Task<ServiceActionResult>>? executeServiceAction = null,
        Func<LocalModelClient>? createClient = null, Func<string?>? runningGame = null,
        Func<IReadOnlyList<PaletteEntry>>? getPlaces = null)
    {
        _refreshDiagnostics = refreshDiagnostics;
        _fixDiagnostic = fixDiagnostic;
        _open = open;
        _refreshContext = refreshContext;
        _executeServiceAction = executeServiceAction;
        _createClient = createClient ?? (() => new LocalModelClient());
        _runningGame = runningGame ?? NetGames.RunningGame;
        _getPlaces = getPlaces ?? GetShellPlaces;
    }

    public override string Key => "assistant";
    public override string Index => "12";
    public override string Title => Loc.T("Помощник");
    public override string Subtitle => Loc.T("Задавайте вопросы о ПК, проверяйте результаты диагностики и открывайте разделы обслуживания.");
    public override string Glyph => Glyphs.Assistant;

    /// <summary>QA only: «--snapshot … --demo-assistant» fills a sample conversation (see <see cref="AssistantDemo"/>).</summary>
    public static bool Demo { get; set; }

    public ObservableCollection<AssistantMessage> Messages { get; } = [];
    public ObservableCollection<string> Models { get; } = [];

    [ObservableProperty] private AssistantState _state = AssistantState.Checking;
    [ObservableProperty] private string _question = "";
    [ObservableProperty] private bool _isAnswering;
    [ObservableProperty] private string? _selectedModel;
    [ObservableProperty] private bool _isChatOpen = true;

    public bool IsReady => State == AssistantState.Ready;
    public bool NeedsOllama => State == AssistantState.NoOllama;
    public bool NeedsModel => State == AssistantState.NoModels;
    public bool HasMessages => Messages.Count > 0;

    public string StateTag => State switch
    {
        AssistantState.Ready => Loc.T("ЛОКАЛЬНО"),
        AssistantState.NoOllama => Loc.T("OLLAMA НЕ НАЙДЕН"),
        AssistantState.NoModels => Loc.T("НЕТ МОДЕЛИ"),
        _ => Loc.T("ПРОВЕРКА"),
    };

    public string StateText => State switch
    {
        AssistantState.Ready => Loc.T("Ответы генерируются на этом ПК. Во время игры доступны подсказки Lycoris и диагностика."),
        AssistantState.NoOllama => Loc.T("Для чата установите Ollama в разделе «Приложения» и нажмите «Проверить модель»."),
        AssistantState.NoModels => Loc.T("Ollama подключён. Скачайте локальную модель в Ollama и нажмите «Проверить модель»."),
        _ => Loc.T("Ищем Ollama на этом ПК…"),
    };

    private static string ExplainProblemsQuestion => Loc.T("Объясни найденные проблемы");

    /// <summary>Ready-made questions: one click asks them.</summary>
    public IReadOnlyList<string> QuickQuestions { get; } =
    [
        Loc.T("Почему ПК может тормозить?"),
        Loc.T("Что улучшить для игр?"),
        ExplainProblemsQuestion,
        Loc.T("Что Lycoris уже менял?"),
        Loc.T("Какие программы и приложения Microsoft Store можно удалить?"),
        Loc.T("Какие службы стоит проверить?"),
        Loc.T("Что можно убрать из автозапуска?"),
        Loc.T("Как проверить временные файлы?"),
    ];

    partial void OnStateChanged(AssistantState value)
    {
        OnPropertyChanged(nameof(IsReady)); OnPropertyChanged(nameof(NeedsOllama)); OnPropertyChanged(nameof(NeedsModel));
        OnPropertyChanged(nameof(StateTag)); OnPropertyChanged(nameof(StateText));
        AskCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedModelChanged(string? value)
    {
        if (value is null || !IsReady || !Models.Contains(value) || Demo || AppSettings.Current.AssistantModel == value) return;
        AppSettings.Current.AssistantModel = value;
        AppSettings.Current.Save();
    }

    partial void OnIsAnsweringChanged(bool value)
    {
        AskCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RunServiceActionCommand.NotifyCanExecuteChanged();
    }
    partial void OnQuestionChanged(string value) => AskCommand.NotifyCanExecuteChanged();

    protected override async Task LoadAsync()
    {
        if (Demo) { AssistantDemo.Fill(this); MarkChecked(); return; }
        await Task.WhenAll(RefreshDiagnosticsAsync(), CheckAsync());
    }

    protected override Task OnReturnAsync() => State == AssistantState.Ready || IsAnswering ? Task.CompletedTask : CheckAsync();

    /// <summary>«ОБНОВИТЬ»: is Ollama running, which models does it have.</summary>
    [RelayCommand]
    private async Task CheckAsync()
    {
        if (Demo) return;
        State = AssistantState.Checking;
        using var client = _createClient();
        var models = await client.ListModelsAsync();
        Models.Clear();
        if (models is null) { State = AssistantState.NoOllama; MarkChecked(); return; }
        foreach (var m in models.Where(AssistantBrain.IsChatModel)) Models.Add(m.Name);
        SelectedModel = AssistantBrain.PickModel(models, AppSettings.Current.AssistantModel);
        State = SelectedModel is null ? AssistantState.NoModels : AssistantState.Ready;
        MarkChecked();
    }

    /// <summary>Internal for the demo: shows the given state and models without asking Ollama.</summary>
    internal void ShowDemoState(IEnumerable<string> models, string selected)
    {
        Models.Clear();
        foreach (var m in models) Models.Add(m);
        SelectedModel = selected;
        State = AssistantState.Ready;
    }

    internal void AddMessage(AssistantMessage message) { Messages.Add(message); OnPropertyChanged(nameof(HasMessages)); ClearCommand.NotifyCanExecuteChanged(); }

    private static bool IsExplainProblemsPreset(string? preset) => string.Equals(preset, ExplainProblemsQuestion, StringComparison.Ordinal);

    private bool CanAsk(string? preset)
    {
        var input = preset ?? Question;
        return !IsAnswering && !string.IsNullOrWhiteSpace(input) &&
            (IsReady || IsExplainProblemsPreset(preset) || ServiceRequestParser.Parse(input).Kind != ServiceRequestKind.None ||
             AssistantMaintenanceIntent.Match(input).Count > 0);
    }

    [RelayCommand(CanExecute = nameof(CanAsk))]
    private async Task AskAsync(string? preset)
    {
        var input = preset ?? Question;
        if (!CanAsk(preset)) return;
        var explainProblems = IsExplainProblemsPreset(preset);
        var service = ServiceRequestParser.Parse(input);
        var model = IsReady && SelectedModel is { } selected && Models.Contains(selected) ? selected : null;
        var question = input.Trim();
        if (question.Length > 1000) question = question[..1000];
        Question = "";
        if (explainProblems)
            await AnswerAsync(question, ExplainProblemsAsync);
        else if (service.Kind == ServiceRequestKind.MissingTarget ||
            (service.Kind == ServiceRequestKind.Ready && !AssistantMaintenanceIntent.IsBareStartupRequest(input)))
            await AnswerAsync(question, (answer, ct) => AnswerServiceRequestAsync(answer, service, ct));
        else
        {
            var maintenance = AssistantMaintenanceIntent.Match(question);
            if (maintenance.Count == 0 && service.Kind == ServiceRequestKind.Invalid)
                await AnswerAsync(question, (answer, ct) => AnswerServiceRequestAsync(answer, service, ct));
            else
                await AnswerAsync(question, (answer, ct) => AnswerQuestionAsync(answer, question, model, maintenance, ct));
        }
    }

    private async Task AnswerAsync(string question, Func<AssistantMessage, CancellationToken, Task> respond)
    {
        AddMessage(new AssistantMessage { IsUser = true, Text = question });
        var answer = new AssistantMessage { IsStreaming = true, Text = Loc.T("Думаю…") };
        AddMessage(answer);
        IsAnswering = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            await respond(answer, ct);
        }
        catch (OperationCanceledException)
        {
            if (answer.Text == Loc.T("Думаю…")) answer.Text = "";
            answer.Note = Loc.T("Ответ остановлен.");
        }
        catch (HttpRequestException)
        {
            Log.Warn("Помощник: Ollama не ответил.");
            answer.Text = Loc.T("Ollama не ответил. Проверьте, что он запущен, и нажмите «Проверить модель».");
        }
        finally
        {
            answer.IsStreaming = false;
            IsAnswering = false;
            _cts?.Dispose(); _cts = null;
        }
    }

    private static IReadOnlyList<PaletteEntry> GetShellPlaces() =>
        Ui.Host is MainViewModel shell ? CommandPaletteIndex.Build(shell.Pages) : [];

    private async Task AnswerQuestionAsync(AssistantMessage answer, string question, string? model,
        IReadOnlyList<(string Page, string Guidance)> maintenance, CancellationToken ct)
    {
        var index = maintenance.Count > 0 ? _getPlaces() : [];
        foreach (var (page, _) in maintenance)
            if (index.FirstOrDefault(entry => entry.Kind == PaletteKind.Page && entry.PageKey == page && entry.FocusId is null) is { } place)
                answer.Actions.Add(place);

        var guidance = maintenance.Count == 0 ? null : string.Join("\n\n", maintenance.Select(intent => intent.Guidance));
        if (model is null)
        {
            answer.Text = guidance ?? (IsReady ? Loc.T("Выберите доступную локальную модель или нажмите «Проверить модель».") : StateText);
            return;
        }

        answer.Note = guidance;
        if (await PauseForGameAsync(answer, ct)) return;
        await AnswerWithModelAsync(answer, question, model, maintenance.Count > 0, ct);
    }

    private async Task<bool> PauseForGameAsync(AssistantMessage answer, CancellationToken ct)
    {
        if (await Task.Run(_runningGame, ct) is null) return false;
        answer.Text = Loc.T("Идёт игра. Помощник продолжит отвечать через локальную модель после завершения игры. Диагностика и подсказки по обслуживанию доступны сейчас.");
        return true;
    }

    private async Task AnswerWithModelAsync(AssistantMessage answer, string question, string model, bool isMaintenance, CancellationToken ct)
    {
        var pc = await _refreshContext().WaitAsync(ct);
        var issueTitles = pc?.Checks.Where(c => c.Status == "warn").Select(c => c.Title).Take(3) ?? [];
        var places = AssistantBrain.SelectPlaces(isMaintenance ? answer.Actions.ToArray() : _getPlaces(), question, issueTitles);
        var changes = ChangeJournal.Instance.Records.Where(r => !r.Reverted).OrderByDescending(r => r.TimestampUtc).Select(r => Loc.Sink(r.Title)).Distinct().Take(6).ToList();

        var turns = new List<ChatTurn> { new("system", AssistantBrain.SystemPrompt(Loc.IsEnglish) + "\n\n" + AssistantBrain.Context(pc, places, changes)) };
        // A short memory of the conversation (the last two exchanges), so «а подробнее?» works.
        foreach (var m in Messages.Take(Messages.Count - 2).TakeLast(4))
            turns.Add(new ChatTurn(m.IsUser ? "user" : "assistant", m.Text.Length <= 1600 ? m.Text : m.Text[..1600]));
        turns.Add(new ChatTurn("user", question));

        if (await PauseForGameAsync(answer, ct)) return;
        var raw = new System.Text.StringBuilder();
        using var client = _createClient();
        var full = await Task.Run(() => client.ChatAsync(model, turns, piece =>
        {
            string text;
            lock (raw) { raw.Append(piece); text = AssistantBrain.ParseReply(raw.ToString(), places).Text; }
            Ui.Post(() => { if (answer.IsStreaming && !ct.IsCancellationRequested && text.Length > 0) answer.Text = text; });
        }, ct), ct);

        var reply = AssistantBrain.ParseReply(full, places);
        answer.Text = reply.Text.Length > 0 ? reply.Text : Loc.T("Модель не дала ответа. Попробуйте спросить иначе или выбрать другую модель.");
        if (!isMaintenance)
            foreach (var a in reply.Actions) answer.Actions.Add(a);
    }

    private bool CanStop() => IsAnswering;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

    private bool CanClear() => Messages.Count > 0;

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear()
    {
        _cts?.Cancel();
        Messages.Clear();
        OnPropertyChanged(nameof(HasMessages));
        ClearCommand.NotifyCanExecuteChanged();
    }

    /// <summary>An answer's button: opens the place exactly like the command palette does (navigation only).</summary>
    [RelayCommand]
    private void OpenPlace(PaletteEntry? place)
    {
        if (place is not null) _open(place.PageKey, place.FocusId);
    }

    /// <summary>Ollama is installed the usual way: its card in «Приложения» (winget, the installer's signature is checked there).</summary>
    [RelayCommand]
    private void InstallOllama()
    {
        _open("apps", "app:" + OllamaWingetId);
    }

    public const string OllamaWingetId = "Ollama.Ollama";
}
