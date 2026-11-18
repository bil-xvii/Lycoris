using System.Windows.Input;
using Lycoris.Core.Services;
using Lycoris.Modules.Services;
using Lycoris.Shell;

namespace Lycoris.Tests;

public class ServiceCleanupUiTests
{
    private static readonly ServiceRemovalCandidate Candidate =
        new("Everything", "Everything", @"C:\Program Files\Everything\Everything.exe", new string('A', 64));

    [Theory]
    [InlineData(false, false, false, false, false, false)]
    [InlineData(false, false, true, false, false, false)]
    [InlineData(false, false, false, true, false, false)]
    [InlineData(true, false, true, true, false, false)]
    [InlineData(false, true, true, true, false, false)]
    [InlineData(false, false, true, true, true, false)]
    [InlineData(false, false, true, true, null, false)]
    [InlineData(false, false, true, true, false, true)]
    public void Removal_requires_both_expert_modes_and_known_unmanaged_state(bool locked, bool demo, bool expert,
        bool servicesExpert, bool? managed, bool allowed) =>
        Assert.Equal(allowed, ServicesViewModel.AllowsResidueRemoval(locked, demo, expert, servicesExpert, managed));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Confirm_and_attestation_are_both_required_before_queuing(bool confirmed, bool attested)
    {
        var result = await ServicesViewModel.ReviewResidueRemovalAsync(Candidate,
            () => Task.FromResult((confirmed, attested)),
            _ => throw new InvalidOperationException("Unexpected operation lease"),
            () => throw new InvalidOperationException("Unexpected re-check"),
            _ => throw new InvalidOperationException("Unexpected write"));

        Assert.Equal(ServiceRemovalOutcome.Unchanged, result.Outcome);
        Assert.Equal(ServiceRemovalReason.ApprovalRequired, result.Reason);
    }

    [Fact]
    public async Task Confirmation_is_refused_when_the_full_fingerprint_changes_before_the_lease_runs()
    {
        var waitingForLease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var granted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = Candidate;
        var reviewing = ServicesViewModel.ReviewResidueRemovalAsync(Candidate,
            () => Task.FromResult((true, true)),
            async operation =>
            {
                waitingForLease.SetResult();
                await granted.Task;
                return await operation();
            },
            () => Task.FromResult<ServiceRemovalCandidate?>(current),
            _ => throw new InvalidOperationException("Unexpected write for a stale fingerprint"));
        await waitingForLease.Task.WaitAsync(TimeSpan.FromSeconds(5));
        current = Candidate with { Fingerprint = new string('B', 64) };
        granted.SetResult();

        var result = await reviewing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ServiceRemovalOutcome.Unchanged, result.Outcome);
        Assert.Equal(ServiceRemovalReason.Changed, result.Reason);
    }

    [Fact]
    public async Task Verified_candidate_passes_the_confirmed_identity_and_returns_pending_removal()
    {
        using var loc = Loc.UseLanguage("en");
        var record = Guid.Parse("bb7ba3ce-01d9-490b-a28b-00a0807166a4");
        var requests = new List<ServiceRemovalRequest>();
        var result = await ServicesViewModel.ReviewResidueRemovalAsync(Candidate,
            () => Task.FromResult((true, true)), operation => operation(),
            () => Task.FromResult<ServiceRemovalCandidate?>(Candidate with { }),
            request =>
            {
                requests.Add(request);
                return Task.FromResult(new ServiceRemovalResult(ServiceRemovalOutcome.PendingDeletion, JournalRecordId: record));
            });

        Assert.Equal(new ServiceRemovalRequest(Candidate.Name, Candidate.Fingerprint, true), Assert.Single(requests));
        Assert.Equal(ServiceRemovalOutcome.PendingDeletion, result.Outcome);
        Assert.Equal(record, result.JournalRecordId);
        Assert.StartsWith("Windows accepted the removal request.", ServicesViewModel.ResidueResultText(result));
    }

    [Fact]
    public void A_missing_recipe_service_and_a_failed_probe_are_distinct()
    {
        using var loc = Loc.UseLanguage("en");
        Assert.Equal("The Everything service is not registered.",
            ServicesViewModel.ResidueProbeText(new(null, ServiceRemovalReason.MissingService)));
        Assert.Equal("Could not verify an Everything service remnant.",
            ServicesViewModel.ResidueProbeText(new(null, ServiceRemovalReason.InspectionFailed)));
    }

    [Theory]
    [InlineData("CheckServiceResidueCommand")]
    [InlineData("RemoveServiceResidueCommand")]
    [InlineData("OpenServiceResidueRecordCommand")]
    public void Cleanup_actions_are_available_to_services_view_bindings(string name) =>
        Assert.True(typeof(ICommand).IsAssignableFrom(typeof(ServicesViewModel).GetProperty(name)?.PropertyType));

    [Theory]
    [InlineData("services", "Everything", true)]
    [InlineData("assistant", "Everything", false)]
    [InlineData("services", "Spooler", false)]
    [InlineData("services", "Everything:fingerprint", false)]
    [InlineData("services", "Everything --force", false)]
    [InlineData("services", null, false)]
    public void Removal_replay_carries_only_the_supported_name(string page, string? parameter, bool accepted)
    {
        var intent = new AdminIntent(page, "RemoveServiceResidue", parameter);
        Assert.Equal(accepted ? intent : null, AdminIntent.Parse(intent.ToArgs()));
    }
}
