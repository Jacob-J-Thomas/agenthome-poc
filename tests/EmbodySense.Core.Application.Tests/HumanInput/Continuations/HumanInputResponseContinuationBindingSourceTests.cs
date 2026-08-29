using EmbodySense.Core.Application.HumanInput.Continuations;
using EmbodySense.Core.Application.HumanInput.Continuations.Models;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Tests.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Continuations;

public sealed class HumanInputResponseContinuationBindingSourceTests
{
    [Fact]
    public void Constructor_rejects_a_missing_authoritative_response_store()
        => Assert.Throws<ArgumentNullException>(() => new HumanInputResponseContinuationBindingSource(null!));

    [Theory]
    [InlineData(HumanInputResponsePolicyKind.FirstValid)]
    [InlineData(HumanInputResponsePolicyKind.ManualSelection)]
    [InlineData(HumanInputResponsePolicyKind.Quorum)]
    public async Task Exact_terminal_selection_projects_one_supported_response_value(
        HumanInputResponsePolicyKind policyKind)
    {
        var (scenario, checkpoint) = await TerminalScenarioAsync(Policy(policyKind), policyKind == HumanInputResponsePolicyKind.Quorum ? ["accepted", "accepted"] : ["accepted"]);

        var result = await new HumanInputResponseContinuationBindingSource(scenario.Responses).ResolveAsync(checkpoint);

        Assert.Equal(GovernedLoopSequentialHumanInputBindingReadStatus.Ready, result.Status);
        var binding = Assert.IsType<GovernedLoopSequentialHumanInputBinding>(result.Binding);
        Assert.Equal(checkpoint.Binding.CheckpointId, binding.CheckpointId);
        Assert.Equal(policyKind, binding.Selection.PolicyKind);
        Assert.Equal("\"accepted\"", binding.Value.CanonicalValueJson);
        Assert.Contains("Value = [REDACTED]", binding.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("accepted", binding.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HumanInputResponsePolicyKind.Quorum)]
    [InlineData(HumanInputResponsePolicyKind.NamedRoles)]
    [InlineData(HumanInputResponsePolicyKind.Merge)]
    public async Task Divergent_or_unsupported_multi_value_selections_fail_closed(
        HumanInputResponsePolicyKind policyKind)
    {
        if (policyKind == HumanInputResponsePolicyKind.Quorum)
        {
            var quorumScenario = await HumanInputResponseContinuationScenario.CreateAsync(
                responsePolicy: Policy(policyKind),
                selectionValues: [HumanInputResponseLifecycleTestData.Text("first"), HumanInputResponseLifecycleTestData.Text("second")],
                requireSelection: false);

            var wake = await quorumScenario.Service.WakeAsync(quorumScenario.Candidate);
            var pending = Assert.Single(quorumScenario.Runs.Current.HumanInputWaitingCheckpoints);
            var quorumResult = await new HumanInputResponseContinuationBindingSource(quorumScenario.Responses).ResolveAsync(pending);

            Assert.Equal(HumanInputResponseContinuationWakeStatus.NoWork, wake.Status);
            Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Pending, pending.Posture);
            Assert.Equal(0, quorumScenario.Ordered.ResumeHumanInputCount);
            Assert.Equal(GovernedLoopSequentialHumanInputBindingReadStatus.Invalid, quorumResult.Status);
            Assert.Null(quorumResult.Binding);
            return;
        }

        var (scenario, checkpoint) = await TerminalScenarioAsync(Policy(policyKind), ["first", "second"]);

        var result = await new HumanInputResponseContinuationBindingSource(scenario.Responses).ResolveAsync(checkpoint);

        Assert.Equal(GovernedLoopSequentialHumanInputBindingReadStatus.Invalid, result.Status);
        Assert.Null(result.Binding);
    }

    [Theory]
    [InlineData(HumanInputResponseLifecycleStoreReadStatus.Ready, GovernedLoopSequentialHumanInputBindingReadStatus.Invalid)]
    [InlineData(HumanInputResponseLifecycleStoreReadStatus.NotFound, GovernedLoopSequentialHumanInputBindingReadStatus.Invalid)]
    [InlineData(HumanInputResponseLifecycleStoreReadStatus.OperationConflict, GovernedLoopSequentialHumanInputBindingReadStatus.Invalid)]
    [InlineData(HumanInputResponseLifecycleStoreReadStatus.Unavailable, GovernedLoopSequentialHumanInputBindingReadStatus.Unavailable)]
    [InlineData(HumanInputResponseLifecycleStoreReadStatus.Ambiguous, GovernedLoopSequentialHumanInputBindingReadStatus.Unavailable)]
    public async Task Corrupt_and_nonready_response_store_observations_remain_closed(
        HumanInputResponseLifecycleStoreReadStatus responseStatus,
        GovernedLoopSequentialHumanInputBindingReadStatus expected)
    {
        var (_, checkpoint) = await TerminalScenarioAsync(Policy(HumanInputResponsePolicyKind.FirstValid), ["accepted"]);
        var store = new HumanInputResponseContinuationFixedResponseStore(null, responseStatus);

        var result = await new HumanInputResponseContinuationBindingSource(store).ResolveAsync(checkpoint);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Binding);
        Assert.Equal(1, store.ReadCount);
    }

    [Fact]
    public async Task Fresh_source_read_recovers_after_transient_unavailability_without_retaining_a_private_value()
    {
        var (scenario, checkpoint) = await TerminalScenarioAsync(Policy(HumanInputResponsePolicyKind.FirstValid), ["accepted"]);
        var request = RequestReference(checkpoint);
        var ready = await scenario.Responses.ReadAsync(request);
        var store = new HumanInputResponseContinuationFixedResponseStore(null, HumanInputResponseLifecycleStoreReadStatus.Unavailable);
        var source = new HumanInputResponseContinuationBindingSource(store);

        var unavailable = await source.ResolveAsync(checkpoint);
        store.Snapshot = ready.Snapshot;
        var recovered = await source.ResolveAsync(checkpoint);

        Assert.Equal(GovernedLoopSequentialHumanInputBindingReadStatus.Unavailable, unavailable.Status);
        Assert.Equal(GovernedLoopSequentialHumanInputBindingReadStatus.Ready, recovered.Status);
        Assert.NotNull(recovered.Binding);
        Assert.Equal(2, store.ReadCount);
    }

    [Fact]
    public async Task Invalid_or_unavailable_source_evidence_never_materializes_a_binding()
    {
        var (_, checkpoint) = await TerminalScenarioAsync(Policy(HumanInputResponsePolicyKind.FirstValid), ["accepted"]);
        var invalidStore = new HumanInputResponseContinuationFixedResponseStore(null, HumanInputResponseLifecycleStoreReadStatus.Ready);
        var unavailableStore = new HumanInputResponseContinuationFixedResponseStore(null, HumanInputResponseLifecycleStoreReadStatus.Unavailable)
        {
            ReadException = new IOException("simulated source outage"),
        };

        var nonterminal = await new HumanInputResponseContinuationBindingSource(invalidStore).ResolveAsync(checkpoint with { Posture = GovernedLoopHumanInputWaitingCheckpointPosture.Pending });
        var unavailable = await new HumanInputResponseContinuationBindingSource(unavailableStore).ResolveAsync(checkpoint);

        Assert.Equal(GovernedLoopSequentialHumanInputBindingReadStatus.Invalid, nonterminal.Status);
        Assert.Null(nonterminal.Binding);
        Assert.Equal(0, invalidStore.ReadCount);
        Assert.Equal(GovernedLoopSequentialHumanInputBindingReadStatus.Unavailable, unavailable.Status);
        Assert.Null(unavailable.Binding);
        Assert.Equal(1, unavailableStore.ReadCount);
    }

    [Fact]
    public async Task Resolve_propagates_a_caller_cancellation_from_the_authoritative_response_store()
    {
        var (_, checkpoint) = await TerminalScenarioAsync(Policy(HumanInputResponsePolicyKind.FirstValid), ["accepted"]);
        var store = new HumanInputResponseContinuationFixedResponseStore(null, HumanInputResponseLifecycleStoreReadStatus.Unavailable)
        {
            ReadException = new OperationCanceledException("simulated caller cancellation"),
        };
        var source = new HumanInputResponseContinuationBindingSource(store);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ResolveAsync(checkpoint, cancellation.Token));

        Assert.Equal(1, store.ReadCount);
    }

    private static HumanInputResponsePolicy Policy(HumanInputResponsePolicyKind policyKind)
        => policyKind switch
        {
            HumanInputResponsePolicyKind.FirstValid => new HumanInputResponsePolicy(policyKind, null, null),
            HumanInputResponsePolicyKind.ManualSelection => new HumanInputResponsePolicy(policyKind, null, ["selector-role"]),
            HumanInputResponsePolicyKind.Quorum => new HumanInputResponsePolicy(policyKind, 2, null),
            HumanInputResponsePolicyKind.NamedRoles => new HumanInputResponsePolicy(policyKind, null, ["role-two", "role-one"]),
            HumanInputResponsePolicyKind.Merge => new HumanInputResponsePolicy(policyKind, 2, ["role-two", "role-one"]),
            _ => throw new ArgumentOutOfRangeException(nameof(policyKind)),
        };

    private static async Task<(HumanInputResponseContinuationScenario Scenario, GovernedLoopHumanInputWaitingCheckpoint Checkpoint)> TerminalScenarioAsync(
        HumanInputResponsePolicy policy,
        IReadOnlyList<string> responseValues)
    {
        var scenario = await HumanInputResponseContinuationScenario.CreateAsync(
            responsePolicy: policy,
            selectionValues: responseValues.Select(HumanInputResponseLifecycleTestData.Text).ToArray());

        await scenario.Service.WakeAsync(scenario.Candidate);

        var checkpoint = Assert.Single(scenario.Runs.Current.HumanInputWaitingCheckpoints);
        Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Terminal, checkpoint.Posture);
        return (scenario, checkpoint);
    }

    private static HumanInputRequestReference RequestReference(GovernedLoopHumanInputWaitingCheckpoint checkpoint)
        => new(
            HumanInputRequestReference.CurrentSchemaVersion,
            checkpoint.Request.RequestId,
            checkpoint.Request.RequestVersionId,
            checkpoint.Request.RequestHash);
}
