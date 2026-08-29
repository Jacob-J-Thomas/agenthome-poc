using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Continuations;

internal sealed class HumanInputResponseContinuationInMemoryRunStore(CustomLoopRunRecord current) : ICustomLoopRunStore
{
    internal CustomLoopRunRecord Current { get; private set; } = current;

    internal int UpdateCount { get; private set; }

    internal Exception? GetException { get; set; }

    internal int? ThrowOnGetCall { get; set; }

    internal CustomLoopRunRecord? GetOverride { get; set; }

    internal int? GetOverrideCall { get; set; }

    internal int? ReturnNullOnGetCall { get; set; }

    internal int GetCount { get; private set; }

    internal bool ConflictNextUpdate { get; set; }

    internal bool CommitCandidateBeforeConflict { get; set; }

    internal CustomLoopRunStoreResult? UpdateOverride { get; set; }

    internal int? UpdateOverrideCall { get; set; }

    internal Exception? UpdateException { get; set; }

    internal int UpdateAttemptCount { get; private set; }

    public Task<CustomLoopRunStoreResult> CreateAsync(CustomLoopRunRecord run, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<CustomLoopRunRecord?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        GetCount++;
        if (ThrowOnGetCall == GetCount)
        {
            throw new IOException("simulated canonical run read failure");
        }
        if (GetException is not null)
        {
            throw GetException;
        }

        if (GetOverrideCall == GetCount)
        {
            return Task.FromResult(GetOverride);
        }
        if (ReturnNullOnGetCall == GetCount)
        {
            return Task.FromResult<CustomLoopRunRecord?>(null);
        }

        return Task.FromResult<CustomLoopRunRecord?>(string.Equals(runId, Current.Id, StringComparison.Ordinal) ? Current : null);
    }

    public Task<CustomLoopRunRecord?> GetByAdmissionOperationAsync(string admissionOperationId, CancellationToken cancellationToken = default)
        => Task.FromResult<CustomLoopRunRecord?>(null);

    public Task<CustomLoopRunRecord?> GetNonterminalByLoopAsync(string loopId, CancellationToken cancellationToken = default)
        => Task.FromResult<CustomLoopRunRecord?>(Current.IsTerminal ? null : Current);

    public Task<IReadOnlyList<CustomLoopRunSummary>> ListRecentAsync(int maximumCount, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CustomLoopRunSummary>>([]);

    public Task<IReadOnlyList<CustomLoopRunRecord>> ListNonterminalAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CustomLoopRunRecord>>(Current.IsTerminal ? [] : [Current]);

    public Task<CustomLoopRunStoreResult> UpdateAsync(CustomLoopRunRecord run, int expectedLifecycleVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpdateAttemptCount++;
        if (UpdateException is not null)
        {
            throw UpdateException;
        }
        if (UpdateOverride is not null && (UpdateOverrideCall is null || UpdateOverrideCall == UpdateAttemptCount))
        {
            return Task.FromResult(UpdateOverride);
        }
        if (ConflictNextUpdate)
        {
            ConflictNextUpdate = false;
            if (CommitCandidateBeforeConflict)
            {
                var concurrentValidation = CustomLoopRunValidator.ValidateUpdate(Current, run);
                Assert.True(concurrentValidation.IsValid, string.Join(Environment.NewLine, concurrentValidation.Errors));
                Current = run;
                UpdateCount++;
            }
            return Task.FromResult(CustomLoopRunStoreResult.VersionConflict(Current, expectedLifecycleVersion));
        }
        if (expectedLifecycleVersion != Current.LifecycleVersion)
        {
            return Task.FromResult(CustomLoopRunStoreResult.VersionConflict(Current, expectedLifecycleVersion));
        }

        var validation = CustomLoopRunValidator.ValidateUpdate(Current, run);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        Current = run;
        UpdateCount++;
        return Task.FromResult(CustomLoopRunStoreResult.Updated(run));
    }

    internal async Task AdvanceFromOrderedHumanInputReentryAsync(
        GovernedLoopSequentialAdapterBinding binding,
        GovernedLoopSequentialPlan plan,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        var selected = GovernedLoopSequentialFrontierMachine.Select(Current.Frontier, binding, plan);
        Assert.Equal(GovernedLoopSequentialFrontierSelectionStatus.Ready, selected.Status);
        var node = Assert.IsType<GovernedLoopSequentialPlanNode>(selected.Node);
        var activation = Assert.IsType<GovernedLoopNodeExecutionEvidence>(selected.Activation);
        const string OperationId = "human-input-continuation-exit-claim";
        var transitioned = GovernedLoopSequentialFrontierMachine.Start(
            Current.Frontier,
            binding,
            plan,
            node,
            activation,
            1,
            OperationId,
            completedAtUtc);
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, transitioned.Status);
        var started = new CustomLoopRunEvent(
            Current.Events[^1].Sequence + 1,
            OperationId,
            completedAtUtc,
            node.Descriptor.Kind == GovernedLoopNodeKind.Fail ? CustomLoopRunEventKind.NodeAttemptStarted : CustomLoopRunEventKind.ExitDecisionStarted,
            activation.CycleIteration ?? Current.Checkpoint.Iteration,
            node.NodeId,
            1,
            "The ordered runtime retained exact downstream dispatch after the Human Input terminal checkpoint.",
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            TraceReservationUtf8Bytes: node.Descriptor.Kind == GovernedLoopNodeKind.Fail
                ? CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes
                : CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes);
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            CustomLoopSequentialNodeEvidence.CurrentSchemaVersion,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            binding.WorkspaceId,
            binding.ExecutionBinding.RunId,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            activation.ActivationOrdinal,
            activation.VisitOrdinal,
            node.NodeId,
            1,
            activation.CycleId,
            activation.CycleIteration,
            null,
            [],
            [],
            null,
            null,
            CustomLoopSequentialNodeDisposition.Unknown,
            CustomLoopSequentialOutcomeArtifactHash.Compute(started),
            string.Empty));
        started = started with { SequentialNodeEvidence = evidence };
        var advanced = Current with
        {
            LifecycleVersion = checked(Current.LifecycleVersion + 1),
            UpdatedAtUtc = completedAtUtc,
            Events = [.. Current.Events, started],
            Frontier = transitioned.Frontier,
        };
        var result = await UpdateAsync(advanced, Current.LifecycleVersion, cancellationToken);
        Assert.Equal(CustomLoopRunStoreStatus.Updated, result.Status);
    }
}
