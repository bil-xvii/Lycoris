using CommunityToolkit.Mvvm.Input;
using Lycoris.Core.Assistant;
using Lycoris.Modules.Assistant;
using Lycoris.Modules.Services;
using Lycoris.Shell;

namespace Lycoris.Tests;

public class AssistantServiceTests
{
    [Theory]
    [InlineData("Включи службу Spooler", ServiceAction.Enable, "Spooler")]
    [InlineData("пожалуйста, отключи службу «Диспетчер печати»", ServiceAction.Disable, "Диспетчер печати")]
    [InlineData("Хочу выключить печать", ServiceAction.Disable, "печать")]
    [InlineData("мне нужно отключить службу печати", ServiceAction.Disable, "печати")]
    [InlineData("включи служба печати", ServiceAction.Enable, "служба печати")]
    [InlineData("отключи Print Spooler", ServiceAction.Disable, "Print Spooler")]
    [InlineData("ЗАПУСТИ Spooler", ServiceAction.Start, "Spooler")]
    [InlineData("останови службу 'Acme (Print)'", ServiceAction.Stop, "Acme (Print)")]
    [InlineData("Please enable the service “Bluetooth Support Service”", ServiceAction.Enable, "Bluetooth Support Service")]
    [InlineData("I need to stop service Acme_Agent-2", ServiceAction.Stop, "Acme_Agent-2")]
    [InlineData("disable service Remote Desktop Services", ServiceAction.Disable, "Remote Desktop Services")]
    [InlineData("start service \"Distributed Link and Tracking Client\"", ServiceAction.Start, "Distributed Link and Tracking Client")]
    public void Parser_retains_one_explicit_target(string input, ServiceAction action, string target)
    {
        var parsed = ServiceRequestParser.Parse(input);
        Assert.Equal(ServiceRequestKind.Ready, parsed.Kind);
        Assert.Equal(new ServiceActionRequest(action, target), parsed.Request);
    }

    [Theory]
    [InlineData("не включи службу Spooler")]
    [InlineData("мне нужно не включить службу Spooler")]
    [InlineData("Если отключить службу Spooler, что произойдёт?")]
    [InlineData("зачем запускать службу Spooler и отключить службу W32Time?")]
    [InlineData("How do I disable service Spooler?")]
    [InlineData("Do not start Spooler")]
    [InlineData("отключи службу Spooler и W32Time")]
    [InlineData("отключи службы")]
    [InlineData("disable service Spooler stop W32Time")]
    [InlineData("enable service Spooler because printing is broken")]
    [InlineData("enable service Spooler if the driver is installed")]
    [InlineData("enable service \"Spooler")]
    [InlineData("enable service «Spooler\"")]
    [InlineData("stop service Spooler & sc.exe stop W32Time")]
    [InlineData("stop service Spooler --force")]
    [InlineData("disable service Spooler\n")]
    [InlineData("disable service Spooler\nstart service W32Time")]
    public void Parser_refuses_ambiguous_or_compound_commands(string input)
    {
        var parsed = ServiceRequestParser.Parse(input);
        Assert.Equal(ServiceRequestKind.Invalid, parsed.Kind);
        Assert.Null(parsed.Request);
    }

    [Theory]
    [InlineData("включи службу")]
    [InlineData("start the service")]
    [InlineData("выключи службу «»")]
    public void Parser_distinguishes_a_missing_name(string input) =>
        Assert.Equal(ServiceRequestKind.MissingTarget, ServiceRequestParser.Parse(input).Kind);

    [Fact]
    public async Task Offline_service_command_executes_once_and_links_to_the_exact_service_and_change_record()
    {
        using var loc = Loc.UseLanguage("en");
        var recordId = Guid.Parse("4b303311-e7e1-4b54-914a-76c513ec4877");
        var requests = new List<ServiceActionRequest>();
        var locations = new List<(string, string?)>();
        const string resultText = "Spooler can start on demand. Current status is stopped.";
        var vm = Offline((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(new ServiceActionResult(ServiceActionOutcome.Applied, resultText, "Spooler", recordId));
        }, (page, focus) => locations.Add((page, focus)));

        vm.Question = "Включи службу «Диспетчер печати»";
        Assert.True(vm.AskCommand.CanExecute(null));
        await vm.AskCommand.ExecuteAsync(null);

        Assert.Equal(new ServiceActionRequest(ServiceAction.Enable, "Диспетчер печати"), Assert.Single(requests));
        var reply = vm.Messages[1];
        Assert.Equal(resultText, reply.Text);
        Assert.False(reply.IsStreaming);
        Assert.Empty(locations);
        foreach (var place in reply.Actions) vm.OpenPlaceCommand.Execute(place);
        Assert.Equal([("restore", $"section:record:{recordId:D}"), ("services", "Spooler")], locations);
        Assert.False(vm.AskCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("Отключи службу", "Which service? Enter its display name or system name, for example Spooler.")]
    [InlineData("Отключи службу Spooler и W32Time", "Enter one service and one action. For example, “disable service Spooler”.")]
    public async Task Missing_or_malformed_target_gets_offline_clarification_without_a_service_call(string input, string message)
    {
        using var loc = Loc.UseLanguage("en");
        var vm = Offline((_, _) => throw new InvalidOperationException("Unexpected service action"));
        vm.Question = input;

        Assert.True(vm.AskCommand.CanExecute(null));
        await vm.AskCommand.ExecuteAsync(null);

        Assert.Equal(message, vm.Messages[1].Text);
        Assert.Empty(vm.Messages[1].Actions);
    }

    [Theory]
    [InlineData(ServiceActionOutcome.Declined, "Operation canceled.")]
    [InlineData(ServiceActionOutcome.Blocked, "This service is protected.")]
    [InlineData(ServiceActionOutcome.Ambiguous, "More than one service matches. Use its system name.")]
    [InlineData(ServiceActionOutcome.Missing, "The service was not found.")]
    public async Task Unapplied_results_display_the_workflow_reason(ServiceActionOutcome outcome, string message)
    {
        var vm = Offline((_, _) => Task.FromResult(new ServiceActionResult(outcome, message)));
        vm.Question = "disable service Spooler";
        await vm.AskCommand.ExecuteAsync(null);

        Assert.Equal(message, vm.Messages[1].Text);
        Assert.Empty(vm.Messages[1].Actions);
    }

    [Fact]
    public async Task Stop_asks_to_cancel_but_displays_the_result_when_a_service_write_already_finished()
    {
        var result = new TaskCompletionSource<ServiceActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        var vm = Offline((_, ct) => { token = ct; return result.Task; });
        vm.Question = "stop service Spooler";
        var running = vm.AskCommand.ExecuteAsync(null);

        Assert.True(vm.StopCommand.CanExecute(null));
        vm.StopCommand.Execute(null);
        Assert.True(token.IsCancellationRequested);
        result.SetResult(new(ServiceActionOutcome.Applied, "Spooler is stopped.", "Spooler"));
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Spooler is stopped.", vm.Messages[1].Text);
        Assert.Null(vm.Messages[1].Note);
    }

    [Fact]
    public async Task Admin_replay_uses_exact_scm_identity_and_opens_the_chat()
    {
        using var loc = Loc.UseLanguage("en");
        var requests = new List<ServiceActionRequest>();
        var vm = Offline((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(new ServiceActionResult(ServiceActionOutcome.AlreadyApplied, "Already running.", request.Target));
        });
        var intent = new AdminIntent("assistant", "RunServiceAction", "start:Spooler");
        var command = Assert.IsAssignableFrom<IAsyncRelayCommand>(intent.FindCommand(vm));

        Assert.True(command.CanExecute(intent.Parameter));
        await command.ExecuteAsync(intent.Parameter);

        Assert.Equal(new ServiceActionRequest(ServiceAction.Start, "Spooler") { ExactNameOnly = true }, Assert.Single(requests));
        Assert.True(vm.IsChatOpen);
        Assert.Equal("Start service Spooler", vm.Messages[0].Text);
        Assert.Equal("Already running.", vm.Messages[1].Text);
    }

    private static AssistantViewModel Offline(Func<ServiceActionRequest, CancellationToken, Task<ServiceActionResult>> execute,
        Action<string, string?>? open = null) =>
        new(() => throw new InvalidOperationException("Unexpected health checks"),
            (_, _) => throw new InvalidOperationException("Unexpected diagnostic fix"),
            open ?? ((_, _) => throw new InvalidOperationException("Unexpected navigation")),
            () => throw new InvalidOperationException("Unexpected model context"), execute)
        {
            State = AssistantState.NoOllama,
        };
}
