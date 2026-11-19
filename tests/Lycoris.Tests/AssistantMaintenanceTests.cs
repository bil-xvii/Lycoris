using System.Net;
using System.Net.Http;
using Lycoris.Core.Assistant;
using Lycoris.Core.Diagnostics;
using Lycoris.Modules.Assistant;
using Lycoris.Shell;

namespace Lycoris.Tests;

public class AssistantMaintenanceTests
{
    private const string Tags = """
        {"models":[{"name":"qwen3:4b","size":2000000000,"digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","details":{"format":"gguf"}},
                   {"name":"qwen3:cloud","size":2000000000,"digest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","details":{"format":"gguf"}}]}
        """;

    [Fact]
    public void A_new_assistant_shows_the_chat_and_accepts_questions_offline()
    {
        var vm = Create();

        Assert.True(vm.IsChatOpen);
        vm.State = AssistantState.NoOllama;
        vm.Question = "Check Microsoft Store apps";
        Assert.True(vm.AskCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("Какие необязательные приложения Microsoft Store удалить?", "removal")]
    [InlineData("Which unused Microsoft apps can I remove?", "removal")]
    [InlineData("Отключи ненужные службы", "services")]
    [InlineData("Don't disable all services", "services")]
    [InlineData("Что можно убрать из автозапуска?", "startup")]
    [InlineData("Отключи автозапуск", "startup")]
    [InlineData("How do I review temporary files?", "cleaner")]
    [InlineData("Clean up junk files; powershell -Command evil", "cleaner")]
    public async Task Offline_maintenance_only_offers_a_catalog_link(string question, string page)
    {
        var opened = new List<(string, string?)>();
        var vm = Create(open: (key, focus) => opened.Add((key, focus)));
        vm.State = AssistantState.NoOllama;

        await vm.AskCommand.ExecuteAsync(question);

        Assert.Empty(opened);
        var answer = vm.Messages[1];
        Assert.False(answer.IsStreaming);
        Assert.False(string.IsNullOrWhiteSpace(answer.Text));
        var link = Assert.Single(answer.Actions);
        Assert.Equal(page, link.PageKey);
        Assert.Equal(PaletteKind.Page, link.Kind);
        vm.OpenPlaceCommand.Execute(link);
        Assert.Equal((page, (string?)null), Assert.Single(opened));
    }

    [Fact]
    public async Task Active_game_blocks_chat_but_keeps_maintenance_navigation()
    {
        using var loc = Loc.UseLanguage("en");
        var vm = Create((request, _) =>
        {
            Assert.Equal("/api/tags", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Reply(Tags));
        }, runningGame: () => "Recognized game");

        await vm.CheckCommand.ExecuteAsync(null);
        Assert.Equal("qwen3:4b", Assert.Single(vm.Models));
        Assert.Equal("qwen3:4b", vm.SelectedModel);
        await vm.AskCommand.ExecuteAsync("Disable unnecessary services");

        var answer = vm.Messages[1];
        Assert.Contains("A game is running", answer.Text);
        Assert.Equal("services", Assert.Single(answer.Actions).PageKey);
    }

    [Fact]
    public async Task A_game_starting_during_diagnostics_prevents_the_chat_request()
    {
        using var loc = Loc.UseLanguage("en");
        var contextRead = false;
        var vm = Create((request, _) =>
        {
            Assert.Equal("/api/tags", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Reply(Tags));
        }, runningGame: () => contextRead ? "Recognized game" : null,
            refreshContext: () =>
            {
                contextRead = true;
                return Task.FromResult<HealthReportData?>(null);
            });
        await vm.CheckCommand.ExecuteAsync(null);

        await vm.AskCommand.ExecuteAsync("How much memory do I have?");

        Assert.True(contextRead);
        Assert.Contains("A game is running", vm.Messages[1].Text);
    }

    [Fact]
    public async Task A_cloud_selection_cannot_start_a_model_request()
    {
        var paths = new List<string>();
        var vm = Create((request, _) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(Reply(Tags));
        });
        await vm.CheckCommand.ExecuteAsync(null);
        vm.SelectedModel = "qwen3:cloud";

        await vm.AskCommand.ExecuteAsync("How much memory do I have?");

        Assert.Equal(["/api/tags"], paths);
        Assert.Empty(vm.Messages[1].Actions);
    }

    [Fact]
    public async Task An_idle_model_answers_once_without_adding_maintenance_links()
    {
        var paths = new List<string>();
        var vm = Create((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            return Task.FromResult(path == "/api/tags" ? Reply(Tags) : Reply(
                "{\"message\":{\"content\":\"Review before removing.\\n[[role:cmd.exe]]\"},\"done\":false}\n{\"done\":true}"));
        });
        await vm.CheckCommand.ExecuteAsync(null);

        await vm.AskCommand.ExecuteAsync("Review Microsoft Store apps");

        Assert.Equal(["/api/tags", "/api/tags", "/api/chat"], paths);
        var answer = vm.Messages[1];
        Assert.False(answer.IsStreaming);
        Assert.StartsWith("Review before removing.", answer.Text);
        Assert.Equal("removal", Assert.Single(answer.Actions).PageKey);
    }

    [Fact]
    public async Task Stop_cancels_active_model_work_and_leaves_the_chat_usable()
    {
        using var loc = Loc.UseLanguage("en");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/tags") return Reply(Tags);
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("The request should have been canceled.");
        });
        await vm.CheckCommand.ExecuteAsync(null);

        var answerTask = vm.AskCommand.ExecuteAsync("How much memory do I have?");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.Messages[1].IsStreaming);
        vm.StopCommand.Execute(null);
        await answerTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsAnswering);
        Assert.False(vm.Messages[1].IsStreaming);
        Assert.Equal("Answer stopped.", vm.Messages[1].Note);
        Assert.True(vm.AskCommand.CanExecute("Another question"));
    }

    private static AssistantViewModel Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? handler = null,
        Func<string?>? runningGame = null, Action<string, string?>? open = null,
        Func<Task<HealthReportData?>>? refreshContext = null) =>
        new(() => Task.FromResult<AssistantDiagnosticSnapshot?>(null),
            (_, _) => throw new InvalidOperationException("Unexpected diagnostic write."),
            open ?? ((_, _) => throw new InvalidOperationException("Unexpected navigation.")),
            refreshContext ?? (() => Task.FromResult<HealthReportData?>(null)),
            (_, _) => throw new InvalidOperationException("Unexpected service write."),
            () => new LocalModelClient(new TestHandler(handler ?? ((_, _) =>
                throw new InvalidOperationException("Unexpected model request.")))),
            runningGame ?? (() => null),
            () => CommandPaletteIndex.Build(
                [("removal", "Remove and disable", ""), ("services", "Startup and services", "")],
                [], [], [], [], CommandPaletteIndex.Sections));

    private static HttpResponseMessage Reply(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    private sealed class TestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
