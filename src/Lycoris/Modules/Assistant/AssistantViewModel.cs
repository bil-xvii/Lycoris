using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Core.Assistant;
using Lycoris.Modules.Dashboard;
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
/// «Помощник»: questions about this PC in plain words, answered by a local model (Ollama on this computer — nothing leaves the PC).
/// It only explains and opens the right place; every change is made with Lycoris's own buttons, confirmation and undo
/// (see <see cref="AssistantBrain"/> for the rules and the place list).
/// </summary>
public sealed partial class AssistantViewModel : PageViewModel
{
    private readonly DashboardViewModel _dashboard;
    private CancellationTokenSource? _cts;

    public AssistantViewModel(DashboardViewModel dashboard) => _dashboard = dashboard;

    public override string Key => "assistant";
    public override string Index => "12";
    public override string Title => Loc.T("Помощник");
    public override string Subtitle => Loc.T("Ответы о вашем ПК простыми словами. Работает на этом компьютере — ничего не уходит в интернет.");
    public override string Glyph => Glyphs.Assistant;

    /// <summary>QA only: «--snapshot … --demo-assistant» fills a sample conversation (see <see cref="AssistantDemo"/>).</summary>
    public static bool Demo { get; set; }

    public ObservableCollection<AssistantMessage> Messages { get; } = [];
    public ObservableCollection<string> Models { get; } = [];

    [ObservableProperty] private AssistantState _state = AssistantState.Checking;
    [ObservableProperty] private string _question = "";
    [ObservableProperty] private bool _isAnswering;
    [ObservableProperty] private string? _selectedModel;

    public bool IsReady => State == AssistantState.Ready;
    public bool NeedsOllama => State == AssistantState.NoOllama;
    public bool NeedsModel => State == AssistantState.NoModels;
    public bool HasMessages => Messages.Count > 0;

    public string StateTag => State switch
    {
        AssistantState.Ready => Loc.T("ГОТОВ"),
        AssistantState.NoOllama => Loc.T("OLLAMA НЕ НАЙДЕН"),
        AssistantState.NoModels => Loc.T("НЕТ МОДЕЛИ"),
        _ => Loc.T("ПРОВЕРКА"),
    };

    public string StateText => State switch
    {
        AssistantState.Ready => Loc.T("Модель работает на этом ПК. Помощник только советует и открывает нужное место — изменения вы делаете кнопками Lycoris, с подтверждением и откатом."),
        AssistantState.NoOllama => Loc.T("Нужна бесплатная программа Ollama — она запускает модель прямо на вашем ПК. Установите её в «Приложениях», затем нажмите «ОБНОВИТЬ»."),
        AssistantState.NoModels => Loc.T("Ollama работает, но модели нет. Скачайте одну командой «ollama pull qwen3:8b» (около 5 ГБ) и нажмите «ОБНОВИТЬ»."),
        _ => Loc.T("Ищем Ollama на этом ПК…"),
    };

    /// <summary>Ready-made questions: one click asks them.</summary>
    public IReadOnlyList<string> QuickQuestions { get; } =
    [
        Loc.T("Почему ПК может тормозить?"),
        Loc.T("Что улучшить для игр?"),
        Loc.T("Объясни найденные проблемы"),
        Loc.T("Что Lycoris уже менял?"),
    ];

    partial void OnStateChanged(AssistantState value)
    {
        OnPropertyChanged(nameof(IsReady)); OnPropertyChanged(nameof(NeedsOllama)); OnPropertyChanged(nameof(NeedsModel));
        OnPropertyChanged(nameof(StateTag)); OnPropertyChanged(nameof(StateText));
        AskCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedModelChanged(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Demo || AppSettings.Current.AssistantModel == value) return;
        AppSettings.Current.AssistantModel = value;
        AppSettings.Current.Save();
    }

    partial void OnIsAnsweringChanged(bool value) { AskCommand.NotifyCanExecuteChanged(); StopCommand.NotifyCanExecuteChanged(); }
    partial void OnQuestionChanged(string value) => AskCommand.NotifyCanExecuteChanged();

    protected override async Task LoadAsync()
    {
        if (Demo) { AssistantDemo.Fill(this); MarkChecked(); return; }
        await CheckAsync();
    }

    protected override Task OnReturnAsync() => State == AssistantState.Ready || IsAnswering ? Task.CompletedTask : CheckAsync();

    /// <summary>«ОБНОВИТЬ»: is Ollama running, which models does it have.</summary>
    [RelayCommand]
    private async Task CheckAsync()
    {
        if (Demo) return;
        State = AssistantState.Checking;
        using var client = new LocalModelClient();
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

    private bool CanAsk(string? preset) => IsReady && !IsAnswering && !string.IsNullOrWhiteSpace(preset ?? Question);

    [RelayCommand(CanExecute = nameof(CanAsk))]
    private async Task AskAsync(string? preset)
    {
        var question = (preset ?? Question).Trim();
        if (question.Length == 0 || SelectedModel is not { } model) return;
        if (question.Length > 1000) question = question[..1000];
        Question = "";
        AddMessage(new AssistantMessage { IsUser = true, Text = question });
        var answer = new AssistantMessage { IsStreaming = true, Text = Loc.T("Думаю…") };
        AddMessage(answer);
        IsAnswering = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            // The facts come from the checks of «Панель» (read-only); run them once if they never ran.
            if (!_dashboard.HasResults) await _dashboard.RefreshChecksCommand.ExecuteAsync(null);
            var pc = _dashboard.HasResults ? _dashboard.BuildHealthReportData() : null;
            var index = Ui.Host is MainViewModel shell ? CommandPaletteIndex.Build(shell.Pages) : [];
            var issueTitles = pc?.Checks.Where(c => c.Status != "ok").Select(c => c.Title).Take(3) ?? [];
            var places = AssistantBrain.SelectPlaces(index, question, issueTitles);
            var changes = ChangeJournal.Instance.Records.Where(r => !r.Reverted).OrderByDescending(r => r.TimestampUtc).Select(r => Loc.Sink(r.Title)).Distinct().Take(6).ToList();

            var turns = new List<ChatTurn> { new("system", AssistantBrain.SystemPrompt(Loc.IsEnglish) + "\n\n" + AssistantBrain.Context(pc, places, changes)) };
            // A short memory of the conversation (the last two exchanges), so «а подробнее?» works.
            foreach (var m in Messages.Take(Messages.Count - 2).TakeLast(4))
                turns.Add(new ChatTurn(m.IsUser ? "user" : "assistant", m.Text));
            turns.Add(new ChatTurn("user", question));

            var raw = new System.Text.StringBuilder();
            using var client = new LocalModelClient();
            var full = await Task.Run(() => client.ChatAsync(model, turns, piece =>
            {
                string text;
                lock (raw) { raw.Append(piece); text = AssistantBrain.ParseReply(raw.ToString(), places).Text; }
                Ui.Post(() => { if (text.Length > 0) answer.Text = text; });
            }, ct), ct);

            var reply = AssistantBrain.ParseReply(full, places);
            answer.Text = reply.Text.Length > 0 ? reply.Text : Loc.T("Модель не дала ответа. Попробуйте спросить иначе или выбрать другую модель.");
            foreach (var a in reply.Actions) answer.Actions.Add(a);
        }
        catch (OperationCanceledException)
        {
            if (answer.Text == Loc.T("Думаю…")) answer.Text = "";
            answer.Note = Loc.T("Ответ остановлен.");
        }
        catch (HttpRequestException ex)
        {
            Log.Warn("Помощник: Ollama не ответил: {0}", ex.Message);
            answer.Text = Loc.T("Ollama не ответил. Проверьте, что он запущен, и нажмите «ОБНОВИТЬ».");
            answer.Note = ex.Message;
        }
        finally
        {
            answer.IsStreaming = false;
            IsAnswering = false;
            _cts?.Dispose(); _cts = null;
        }
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
        if (place is null) return;
        if (place.FocusId is { } id) Ui.RequestFocus(place.PageKey, id);
        Ui.Navigate(place.PageKey);
    }

    /// <summary>Ollama is installed the usual way: its card in «Приложения» (winget, the installer's signature is checked there).</summary>
    [RelayCommand]
    private void InstallOllama()
    {
        Ui.RequestFocus("apps", "app:" + OllamaWingetId);
        Ui.Navigate("apps");
    }

    public const string OllamaWingetId = "Ollama.Ollama";
}
