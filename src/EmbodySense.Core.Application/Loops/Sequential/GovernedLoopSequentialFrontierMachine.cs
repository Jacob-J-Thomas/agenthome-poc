using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Creates, selects, and advances one durable concurrency-one frontier over an immutable admitted graph topology.</summary>
/// <remarks>
/// The machine is pure policy. It never persists, dispatches, grants authority, evaluates a Condition, or infers progress from
/// the legacy numeric checkpoint. Every claim and resolution consumes an exact durable activation identity. Multiple activations
/// may remain Ready, but exactly one can be Running.
/// </remarks>
public static class GovernedLoopSequentialFrontierMachine
{
    private const int ConcurrencyCeiling = GovernedLoopTopologySchedulerPolicy.DefaultMaximumConcurrency;

    /// <summary>Creates the initial completed Trigger and every directly eligible Ready activation.</summary>
    public static GovernedLoopSequentialFrontierTransitionResult Initialize(
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopSequentialPlan? plan,
        string? triggerAttemptOperationId,
        string? triggerOutcomeEvidenceId,
        string? triggerOutcomeEvidenceHash,
        DateTimeOffset updatedAtUtc)
    {
        if (!MatchesPlanBinding(binding, plan)
            || plan!.Nodes.Count < 2
            || !Equals(plan.Nodes[0].Descriptor, GovernedLoopSequentialNodeDescriptors.ManualTrigger))
        {
            return Invalid("The immutable topology binding and plan cannot form an initial frontier.");
        }

        try
        {
            if (!TryResolveRoute(plan, plan.Nodes[0], GovernedLoopControlCondition.Always, out var selectedEdges, out var skippedEdges))
            {
                return Invalid("The admitted Trigger does not expose one exact unconditional initial route.");
            }

            var trigger = CreateActivation(
                plan.Nodes[0],
                activationOrdinal: 0,
                visitOrdinal: 1,
                cycleIteration: null,
                GovernedLoopNodeExecutionStatus.Completed,
                attempt: 1,
                triggerAttemptOperationId,
                triggerOutcomeEvidenceId,
                triggerOutcomeEvidenceHash,
                GovernedLoopControlCondition.Always,
                selectedEdges,
                skippedEdges,
                []);
            var nodes = new List<GovernedLoopNodeExecutionEvidence> { trigger };
            if (!TryAppendEligibleSuccessors(plan, nodes, trigger, updatedAtUtc, cycleStartedAtUtc: null, out var failure)
                || nodes.Count == 1)
            {
                return Invalid(failure ?? "The completed Trigger exposed no executable admitted successor.");
            }

            var frontier = CreatePosture(binding!, 1, GovernedLoopFrontierStatus.Active, nodes, updatedAtUtc);
            return Validate(frontier, binding, plan)
                ? Applied(frontier, "The completed Trigger exposed only exact eligible Ready activations under concurrency-one policy.")
                : Invalid("The initial canonical topology frontier failed exact validation.");
        }
        catch (Exception exception) when (IsContractFailure(exception))
        {
            return Invalid($"The initial canonical frontier was rejected by its bounded contract: {exception.GetType().Name}.");
        }
    }

    /// <summary>Returns whether a posture is an exact hash-valid activation history for the admitted immutable topology.</summary>
    public static bool Validate(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopSequentialPlan? plan)
    {
        if (!ValidateBoundFrontier(frontier, binding)
            || !MatchesPlanBinding(binding, plan)
            || frontier!.Payload.Nodes.Count < 2)
        {
            return false;
        }

        var nodes = frontier.Payload.Nodes;
        for (var index = 0; index < nodes.Count; index++)
        {
            var activation = nodes[index];
            if (activation.ActivationOrdinal != index
                || activation.PlanOrdinal < 0
                || activation.PlanOrdinal >= plan!.Nodes.Count
                || !MatchesPlanNode(activation, plan.Nodes[activation.PlanOrdinal])
                || !HasExactRoute(plan, activation))
            {
                return false;
            }

            if (index > 0 && !HasCausalAdmission(plan, nodes, activation))
            {
                return false;
            }
        }

        var trigger = nodes[0];
        if (trigger.PlanOrdinal != 0
            || trigger.Status != GovernedLoopNodeExecutionStatus.Completed
            || trigger.Attempt != 1
            || trigger.AttemptOperationId is null
            || trigger.OutcomeEvidenceId is null
            || trigger.OutcomeEvidenceHash is null
            || trigger.ControlOutcome != GovernedLoopControlCondition.Always)
        {
            return false;
        }

        return frontier.Payload.Status switch
        {
            GovernedLoopFrontierStatus.Active => nodes.Any(node => node.Status is GovernedLoopNodeExecutionStatus.Ready or GovernedLoopNodeExecutionStatus.Running),
            GovernedLoopFrontierStatus.ReviewBlocked => nodes.All(node => node.Status != GovernedLoopNodeExecutionStatus.Running)
                && (nodes.Any(node => node.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked)
                    || nodes.Any(node => node.Status == GovernedLoopNodeExecutionStatus.Ready)),
            GovernedLoopFrontierStatus.Completed => nodes.Any(node => node.Status == GovernedLoopNodeExecutionStatus.Completed && node.Descriptor.Kind == GovernedLoopNodeKind.Exit),
            GovernedLoopFrontierStatus.Failed => nodes.Any(node => node.Status == GovernedLoopNodeExecutionStatus.Failed),
            GovernedLoopFrontierStatus.Cancelled => true,
            _ => false,
        };
    }

    /// <summary>Selects the exact Running activation for reconciliation, or the deterministically lowest Ready activation.</summary>
    public static GovernedLoopSequentialFrontierSelectionResult Select(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopSequentialPlan? plan)
    {
        if (!Validate(frontier, binding, plan))
        {
            return Selection(GovernedLoopSequentialFrontierSelectionStatus.Invalid, null, null, null, null, "The canonical frontier is missing, corrupt, substituted, or not an exact admitted activation history.");
        }

        if (frontier!.Payload.Status == GovernedLoopFrontierStatus.ReviewBlocked)
        {
            return Selection(GovernedLoopSequentialFrontierSelectionStatus.ReviewBlocked, null, null, null, null, "The canonical frontier is durably blocked on review.");
        }

        if (frontier.Payload.Status is GovernedLoopFrontierStatus.Completed or GovernedLoopFrontierStatus.Failed or GovernedLoopFrontierStatus.Cancelled)
        {
            return Selection(GovernedLoopSequentialFrontierSelectionStatus.Terminal, null, null, null, null, "The canonical frontier is terminal.");
        }

        var running = frontier.Payload.Nodes.SingleOrDefault(node => node.Status == GovernedLoopNodeExecutionStatus.Running);
        if (running is not null)
        {
            return Selection(
                GovernedLoopSequentialFrontierSelectionStatus.Running,
                plan!.Nodes[running.PlanOrdinal],
                running,
                running.Attempt,
                running.AttemptOperationId,
                "The exact Running activation requires evidence-only reconciliation and cannot be redispatched.");
        }

        var ready = frontier.Payload.Nodes
            .Where(node => node.Status == GovernedLoopNodeExecutionStatus.Ready)
            .OrderBy(node => plan!.Nodes[node.PlanOrdinal].StaticOrdinal)
            .ThenBy(node => node.NodeId, StringComparer.Ordinal)
            .ThenBy(node => node.ActivationOrdinal)
            .FirstOrDefault();
        return ready is null
            ? Selection(GovernedLoopSequentialFrontierSelectionStatus.Invalid, null, null, null, null, "An active frontier contains no deterministic Ready activation.")
            : Selection(GovernedLoopSequentialFrontierSelectionStatus.Ready, plan!.Nodes[ready.PlanOrdinal], ready, null, null, "The exact lowest admitted Ready activation was selected deterministically.");
    }

    /// <summary>Returns the exact Ready activations that require append-once skip evidence for a committed route.</summary>
    public static GovernedLoopSequentialPruningPlanResult PlanPruning(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopSequentialPlan? plan,
        GovernedLoopNodeExecutionEvidence? activation,
        GovernedLoopControlCondition controlOutcome)
    {
        var selected = Select(frontier, binding, plan);
        if (selected.Status != GovernedLoopSequentialFrontierSelectionStatus.Running
            || !SameActivation(selected.Activation, activation)
            || !TryResolveRoute(plan!, selected.Node!, controlOutcome, out _, out var skippedEdges))
        {
            return new GovernedLoopSequentialPruningPlanResult(GovernedLoopSequentialFrontierTransitionStatus.Invalid, [], "Only the exact Running activation and one admitted control outcome can plan pruning.");
        }

        var pruned = frontier!.Payload.Nodes
            .Where(candidate => candidate.Status == GovernedLoopNodeExecutionStatus.Ready
                && candidate.Descriptor.Kind != GovernedLoopNodeKind.Join)
            .Select(candidate => new
            {
                Activation = candidate,
                GoverningEdge = candidate.IncomingControlEdgeIds.Intersect(skippedEdges, StringComparer.Ordinal).Order(StringComparer.Ordinal).FirstOrDefault(),
            })
            .Where(candidate => candidate.GoverningEdge is not null)
            .Select(candidate => new GovernedLoopSequentialPrunedActivation(candidate.Activation, activation!.ActivationOrdinal, candidate.GoverningEdge!))
            .OrderBy(candidate => candidate.Activation.ActivationOrdinal)
            .ToArray();
        return new GovernedLoopSequentialPruningPlanResult(GovernedLoopSequentialFrontierTransitionStatus.Applied, pruned, "The exact append-once topology-pruning evidence set was derived from the committed route.");
    }

    /// <summary>Claims only the exact selected Ready activation at attempt one.</summary>
    public static GovernedLoopSequentialFrontierTransitionResult Start(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopSequentialPlan? plan,
        GovernedLoopSequentialPlanNode? node,
        GovernedLoopNodeExecutionEvidence? activation,
        int attempt,
        string? attemptOperationId,
        DateTimeOffset updatedAtUtc)
    {
        var selected = Select(frontier, binding, plan);
        if (selected.Status != GovernedLoopSequentialFrontierSelectionStatus.Ready
            || !SamePlanNode(selected.Node, node)
            || !SameActivation(selected.Activation, activation)
            || attempt != 1)
        {
            return Invalid("Schema-1 execution can claim only the exact deterministic Ready activation at attempt one.");
        }

        try
        {
            var running = CopyActivation(activation!, GovernedLoopNodeExecutionStatus.Running, attempt, attemptOperationId, null, null, null, [], []);
            var successor = ReplaceActivation(frontier!, binding!, running, GovernedLoopFrontierStatus.Active, updatedAtUtc);
            return TransitionIsValid(frontier!, successor, binding, plan)
                ? Applied(successor, "The exact selected activation entered Running before dispatch.")
                : Invalid("The Ready-to-Running successor violates the canonical activation contract.");
        }
        catch (Exception exception) when (IsContractFailure(exception))
        {
            return Invalid($"The Ready-to-Running transition was rejected by its bounded contract: {exception.GetType().Name}.");
        }
    }

    /// <summary>Commits one exact successful Running outcome, route, pruning evidence, and newly eligible Ready activations.</summary>
    public static GovernedLoopSequentialFrontierTransitionResult CompleteRunning(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopSequentialPlan? plan,
        GovernedLoopSequentialPlanNode? node,
        GovernedLoopNodeExecutionEvidence? activation,
        int attempt,
        string? attemptOperationId,
        string? outcomeEvidenceId,
        string? outcomeEvidenceHash,
        GovernedLoopControlCondition controlOutcome,
        IReadOnlyList<GovernedLoopSequentialSkipEvidenceReference>? skipEvidence,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? cycleStartedAtUtc = null)
        => ResolveRunning(frontier, binding, plan, node, activation, attempt, attemptOperationId, GovernedLoopNodeExecutionStatus.Completed, outcomeEvidenceId, outcomeEvidenceHash, controlOutcome, skipEvidence, updatedAtUtc, cycleStartedAtUtc);

    /// <summary>Commits one exact definitive failed outcome without dispatching successors.</summary>
    public static GovernedLoopSequentialFrontierTransitionResult FailRunning(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopSequentialPlan? plan,
        GovernedLoopSequentialPlanNode? node,
        GovernedLoopNodeExecutionEvidence? activation,
        int attempt,
        string? attemptOperationId,
        string? outcomeEvidenceId,
        string? outcomeEvidenceHash,
        GovernedLoopControlCondition controlOutcome,
        DateTimeOffset updatedAtUtc)
        => ResolveRunning(frontier, binding, plan, node, activation, attempt, attemptOperationId, GovernedLoopNodeExecutionStatus.Failed, outcomeEvidenceId, outcomeEvidenceHash, controlOutcome, [], updatedAtUtc, null);

    /// <summary>Blocks one exact Running activation on review without route commitment or redispatch.</summary>
    public static GovernedLoopSequentialFrontierTransitionResult ReviewBlockRunning(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopSequentialPlan? plan,
        GovernedLoopSequentialPlanNode? node,
        GovernedLoopNodeExecutionEvidence? activation,
        int attempt,
        string? attemptOperationId,
        string? outcomeEvidenceId,
        string? outcomeEvidenceHash,
        DateTimeOffset updatedAtUtc)
        => ResolveRunning(frontier, binding, plan, node, activation, attempt, attemptOperationId, GovernedLoopNodeExecutionStatus.ReviewBlocked, outcomeEvidenceId, outcomeEvidenceHash, null, [], updatedAtUtc, null);

    /// <summary>Atomically claims the exact selected Ready activation and blocks it on review with authenticated outcome evidence.</summary>
    public static GovernedLoopSequentialFrontierTransitionResult ReviewBlockReady(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopSequentialPlan? plan,
        GovernedLoopSequentialPlanNode? node,
        GovernedLoopNodeExecutionEvidence? activation,
        int attempt,
        string? attemptOperationId,
        string? outcomeEvidenceId,
        string? outcomeEvidenceHash,
        DateTimeOffset updatedAtUtc)
    {
        var selected = Select(frontier, binding, plan);
        if (selected.Status != GovernedLoopSequentialFrontierSelectionStatus.Ready
            || !SamePlanNode(selected.Node, node)
            || !SameActivation(selected.Activation, activation)
            || attempt != 1)
        {
            return Invalid("Schema-1 execution can atomically review-block only the exact deterministic Ready activation at attempt one.");
        }

        try
        {
            var blocked = CopyActivation(
                activation!,
                GovernedLoopNodeExecutionStatus.ReviewBlocked,
                attempt,
                attemptOperationId,
                outcomeEvidenceId,
                outcomeEvidenceHash,
                null,
                [],
                []);
            var successor = ReplaceActivation(frontier!, binding!, blocked, GovernedLoopFrontierStatus.ReviewBlocked, updatedAtUtc);
            return TransitionIsValid(frontier!, successor, binding, plan)
                ? Applied(successor, "The exact selected Ready activation retained one atomic attempt and review outcome without an intermediate durable dispatch posture.")
                : Invalid("The atomic Ready-to-ReviewBlocked successor violates the canonical activation contract.");
        }
        catch (Exception exception) when (IsContractFailure(exception))
        {
            return Invalid($"The atomic Ready-to-ReviewBlocked transition was rejected by its bounded contract: {exception.GetType().Name}.");
        }
    }

    /// <summary>Fails the sole exact active activation without selecting among ambiguous parallel Ready work.</summary>
    public static GovernedLoopSequentialFrontierTransitionResult FailCurrent(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        string? attemptOperationId,
        string? outcomeEvidenceId,
        string? outcomeEvidenceHash,
        DateTimeOffset updatedAtUtc)
        => ResolveCurrentTerminal(frontier, binding, GovernedLoopNodeExecutionStatus.Failed, attemptOperationId, outcomeEvidenceId, outcomeEvidenceHash, null, [], [], updatedAtUtc);

    /// <summary>Fails the sole exact claimed activation while retaining its authenticated terminal route partition.</summary>
    public static GovernedLoopSequentialFrontierTransitionResult FailCurrent(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        string? attemptOperationId,
        string? outcomeEvidenceId,
        string? outcomeEvidenceHash,
        GovernedLoopControlCondition? controlOutcome,
        IReadOnlyList<string>? selectedControlEdgeIds,
        IReadOnlyList<string>? skippedControlEdgeIds,
        DateTimeOffset updatedAtUtc)
        => ResolveCurrentTerminal(
            frontier,
            binding,
            GovernedLoopNodeExecutionStatus.Failed,
            attemptOperationId,
            outcomeEvidenceId,
            outcomeEvidenceHash,
            controlOutcome,
            selectedControlEdgeIds,
            skippedControlEdgeIds,
            updatedAtUtc);

    /// <summary>Claims the sole exact Ready activation for a terminal-review boundary.</summary>
    public static GovernedLoopSequentialFrontierTransitionResult StartCurrent(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        string? attemptOperationId,
        DateTimeOffset updatedAtUtc)
    {
        if (!ValidateBoundFrontier(frontier, binding)
            || frontier!.Payload.Status != GovernedLoopFrontierStatus.Active
            || frontier.Payload.Nodes.Where(node => node.Status == GovernedLoopNodeExecutionStatus.Ready).ToArray() is not { Length: 1 } ready
            || frontier.Payload.Nodes.Any(node => node.Status == GovernedLoopNodeExecutionStatus.Running))
        {
            return Invalid("Terminal preparation requires one unambiguous exact Ready activation.");
        }

        try
        {
            var running = CopyActivation(ready[0], GovernedLoopNodeExecutionStatus.Running, 1, attemptOperationId, null, null, null, [], []);
            var successor = ReplaceActivation(frontier, binding!, running, GovernedLoopFrontierStatus.Active, updatedAtUtc);
            return GovernedLoopExecutionValidator.ValidateTransition(frontier, successor).IsValid
                ? Applied(successor, "The sole exact Ready activation entered Running for a terminal-review boundary.")
                : Invalid("The bound Ready-to-Running successor violates the activation contract.");
        }
        catch (Exception exception) when (IsContractFailure(exception))
        {
            return Invalid($"The bound Ready-to-Running transition was rejected by its bounded contract: {exception.GetType().Name}.");
        }
    }

    /// <summary>Blocks the sole exact Running activation on review without selecting or dispatching work.</summary>
    public static GovernedLoopSequentialFrontierTransitionResult ReviewBlockCurrent(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        string? outcomeEvidenceId,
        string? outcomeEvidenceHash,
        DateTimeOffset updatedAtUtc)
        => ResolveCurrentTerminal(frontier, binding, GovernedLoopNodeExecutionStatus.ReviewBlocked, null, outcomeEvidenceId, outcomeEvidenceHash, null, [], [], updatedAtUtc);

    /// <summary>Blocks the sole exact claimed activation while retaining an already-authenticated terminal route partition.</summary>
    public static GovernedLoopSequentialFrontierTransitionResult ReviewBlockCurrent(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        string? outcomeEvidenceId,
        string? outcomeEvidenceHash,
        GovernedLoopControlCondition? controlOutcome,
        IReadOnlyList<string>? selectedControlEdgeIds,
        IReadOnlyList<string>? skippedControlEdgeIds,
        DateTimeOffset updatedAtUtc)
        => ResolveCurrentTerminal(
            frontier,
            binding,
            GovernedLoopNodeExecutionStatus.ReviewBlocked,
            null,
            outcomeEvidenceId,
            outcomeEvidenceHash,
            controlOutcome,
            selectedControlEdgeIds,
            skippedControlEdgeIds,
            updatedAtUtc);

    /// <summary>Blocks an undispatched aggregate frontier for review without claiming or rewriting any Ready activation.</summary>
    public static GovernedLoopSequentialFrontierTransitionResult ReviewBlockAggregate(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        DateTimeOffset updatedAtUtc)
    {
        if (!ValidateBoundFrontier(frontier, binding)
            || frontier!.Payload.Status != GovernedLoopFrontierStatus.Active
            || frontier.Payload.Nodes.Any(node => node.Status == GovernedLoopNodeExecutionStatus.Running)
            || frontier.Payload.Nodes.All(node => node.Status != GovernedLoopNodeExecutionStatus.Ready))
        {
            return Invalid("Only an active undispatched frontier with retained Ready work can enter aggregate review.");
        }

        try
        {
            var successor = CreatePosture(
                binding!,
                checked(frontier.Payload.FrontierVersion + 1),
                GovernedLoopFrontierStatus.ReviewBlocked,
                frontier.Payload.Nodes,
                updatedAtUtc);
            return GovernedLoopExecutionValidator.ValidateTransition(frontier, successor).IsValid
                ? Applied(successor, "The undispatched activation history entered aggregate review without inventing a node attempt.")
                : Invalid("The aggregate review successor violates the bound frontier transition contract.");
        }
        catch (Exception exception) when (IsContractFailure(exception))
        {
            return Invalid($"The aggregate review transition was rejected by its bounded contract: {exception.GetType().Name}.");
        }
    }

    /// <summary>Cancels an exact bound activation history without rewriting reached evidence.</summary>
    public static GovernedLoopSequentialFrontierTransitionResult CancelCurrent(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        DateTimeOffset updatedAtUtc)
    {
        if (!ValidateBoundFrontier(frontier, binding)
            || frontier!.Payload.Status is GovernedLoopFrontierStatus.Completed or GovernedLoopFrontierStatus.Failed or GovernedLoopFrontierStatus.Cancelled)
        {
            return Invalid("Only a valid nonterminal bound canonical frontier can be cancelled.");
        }

        try
        {
            var successor = CreatePosture(binding!, checked(frontier.Payload.FrontierVersion + 1), GovernedLoopFrontierStatus.Cancelled, frontier.Payload.Nodes, updatedAtUtc);
            return GovernedLoopExecutionValidator.ValidateTransition(frontier, successor).IsValid
                ? Applied(successor, "The canonical activation history entered Cancelled without rewriting evidence.")
                : Invalid("The bound canonical cancellation violates the frontier transition contract.");
        }
        catch (Exception exception) when (IsContractFailure(exception))
        {
            return Invalid($"The bound cancellation was rejected by its bounded contract: {exception.GetType().Name}.");
        }
    }

    /// <summary>Commits aggregate cancellation after exact plan validation.</summary>
    public static GovernedLoopSequentialFrontierTransitionResult Cancel(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopSequentialPlan? plan,
        DateTimeOffset updatedAtUtc)
    {
        if (!Validate(frontier, binding, plan))
        {
            return Invalid("Only an exact canonical topology frontier can be cancelled.");
        }

        var cancelled = CancelCurrent(frontier, binding, updatedAtUtc);
        return cancelled.Status == GovernedLoopSequentialFrontierTransitionStatus.Applied && Validate(cancelled.Frontier, binding, plan)
            ? cancelled
            : Invalid(cancelled.Detail);
    }

    internal static bool IsUndispatchedReadyCheckpoint(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding)
        => ValidateBoundFrontier(frontier, binding)
            && frontier!.Payload.Status == GovernedLoopFrontierStatus.Active
            && frontier.Payload.Nodes.Any(node => node.Status == GovernedLoopNodeExecutionStatus.Ready)
            && frontier.Payload.Nodes.All(node => node.Status != GovernedLoopNodeExecutionStatus.Running);

    private static GovernedLoopSequentialFrontierTransitionResult ResolveRunning(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopSequentialPlan? plan,
        GovernedLoopSequentialPlanNode? node,
        GovernedLoopNodeExecutionEvidence? activation,
        int attempt,
        string? attemptOperationId,
        GovernedLoopNodeExecutionStatus resolution,
        string? outcomeEvidenceId,
        string? outcomeEvidenceHash,
        GovernedLoopControlCondition? controlOutcome,
        IReadOnlyList<GovernedLoopSequentialSkipEvidenceReference>? skipEvidence,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? cycleStartedAtUtc)
    {
        var selected = Select(frontier, binding, plan);
        if (selected.Status != GovernedLoopSequentialFrontierSelectionStatus.Running
            || !SamePlanNode(selected.Node, node)
            || !SameActivation(selected.Activation, activation)
            || selected.Attempt != attempt
            || !string.Equals(selected.AttemptOperationId, attemptOperationId, StringComparison.Ordinal)
            || resolution is not (GovernedLoopNodeExecutionStatus.Completed or GovernedLoopNodeExecutionStatus.Failed or GovernedLoopNodeExecutionStatus.ReviewBlocked))
        {
            return Invalid("Only the exact committed Running activation can resolve the canonical frontier.");
        }

        if (resolution == GovernedLoopNodeExecutionStatus.ReviewBlocked)
        {
            if (controlOutcome is not null || skipEvidence is { Count: > 0 })
            {
                return Invalid("Review-blocked evidence cannot expose a control route or prune Ready activations.");
            }
        }
        else if (controlOutcome is null || !TryResolveRoute(
            plan!,
            node!,
            controlOutcome.Value,
            out _,
            out _,
            allowUnroutedFailure: resolution == GovernedLoopNodeExecutionStatus.Failed))
        {
            return Invalid("A terminal completed or failed activation requires one exact admitted control route.");
        }

        try
        {
            var selectedEdges = Array.Empty<string>();
            var skippedEdges = Array.Empty<string>();
            if (controlOutcome is { } route)
            {
                _ = TryResolveRoute(
                    plan!,
                    node!,
                    route,
                    out selectedEdges,
                    out skippedEdges,
                    allowUnroutedFailure: resolution == GovernedLoopNodeExecutionStatus.Failed);
            }

            var resolved = CopyActivation(activation!, resolution, attempt, attemptOperationId, outcomeEvidenceId, outcomeEvidenceHash, controlOutcome, selectedEdges, skippedEdges);
            var nodes = frontier!.Payload.Nodes.ToList();
            nodes[resolved.ActivationOrdinal] = resolved;
            if (resolution == GovernedLoopNodeExecutionStatus.Completed)
            {
                var pruning = ExpectedPruning(nodes, resolved, skippedEdges);
                if (!TryApplyPruning(nodes, pruning, skipEvidence, out var pruningFailure))
                {
                    return Invalid(pruningFailure!);
                }

                if (!TryAppendEligibleSuccessors(plan!, nodes, resolved, updatedAtUtc, cycleStartedAtUtc, out var successorFailure))
                {
                    return Invalid(successorFailure!);
                }

                if (!TryAppendNewlyEligibleJoins(plan!, nodes, updatedAtUtc, cycleStartedAtUtc, out successorFailure))
                {
                    return Invalid(successorFailure!);
                }
            }

            var aggregate = resolution switch
            {
                GovernedLoopNodeExecutionStatus.ReviewBlocked => GovernedLoopFrontierStatus.ReviewBlocked,
                GovernedLoopNodeExecutionStatus.Failed => GovernedLoopFrontierStatus.Failed,
                GovernedLoopNodeExecutionStatus.Completed when nodes.Any(item => item.Status is GovernedLoopNodeExecutionStatus.Ready or GovernedLoopNodeExecutionStatus.Running) => GovernedLoopFrontierStatus.Active,
                GovernedLoopNodeExecutionStatus.Completed when resolved.Descriptor.Kind == GovernedLoopNodeKind.Exit => GovernedLoopFrontierStatus.Completed,
                _ => throw new InvalidOperationException("A completed non-Exit route ended without an eligible successor."),
            };
            var successor = CreatePosture(binding!, checked(frontier.Payload.FrontierVersion + 1), aggregate, nodes, updatedAtUtc);
            return TransitionIsValid(frontier, successor, binding, plan)
                ? Applied(successor, "The exact outcome, route, pruning evidence, and eligible successors committed atomically.")
                : Invalid("The Running resolution violates the exact canonical frontier transition contract.");
        }
        catch (Exception exception) when (IsContractFailure(exception))
        {
            return Invalid($"The Running resolution was rejected by its bounded contract: {exception.GetType().Name}.");
        }
    }

    private static GovernedLoopSequentialFrontierTransitionResult ResolveCurrentTerminal(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopNodeExecutionStatus resolution,
        string? attemptOperationId,
        string? outcomeEvidenceId,
        string? outcomeEvidenceHash,
        GovernedLoopControlCondition? controlOutcome,
        IReadOnlyList<string>? selectedControlEdgeIds,
        IReadOnlyList<string>? skippedControlEdgeIds,
        DateTimeOffset updatedAtUtc)
    {
        if (!ValidateBoundFrontier(frontier, binding)
            || frontier!.Payload.Status is not (GovernedLoopFrontierStatus.Active or GovernedLoopFrontierStatus.ReviewBlocked))
        {
            return Invalid("Only a valid active bound frontier can enter a terminal posture.");
        }

        var claimed = frontier.Payload.Nodes.Where(node => node.Status is GovernedLoopNodeExecutionStatus.Running or GovernedLoopNodeExecutionStatus.ReviewBlocked).ToArray();
        var candidates = claimed.Length == 0
            ? frontier.Payload.Nodes.Where(node => node.Status == GovernedLoopNodeExecutionStatus.Ready).ToArray()
            : claimed;
        if (candidates.Length != 1)
        {
            return Invalid("Terminal fallback cannot choose among multiple exact active activations.");
        }

        var current = candidates[0];
        var permitted = resolution switch
        {
            GovernedLoopNodeExecutionStatus.Failed => current.Status is GovernedLoopNodeExecutionStatus.Ready or GovernedLoopNodeExecutionStatus.Running or GovernedLoopNodeExecutionStatus.ReviewBlocked,
            GovernedLoopNodeExecutionStatus.ReviewBlocked => frontier.Payload.Status == GovernedLoopFrontierStatus.Active && current.Status == GovernedLoopNodeExecutionStatus.Running,
            _ => false,
        };
        if (!permitted)
        {
            return Invalid("The exact active activation cannot enter the requested terminal posture.");
        }

        try
        {
            var selectedEdges = selectedControlEdgeIds?.ToArray() ?? [];
            var skippedEdges = skippedControlEdgeIds?.ToArray() ?? [];
            if (controlOutcome is GovernedLoopControlCondition.Unknown
                || controlOutcome is { } suppliedOutcome && !Enum.IsDefined(suppliedOutcome)
                || controlOutcome is null && (selectedEdges.Length != 0 || skippedEdges.Length != 0)
                || controlOutcome is not null
                    && (!selectedEdges.Concat(skippedEdges).Order(StringComparer.Ordinal).SequenceEqual(current.OutgoingControlEdgeIds, StringComparer.Ordinal)
                        || selectedEdges.Intersect(skippedEdges, StringComparer.Ordinal).Any()))
            {
                return Invalid("Terminal fallback route evidence does not exactly partition the claimed activation's admitted outgoing edges.");
            }

            var attempt = current.Status == GovernedLoopNodeExecutionStatus.Ready ? 1 : current.Attempt;
            var operationId = current.Status == GovernedLoopNodeExecutionStatus.Ready ? attemptOperationId : current.AttemptOperationId;
            var replacement = CopyActivation(current, resolution, attempt, operationId, outcomeEvidenceId, outcomeEvidenceHash, controlOutcome, selectedEdges, skippedEdges);
            var aggregate = resolution == GovernedLoopNodeExecutionStatus.Failed ? GovernedLoopFrontierStatus.Failed : GovernedLoopFrontierStatus.ReviewBlocked;
            var successor = ReplaceActivation(frontier, binding!, replacement, aggregate, updatedAtUtc);
            return GovernedLoopExecutionValidator.ValidateTransition(frontier, successor).IsValid
                ? Applied(successor, "The sole exact active activation entered its durable terminal posture.")
                : Invalid("The bound terminal successor violates the activation transition contract.");
        }
        catch (Exception exception) when (IsContractFailure(exception))
        {
            return Invalid($"The bound terminal transition was rejected by its bounded contract: {exception.GetType().Name}.");
        }
    }

    private static IReadOnlyList<GovernedLoopSequentialPrunedActivation> ExpectedPruning(
        IReadOnlyList<GovernedLoopNodeExecutionEvidence> nodes,
        GovernedLoopNodeExecutionEvidence governing,
        IReadOnlyList<string> skippedEdges)
        => nodes
            .Where(candidate => candidate.Status == GovernedLoopNodeExecutionStatus.Ready
                && candidate.Descriptor.Kind != GovernedLoopNodeKind.Join)
            .Select(candidate => new
            {
                Activation = candidate,
                Edge = candidate.IncomingControlEdgeIds.Intersect(skippedEdges, StringComparer.Ordinal).Order(StringComparer.Ordinal).FirstOrDefault(),
            })
            .Where(candidate => candidate.Edge is not null)
            .Select(candidate => new GovernedLoopSequentialPrunedActivation(candidate.Activation, governing.ActivationOrdinal, candidate.Edge!))
            .OrderBy(candidate => candidate.Activation.ActivationOrdinal)
            .ToArray();

    private static bool TryApplyPruning(
        IList<GovernedLoopNodeExecutionEvidence> nodes,
        IReadOnlyList<GovernedLoopSequentialPrunedActivation> pruning,
        IReadOnlyList<GovernedLoopSequentialSkipEvidenceReference>? references,
        out string? failure)
    {
        failure = null;
        var supplied = references?.OrderBy(reference => reference.ActivationOrdinal).ToArray() ?? [];
        if (supplied.Length != pruning.Count || supplied.Select(reference => reference.ActivationOrdinal).Distinct().Count() != supplied.Length)
        {
            failure = "Every exact pruned Ready activation requires one unique append-once skip-evidence reference.";
            return false;
        }

        for (var index = 0; index < pruning.Count; index++)
        {
            var expected = pruning[index];
            var reference = supplied[index];
            if (reference.ActivationOrdinal != expected.Activation.ActivationOrdinal
                || reference.GoverningActivationOrdinal != expected.GoverningActivationOrdinal
                || !string.Equals(reference.GoverningControlEdgeId, expected.GoverningControlEdgeId, StringComparison.Ordinal))
            {
                failure = "Topology-pruning evidence does not match the exact activation and governing skipped edge.";
                return false;
            }

            nodes[reference.ActivationOrdinal] = CopyActivation(expected.Activation, GovernedLoopNodeExecutionStatus.Skipped, null, null, reference.OutcomeEvidenceId, reference.OutcomeEvidenceHash, null, [], []);
        }

        return true;
    }

    private static bool TryAppendEligibleSuccessors(
        GovernedLoopSequentialPlan plan,
        List<GovernedLoopNodeExecutionEvidence> nodes,
        GovernedLoopNodeExecutionEvidence source,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? cycleStartedAtUtc,
        out string? failure)
    {
        failure = null;
        foreach (var edgeId in source.SelectedControlEdgeIds)
        {
            var edge = plan.ControlEdges.Single(candidate => string.Equals(candidate.Id, edgeId, StringComparison.Ordinal));
            var target = plan.Nodes.Single(candidate => string.Equals(candidate.NodeId, edge.ToNodeId, StringComparison.Ordinal));
            if (!TryGetTargetCycleIteration(plan, source, target, out var cycleIteration, out failure))
            {
                return false;
            }

            if (HasExistingActivation(nodes, target, cycleIteration))
            {
                continue;
            }

            var arrivals = ResolveJoinArrivals(plan, nodes, target, cycleIteration);
            if (!IsEligibleTarget(plan, nodes, target, arrivals, cycleIteration))
            {
                continue;
            }

            var entersCycle = target.CycleId is not null
                && !string.Equals(source.CycleId, target.CycleId, StringComparison.Ordinal);
            if (!WithinCycleBudget(plan, target, cycleIteration, entersCycle, updatedAtUtc, cycleStartedAtUtc, out failure))
            {
                return false;
            }

            if (nodes.Count >= GovernedLoopExecutionLimits.MaxFrontierNodes)
            {
                failure = "The bounded activation-history budget was exhausted before another activation could become Ready.";
                return false;
            }

            var visit = nodes.Count(candidate => string.Equals(candidate.NodeId, target.NodeId, StringComparison.Ordinal)) + 1;
            nodes.Add(CreateActivation(
                target,
                nodes.Count,
                visit,
                cycleIteration,
                GovernedLoopNodeExecutionStatus.Ready,
                null,
                null,
                null,
                null,
                null,
                [],
                [],
                arrivals));
        }

        return true;
    }

    private static bool TryGetTargetCycleIteration(
        GovernedLoopSequentialPlan plan,
        GovernedLoopNodeExecutionEvidence source,
        GovernedLoopSequentialPlanNode target,
        out int? cycleIteration,
        out string? failure)
    {
        failure = null;
        cycleIteration = null;
        if (target.CycleId is null)
        {
            return true;
        }

        if (!string.Equals(source.CycleId, target.CycleId, StringComparison.Ordinal))
        {
            cycleIteration = 1;
            return true;
        }

        if (source.CycleIteration is not { } sourceIteration)
        {
            failure = "An internal cycle edge lacks exact source-iteration evidence.";
            return false;
        }

        var sourcePlan = plan.Nodes[source.PlanOrdinal];
        cycleIteration = target.ComponentTraversalOrdinal > sourcePlan.ComponentTraversalOrdinal
            ? sourceIteration
            : checked(sourceIteration + 1);
        return true;
    }

    private static bool TryAppendNewlyEligibleJoins(
        GovernedLoopSequentialPlan plan,
        List<GovernedLoopNodeExecutionEvidence> nodes,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? cycleStartedAtUtc,
        out string? failure)
    {
        failure = null;
        var candidates = plan.Nodes
            .Where(target => target.Descriptor.Kind == GovernedLoopNodeKind.Join)
            .SelectMany(target => target.IncomingControlEdgeIds
                .SelectMany(edgeId => nodes
                    .Where(source => source.Status == GovernedLoopNodeExecutionStatus.Completed
                        && source.SelectedControlEdgeIds.Contains(edgeId, StringComparer.Ordinal)
                        && TryGetTargetCycleIteration(plan, source, target, out _, out _))
                    .Select(source =>
                    {
                        _ = TryGetTargetCycleIteration(plan, source, target, out var cycleIteration, out _);
                        return (Target: target, CycleIteration: cycleIteration);
                    })))
            .DistinctBy(candidate => (candidate.Target.Ordinal, candidate.CycleIteration))
            .OrderBy(candidate => candidate.Target.StaticOrdinal)
            .ThenBy(candidate => candidate.CycleIteration)
            .ToArray();
        foreach (var candidate in candidates)
        {
            var target = candidate.Target;
            var cycleIteration = candidate.CycleIteration;
            if (HasExistingActivation(nodes, target, cycleIteration))
            {
                continue;
            }

            var arrivals = ResolveJoinArrivals(plan, nodes, target, cycleIteration);
            if (!IsEligibleTarget(plan, nodes, target, arrivals, cycleIteration))
            {
                continue;
            }

            var source = nodes[arrivals[0].SourceActivationOrdinal];
            var entersCycle = target.CycleId is not null
                && !string.Equals(source.CycleId, target.CycleId, StringComparison.Ordinal);
            if (!WithinCycleBudget(plan, target, cycleIteration, entersCycle, updatedAtUtc, cycleStartedAtUtc, out failure))
            {
                return false;
            }

            if (nodes.Count >= GovernedLoopExecutionLimits.MaxFrontierNodes)
            {
                failure = "The bounded activation-history budget was exhausted before another Join activation could become Ready.";
                return false;
            }

            var visit = nodes.Count(item => string.Equals(item.NodeId, target.NodeId, StringComparison.Ordinal)) + 1;
            nodes.Add(CreateActivation(
                target,
                nodes.Count,
                visit,
                cycleIteration,
                GovernedLoopNodeExecutionStatus.Ready,
                null,
                null,
                null,
                null,
                null,
                [],
                [],
                arrivals));
        }

        return true;
    }

    private static bool WithinCycleBudget(
        GovernedLoopSequentialPlan plan,
        GovernedLoopSequentialPlanNode target,
        int? cycleIteration,
        bool entersCycle,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? cycleStartedAtUtc,
        out string? failure)
    {
        failure = null;
        if (target.CycleId is null || cycleIteration is not { } iteration)
        {
            return true;
        }

        var component = plan.Components.Single(candidate => string.Equals(candidate.CycleId, target.CycleId, StringComparison.Ordinal));
        if (component.MaximumIterations is not { } maximumIterations || iteration > maximumIterations)
        {
            failure = "The admitted cycle iteration budget was exhausted before another activation could become Ready.";
            return false;
        }

        if (component.MaximumDurationMilliseconds is not { } maximumDuration)
        {
            failure = "The admitted cycle has no finite durable time budget.";
            return false;
        }

        if (!entersCycle
            && (cycleStartedAtUtc is null
                || updatedAtUtc < cycleStartedAtUtc
                || (long)(updatedAtUtc - cycleStartedAtUtc.Value).TotalMilliseconds > maximumDuration))
        {
            failure = "The admitted durable cycle time budget was exhausted or could not be proven before another activation became Ready.";
            return false;
        }

        return true;
    }

    private static IReadOnlyList<GovernedLoopJoinArrivalEvidence> ResolveJoinArrivals(
        GovernedLoopSequentialPlan plan,
        IReadOnlyList<GovernedLoopNodeExecutionEvidence> nodes,
        GovernedLoopSequentialPlanNode target,
        int? targetCycleIteration)
    {
        if (target.Descriptor.Kind != GovernedLoopNodeKind.Join)
        {
            return [];
        }

        var arrivals = new List<GovernedLoopJoinArrivalEvidence>();
        foreach (var edgeId in target.IncomingControlEdgeIds)
        {
            var source = nodes
                .Where(candidate => candidate.Status == GovernedLoopNodeExecutionStatus.Completed
                    && candidate.ActivationOrdinal < nodes.Count
                    && candidate.SelectedControlEdgeIds.Contains(edgeId, StringComparer.Ordinal)
                    && SourceReachesTargetIteration(plan, candidate, target, targetCycleIteration))
                .OrderByDescending(candidate => candidate.ActivationOrdinal)
                .FirstOrDefault();
            if (source is not null)
            {
                arrivals.Add(GovernedLoopJoinArrivalEvidence.Create(1, edgeId, source.ActivationOrdinal));
            }
        }

        return arrivals.OrderBy(arrival => arrival.ControlEdgeId, StringComparer.Ordinal).ToArray();
    }

    private static bool SourceReachesTargetIteration(
        GovernedLoopSequentialPlan plan,
        GovernedLoopNodeExecutionEvidence source,
        GovernedLoopSequentialPlanNode target,
        int? targetCycleIteration)
    {
        if (!TryGetTargetCycleIteration(plan, source, target, out var expectedIteration, out _))
        {
            return false;
        }

        return expectedIteration == targetCycleIteration;
    }

    private static bool IsEligibleTarget(
        GovernedLoopSequentialPlan plan,
        IReadOnlyList<GovernedLoopNodeExecutionEvidence> nodes,
        GovernedLoopSequentialPlanNode target,
        IReadOnlyList<GovernedLoopJoinArrivalEvidence> arrivals,
        int? targetCycleIteration)
    {
        if (target.Descriptor.Kind != GovernedLoopNodeKind.Join)
        {
            return true;
        }

        if (!GovernedLoopTopologyNodeCatalogContract.TryResolve(target.Descriptor, out var contract) || contract is null)
        {
            return false;
        }

        return contract.JoinPolicy switch
        {
            GovernedLoopJoinPolicy.Any => arrivals.Count > 0,
            GovernedLoopJoinPolicy.All => arrivals.Count == target.IncomingControlEdgeIds.Count,
            GovernedLoopJoinPolicy.Selected => arrivals.Count > 0 && target.IncomingControlEdgeIds.All(edgeId => arrivals.Any(arrival => string.Equals(arrival.ControlEdgeId, edgeId, StringComparison.Ordinal)) || IsControlEdgePruned(plan, nodes, edgeId, target, targetCycleIteration, [])),
            _ => false,
        };
    }

    private static bool IsControlEdgePruned(
        GovernedLoopSequentialPlan plan,
        IReadOnlyList<GovernedLoopNodeExecutionEvidence> nodes,
        string edgeId,
        GovernedLoopSequentialPlanNode target,
        int? targetCycleIteration,
        HashSet<(string EdgeId, int? TargetCycleIteration)> visited)
    {
        if (!visited.Add((edgeId, targetCycleIteration)))
        {
            return false;
        }

        var edge = plan.ControlEdges.Single(candidate => string.Equals(candidate.Id, edgeId, StringComparison.Ordinal));
        var source = plan.Nodes.Single(candidate => string.Equals(candidate.NodeId, edge.FromNodeId, StringComparison.Ordinal));
        if (!TryGetSourceCycleIteration(source, target, targetCycleIteration, out var sourceCycleIteration))
        {
            return false;
        }

        var exactSources = nodes
            .Where(candidate => candidate.PlanOrdinal == source.Ordinal
                && candidate.CycleIteration == sourceCycleIteration)
            .ToArray();
        if (exactSources.Length > 0)
        {
            return exactSources.Any(candidate => candidate.Status == GovernedLoopNodeExecutionStatus.Completed
                && candidate.SkippedControlEdgeIds.Contains(edgeId, StringComparer.Ordinal));
        }

        return source.IncomingControlEdgeIds.Count > 0
            && source.IncomingControlEdgeIds.All(incoming => IsControlEdgePruned(
                plan,
                nodes,
                incoming,
                source,
                sourceCycleIteration,
                new HashSet<(string EdgeId, int? TargetCycleIteration)>(visited)));
    }

    private static bool TryGetSourceCycleIteration(
        GovernedLoopSequentialPlanNode source,
        GovernedLoopSequentialPlanNode target,
        int? targetCycleIteration,
        out int? sourceCycleIteration)
    {
        sourceCycleIteration = null;
        if (source.CycleId is null)
        {
            return target.CycleId is null || targetCycleIteration == 1;
        }

        if (target.CycleId is null
            || !string.Equals(source.CycleId, target.CycleId, StringComparison.Ordinal)
            || targetCycleIteration is not { } targetIteration)
        {
            return false;
        }

        sourceCycleIteration = target.ComponentTraversalOrdinal > source.ComponentTraversalOrdinal
            ? targetIteration
            : targetIteration - 1;
        return sourceCycleIteration > 0;
    }

    private static bool HasExistingActivation(
        IReadOnlyList<GovernedLoopNodeExecutionEvidence> nodes,
        GovernedLoopSequentialPlanNode target,
        int? cycleIteration)
        => nodes.Any(candidate => candidate.PlanOrdinal == target.Ordinal
            && (target.CycleId is null || candidate.CycleIteration == cycleIteration));

    private static bool HasCausalAdmission(
        GovernedLoopSequentialPlan plan,
        IReadOnlyList<GovernedLoopNodeExecutionEvidence> nodes,
        GovernedLoopNodeExecutionEvidence activation)
    {
        var prior = nodes.Take(activation.ActivationOrdinal).ToArray();
        if (activation.Descriptor.Kind == GovernedLoopNodeKind.Join)
        {
            var target = plan.Nodes[activation.PlanOrdinal];
            if (!GovernedLoopTopologyNodeCatalogContract.TryResolve(target.Descriptor, out var contract) || contract is null)
            {
                return false;
            }

            var exactArrivals = ResolveJoinArrivals(plan, prior, target, activation.CycleIteration);
            return contract.JoinPolicy switch
            {
                GovernedLoopJoinPolicy.Any => activation.JoinArrivals.Count > 0
                    && activation.JoinArrivals.All(exactArrivals.Contains),
                GovernedLoopJoinPolicy.All => exactArrivals.SequenceEqual(activation.JoinArrivals)
                    && IsEligibleTarget(plan, prior, target, exactArrivals, activation.CycleIteration),
                GovernedLoopJoinPolicy.Selected => exactArrivals.SequenceEqual(activation.JoinArrivals)
                    && IsEligibleTarget(plan, prior, target, exactArrivals, activation.CycleIteration),
                _ => false,
            };
        }

        return activation.IncomingControlEdgeIds.Any(edgeId => prior.Any(source => source.Status == GovernedLoopNodeExecutionStatus.Completed
            && source.SelectedControlEdgeIds.Contains(edgeId, StringComparer.Ordinal)
            && SourceReachesTargetIteration(plan, source, plan.Nodes[activation.PlanOrdinal], activation.CycleIteration)));
    }

    private static bool HasExactRoute(GovernedLoopSequentialPlan plan, GovernedLoopNodeExecutionEvidence activation)
    {
        if (activation.Status == GovernedLoopNodeExecutionStatus.Skipped)
        {
            return activation.ControlOutcome is null
                && activation.SelectedControlEdgeIds.Count == 0
                && activation.SkippedControlEdgeIds.Count == 0;
        }

        if (activation.ControlOutcome is null)
        {
            return activation.SelectedControlEdgeIds.Count == 0 && activation.SkippedControlEdgeIds.Count == 0;
        }

        return TryResolveRoute(
                plan,
                plan.Nodes[activation.PlanOrdinal],
                activation.ControlOutcome.Value,
                out var selected,
                out var skipped,
                allowUnroutedFailure: activation.Status == GovernedLoopNodeExecutionStatus.Failed)
            && activation.SelectedControlEdgeIds.SequenceEqual(selected, StringComparer.Ordinal)
            && activation.SkippedControlEdgeIds.SequenceEqual(skipped, StringComparer.Ordinal);
    }

    private static bool TryResolveRoute(
        GovernedLoopSequentialPlan plan,
        GovernedLoopSequentialPlanNode node,
        GovernedLoopControlCondition outcome,
        out string[] selected,
        out string[] skipped,
        bool allowUnroutedFailure = false)
    {
        selected = [];
        skipped = [];
        if (outcome == GovernedLoopControlCondition.Unknown || !Enum.IsDefined(outcome))
        {
            return false;
        }

        var outgoing = plan.ControlEdges
            .Where(edge => string.Equals(edge.FromNodeId, node.NodeId, StringComparison.Ordinal))
            .OrderBy(edge => edge.Id, StringComparer.Ordinal)
            .ToArray();
        if (!outgoing.Select(edge => edge.Id).SequenceEqual(node.OutgoingControlEdgeIds, StringComparer.Ordinal))
        {
            return false;
        }

        selected = outgoing.Where(edge => edge.Condition == outcome).Select(edge => edge.Id).ToArray();
        skipped = outgoing.Where(edge => edge.Condition != outcome).Select(edge => edge.Id).ToArray();
        return outgoing.Length == 0
            || selected.Length > 0
            || allowUnroutedFailure && outcome == GovernedLoopControlCondition.Failure;
    }

    private static GovernedLoopNodeExecutionEvidence CreateActivation(
        GovernedLoopSequentialPlanNode node,
        int activationOrdinal,
        int visitOrdinal,
        int? cycleIteration,
        GovernedLoopNodeExecutionStatus status,
        int? attempt,
        string? attemptOperationId,
        string? outcomeEvidenceId,
        string? outcomeEvidenceHash,
        GovernedLoopControlCondition? controlOutcome,
        IEnumerable<string> selectedControlEdgeIds,
        IEnumerable<string> skippedControlEdgeIds,
        IEnumerable<GovernedLoopJoinArrivalEvidence> joinArrivals)
        => GovernedLoopNodeExecutionEvidence.CreateActivation(
            activationOrdinal,
            node.Ordinal,
            visitOrdinal,
            node.NodeId,
            node.Descriptor,
            node.IncomingControlEdgeIds,
            node.OutgoingControlEdgeIds,
            status,
            attempt,
            attemptOperationId,
            outcomeEvidenceId,
            outcomeEvidenceHash,
            node.CycleId,
            node.CycleId is null ? null : cycleIteration,
            controlOutcome,
            selectedControlEdgeIds,
            skippedControlEdgeIds,
            joinArrivals);

    private static GovernedLoopNodeExecutionEvidence CopyActivation(
        GovernedLoopNodeExecutionEvidence source,
        GovernedLoopNodeExecutionStatus status,
        int? attempt,
        string? attemptOperationId,
        string? outcomeEvidenceId,
        string? outcomeEvidenceHash,
        GovernedLoopControlCondition? controlOutcome,
        IEnumerable<string> selectedControlEdgeIds,
        IEnumerable<string> skippedControlEdgeIds)
        => GovernedLoopNodeExecutionEvidence.CreateActivation(
            source.ActivationOrdinal,
            source.PlanOrdinal,
            source.VisitOrdinal,
            source.NodeId,
            source.Descriptor,
            source.IncomingControlEdgeIds,
            source.OutgoingControlEdgeIds,
            status,
            attempt,
            attemptOperationId,
            outcomeEvidenceId,
            outcomeEvidenceHash,
            source.CycleId,
            source.CycleIteration,
            controlOutcome,
            selectedControlEdgeIds,
            skippedControlEdgeIds,
            source.JoinArrivals);

    private static GovernedLoopFrontierPosture ReplaceActivation(
        GovernedLoopFrontierPosture current,
        GovernedLoopSequentialAdapterBinding binding,
        GovernedLoopNodeExecutionEvidence replacement,
        GovernedLoopFrontierStatus status,
        DateTimeOffset updatedAtUtc)
    {
        var nodes = current.Payload.Nodes.ToArray();
        nodes[replacement.ActivationOrdinal] = replacement;
        return CreatePosture(binding, checked(current.Payload.FrontierVersion + 1), status, nodes, updatedAtUtc);
    }

    private static GovernedLoopFrontierPosture CreatePosture(
        GovernedLoopSequentialAdapterBinding binding,
        long frontierVersion,
        GovernedLoopFrontierStatus status,
        IEnumerable<GovernedLoopNodeExecutionEvidence> nodes,
        DateTimeOffset updatedAtUtc)
    {
        var payload = GovernedLoopFrontierPayload.Create(1, frontierVersion, ConcurrencyCeiling, status, nodes, updatedAtUtc, string.Empty);
        return GovernedLoopFrontierPosture.Create(binding.ExecutionBinding, binding.WorkspaceId, binding.GraphArtifactHash, binding.GraphLayoutHash, binding.AdmissionReceiptHash, payload);
    }

    private static bool TransitionIsValid(
        GovernedLoopFrontierPosture current,
        GovernedLoopFrontierPosture successor,
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopSequentialPlan? plan)
        => Validate(successor, binding, plan)
            && GovernedLoopExecutionValidator.ValidateTransition(current, successor).IsValid;

    private static bool MatchesPlanBinding(GovernedLoopSequentialAdapterBinding? binding, GovernedLoopSequentialPlan? plan)
        => binding is not null
            && plan is not null
            && GovernedLoopSequentialContractValidator.Validate(binding).IsValid
            && plan.SchemaVersion == 1
            && Equals(plan.Revision, binding.ExecutionBinding.Revision)
            && string.Equals(plan.GraphArtifactHash, binding.GraphArtifactHash, StringComparison.Ordinal)
            && string.Equals(plan.GraphLayoutHash, binding.GraphLayoutHash, StringComparison.Ordinal)
            && plan.SchedulerPolicy.MaximumConcurrency == ConcurrencyCeiling
            && plan.SchedulerPolicy.ReadyOrdering == GovernedLoopTopologyReadyOrdering.StaticOrdinalThenNodeId
            && plan.Nodes.Count >= 2
            && plan.Nodes.Select((node, ordinal) => node.Ordinal == ordinal && node.StaticOrdinal == ordinal).All(matches => matches);

    private static bool ValidateBoundFrontier(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding)
        => frontier is not null
            && binding is not null
            && GovernedLoopSequentialContractValidator.Validate(binding).IsValid
            && GovernedLoopFrontierContractValidator.Validate(frontier).IsValid
            && GovernedLoopFrontierContractHash.Matches(frontier)
            && frontier.SchemaVersion == 1
            && string.Equals(frontier.WorkspaceId, binding.WorkspaceId, StringComparison.Ordinal)
            && Equals(frontier.Binding, binding.ExecutionBinding)
            && string.Equals(frontier.GraphArtifactHash, binding.GraphArtifactHash, StringComparison.Ordinal)
            && string.Equals(frontier.GraphLayoutHash, binding.GraphLayoutHash, StringComparison.Ordinal)
            && string.Equals(frontier.AdmissionReceiptHash, binding.AdmissionReceiptHash, StringComparison.Ordinal)
            && frontier.Payload.ConcurrencyCeiling == ConcurrencyCeiling;

    private static bool MatchesPlanNode(GovernedLoopNodeExecutionEvidence reached, GovernedLoopSequentialPlanNode planned)
        => reached.SchemaVersion == 1
            && reached.PlanOrdinal == planned.Ordinal
            && string.Equals(reached.NodeId, planned.NodeId, StringComparison.Ordinal)
            && Equals(reached.Descriptor, planned.Descriptor)
            && string.Equals(reached.CycleId, planned.CycleId, StringComparison.Ordinal)
            && reached.IncomingControlEdgeIds.SequenceEqual(planned.IncomingControlEdgeIds, StringComparer.Ordinal)
            && reached.OutgoingControlEdgeIds.SequenceEqual(planned.OutgoingControlEdgeIds, StringComparer.Ordinal);

    private static bool SamePlanNode(GovernedLoopSequentialPlanNode? left, GovernedLoopSequentialPlanNode? right)
        => left is not null
            && right is not null
            && left.Ordinal == right.Ordinal
            && string.Equals(left.NodeId, right.NodeId, StringComparison.Ordinal)
            && Equals(left.Descriptor, right.Descriptor);

    private static bool SameActivation(GovernedLoopNodeExecutionEvidence? left, GovernedLoopNodeExecutionEvidence? right)
        => left is not null
            && right is not null
            && left.ActivationOrdinal == right.ActivationOrdinal
            && left.PlanOrdinal == right.PlanOrdinal
            && left.VisitOrdinal == right.VisitOrdinal
            && string.Equals(left.NodeId, right.NodeId, StringComparison.Ordinal)
            && string.Equals(left.CycleId, right.CycleId, StringComparison.Ordinal)
            && left.CycleIteration == right.CycleIteration
            && left.Status == right.Status
            && left.Attempt == right.Attempt
            && string.Equals(left.AttemptOperationId, right.AttemptOperationId, StringComparison.Ordinal);

    private static GovernedLoopSequentialFrontierTransitionResult Applied(GovernedLoopFrontierPosture frontier, string detail)
        => new(GovernedLoopSequentialFrontierTransitionStatus.Applied, frontier, detail);

    private static GovernedLoopSequentialFrontierTransitionResult Invalid(string detail)
        => new(GovernedLoopSequentialFrontierTransitionStatus.Invalid, null, detail);

    private static GovernedLoopSequentialFrontierSelectionResult Selection(
        GovernedLoopSequentialFrontierSelectionStatus status,
        GovernedLoopSequentialPlanNode? node,
        GovernedLoopNodeExecutionEvidence? activation,
        int? attempt,
        string? operationId,
        string detail)
        => new(status, node, activation, attempt, operationId, detail);

    private static bool IsContractFailure(Exception exception)
        => exception is ArgumentException or InvalidOperationException or OverflowException;
}
