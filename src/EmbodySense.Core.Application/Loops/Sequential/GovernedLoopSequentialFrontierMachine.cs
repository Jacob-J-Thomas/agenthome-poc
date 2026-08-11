using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Creates, selects, and advances the deterministic concurrency-one frontier for the admitted sequential plan.</summary>
/// <remarks>
/// This pure policy does not persist, dispatch, grant authority, or infer progress from the legacy numeric checkpoint.
/// Every operation authenticates the exact adapter binding and a contiguous reached prefix of the immutable plan.
/// </remarks>
public static class GovernedLoopSequentialFrontierMachine
{
    private const int ConcurrencyCeiling = 1;

    /// <summary>Creates the initial Trigger-completed, first-executable-ready frontier from exact durable Trigger evidence.</summary>
    /// <param name="binding">The immutable canonical admission and execution binding.</param>
    /// <param name="plan">The deterministic plan rebuilt from the exact graph artifact.</param>
    /// <param name="triggerAttemptOperationId">The durable Trigger operation/event identity.</param>
    /// <param name="triggerOutcomeEvidenceId">The exact durable Trigger outcome event identity.</param>
    /// <param name="triggerOutcomeEvidenceHash">The exact retained Trigger outcome evidence hash.</param>
    /// <param name="updatedAtUtc">The UTC commit timestamp.</param>
    /// <returns>The initial posture, or an invalid result without a frontier.</returns>
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
            || !Equals(plan.Nodes[0].Descriptor, GovernedLoopSequentialNodeDescriptors.ManualTrigger)
            || Equals(plan.Nodes[1].Descriptor, GovernedLoopSequentialNodeDescriptors.ManualTrigger))
        {
            return Invalid("The immutable sequential binding and plan cannot form an initial frontier.");
        }

        try
        {
            var trigger = CreateNode(
                plan.Nodes[0],
                GovernedLoopNodeExecutionStatus.Completed,
                1,
                triggerAttemptOperationId,
                triggerOutcomeEvidenceId,
                triggerOutcomeEvidenceHash);
            var firstExecutable = CreateNode(
                plan.Nodes[1],
                GovernedLoopNodeExecutionStatus.Ready,
                null,
                null,
                null,
                null);
            var frontier = CreatePosture(
                binding!,
                1,
                GovernedLoopFrontierStatus.Active,
                [trigger, firstExecutable],
                updatedAtUtc);
            return Validate(frontier, binding, plan)
                ? Applied(frontier, "The completed Trigger and first executable Ready node formed the initial canonical frontier.")
                : Invalid("The initial canonical frontier failed exact plan-prefix validation.");
        }
        catch (Exception exception) when (IsContractFailure(exception))
        {
            return Invalid($"The initial canonical frontier was rejected by its bounded contract: {exception.GetType().Name}.");
        }
    }

    /// <summary>Returns whether the posture is an exact, hash-valid contiguous reached prefix of the admitted plan.</summary>
    public static bool Validate(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopSequentialPlan? plan)
    {
        if (frontier is null
            || !MatchesPlanBinding(binding, plan)
            || !GovernedLoopFrontierContractValidator.Validate(frontier).IsValid
            || !GovernedLoopFrontierContractHash.Matches(frontier)
            || frontier.SchemaVersion != 1
            || !string.Equals(frontier.WorkspaceId, binding!.WorkspaceId, StringComparison.Ordinal)
            || !Equals(frontier.Binding, binding.ExecutionBinding)
            || !string.Equals(frontier.GraphArtifactHash, binding.GraphArtifactHash, StringComparison.Ordinal)
            || !string.Equals(frontier.GraphLayoutHash, binding.GraphLayoutHash, StringComparison.Ordinal)
            || !string.Equals(frontier.AdmissionReceiptHash, binding.AdmissionReceiptHash, StringComparison.Ordinal)
            || frontier.Payload.ConcurrencyCeiling != ConcurrencyCeiling
            || frontier.Payload.Nodes.Count is < 2
            || frontier.Payload.Nodes.Count > plan!.Nodes.Count)
        {
            return false;
        }

        for (var index = 0; index < frontier.Payload.Nodes.Count; index++)
        {
            if (!MatchesPlanNode(frontier.Payload.Nodes[index], plan.Nodes[index]))
            {
                return false;
            }
        }

        var reached = frontier.Payload.Nodes;
        if (reached[0].Status != GovernedLoopNodeExecutionStatus.Completed
            || reached[0].Attempt != 1
            || reached[0].AttemptOperationId is null
            || reached[0].OutcomeEvidenceId is null
            || reached[0].OutcomeEvidenceHash is null
            || reached.Take(reached.Count - 1).Any(node => node.Status != GovernedLoopNodeExecutionStatus.Completed))
        {
            return false;
        }

        var last = reached[^1];
        return frontier.Payload.Status switch
        {
            GovernedLoopFrontierStatus.Active => last.Status is GovernedLoopNodeExecutionStatus.Ready or GovernedLoopNodeExecutionStatus.Running,
            GovernedLoopFrontierStatus.ReviewBlocked => last.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked,
            GovernedLoopFrontierStatus.Completed => reached.Count == plan.Nodes.Count && last.Status == GovernedLoopNodeExecutionStatus.Completed,
            GovernedLoopFrontierStatus.Failed => last.Status == GovernedLoopNodeExecutionStatus.Failed,
            GovernedLoopFrontierStatus.Cancelled => true,
            _ => false,
        };
    }

    /// <summary>Selects the one deterministic Ready node or identifies the one Running node that only reconciliation may continue.</summary>
    public static GovernedLoopSequentialFrontierSelectionResult Select(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopSequentialPlan? plan)
    {
        if (!Validate(frontier, binding, plan))
        {
            return Selection(GovernedLoopSequentialFrontierSelectionStatus.Invalid, null, null, null, "The canonical frontier is missing, corrupt, substituted, or not an exact admitted plan prefix.");
        }

        var last = frontier!.Payload.Nodes[^1];
        var node = plan!.Nodes[last.PlanOrdinal];
        return frontier.Payload.Status switch
        {
            GovernedLoopFrontierStatus.Active when last.Status == GovernedLoopNodeExecutionStatus.Ready
                => Selection(GovernedLoopSequentialFrontierSelectionStatus.Ready, node, null, null, "The exact lowest admitted plan ordinal is Ready."),
            GovernedLoopFrontierStatus.Active when last.Status == GovernedLoopNodeExecutionStatus.Running
                => Selection(GovernedLoopSequentialFrontierSelectionStatus.Running, node, last.Attempt, last.AttemptOperationId, "The exact Running attempt requires evidence-only reconciliation and cannot be redispatched."),
            GovernedLoopFrontierStatus.ReviewBlocked
                => Selection(GovernedLoopSequentialFrontierSelectionStatus.ReviewBlocked, null, null, null, "The canonical frontier is durably blocked on review."),
            GovernedLoopFrontierStatus.Completed or GovernedLoopFrontierStatus.Failed or GovernedLoopFrontierStatus.Cancelled
                => Selection(GovernedLoopSequentialFrontierSelectionStatus.Terminal, null, null, null, "The canonical frontier is terminal."),
            _ => Selection(GovernedLoopSequentialFrontierSelectionStatus.Invalid, null, null, null, "The canonical frontier has no legal concurrency-one selection."),
        };
    }

    /// <summary>Transitions the exact selected Ready node to Running before any node behavior may dispatch.</summary>
    public static GovernedLoopSequentialFrontierTransitionResult Start(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopSequentialPlan? plan,
        GovernedLoopSequentialPlanNode? node,
        int attempt,
        string? attemptOperationId,
        DateTimeOffset updatedAtUtc)
    {
        var selected = Select(frontier, binding, plan);
        if (selected.Status != GovernedLoopSequentialFrontierSelectionStatus.Ready
            || !SamePlanNode(selected.Node, node)
            || attempt != 1)
        {
            return Invalid("Schema-1 sequential execution can start only the exact deterministic Ready node at attempt one.");
        }

        try
        {
            var successor = ReplaceLast(
                frontier!,
                binding!,
                CreateNode(node!, GovernedLoopNodeExecutionStatus.Running, attempt, attemptOperationId, null, null),
                GovernedLoopFrontierStatus.Active,
                updatedAtUtc);
            return TransitionIsValid(frontier!, successor, binding, plan)
                ? Applied(successor, "The selected canonical node entered Running before dispatch.")
                : Invalid("The Ready-to-Running successor violates the exact canonical frontier transition contract.");
        }
        catch (Exception exception) when (IsContractFailure(exception))
        {
            return Invalid($"The Ready-to-Running transition was rejected by its bounded contract: {exception.GetType().Name}.");
        }
    }

    /// <summary>Commits an exact Running outcome and, for success, appends only the next admitted plan ordinal as Ready.</summary>
    public static GovernedLoopSequentialFrontierTransitionResult CompleteRunning(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopSequentialPlan? plan,
        GovernedLoopSequentialPlanNode? node,
        int attempt,
        string? attemptOperationId,
        string? outcomeEvidenceId,
        string? outcomeEvidenceHash,
        DateTimeOffset updatedAtUtc)
        => ResolveRunning(
            frontier,
            binding,
            plan,
            node,
            attempt,
            attemptOperationId,
            GovernedLoopNodeExecutionStatus.Completed,
            outcomeEvidenceId,
            outcomeEvidenceHash,
            updatedAtUtc);

    /// <summary>Commits an exact definitive failed outcome without exposing later plan nodes.</summary>
    public static GovernedLoopSequentialFrontierTransitionResult FailRunning(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopSequentialPlan? plan,
        GovernedLoopSequentialPlanNode? node,
        int attempt,
        string? attemptOperationId,
        string? outcomeEvidenceId,
        string? outcomeEvidenceHash,
        DateTimeOffset updatedAtUtc)
        => ResolveRunning(
            frontier,
            binding,
            plan,
            node,
            attempt,
            attemptOperationId,
            GovernedLoopNodeExecutionStatus.Failed,
            outcomeEvidenceId,
            outcomeEvidenceHash,
            updatedAtUtc);

    /// <summary>Blocks one exact Running attempt on review, retaining any available outcome correlation without redispatch.</summary>
    public static GovernedLoopSequentialFrontierTransitionResult ReviewBlockRunning(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopSequentialPlan? plan,
        GovernedLoopSequentialPlanNode? node,
        int attempt,
        string? attemptOperationId,
        string? outcomeEvidenceId,
        string? outcomeEvidenceHash,
        DateTimeOffset updatedAtUtc)
        => ResolveRunning(
            frontier,
            binding,
            plan,
            node,
            attempt,
            attemptOperationId,
            GovernedLoopNodeExecutionStatus.ReviewBlocked,
            outcomeEvidenceId,
            outcomeEvidenceHash,
            updatedAtUtc);

    /// <summary>Commits aggregate cancellation without rewriting the reached node evidence prefix.</summary>
    public static GovernedLoopSequentialFrontierTransitionResult Cancel(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopSequentialPlan? plan,
        DateTimeOffset updatedAtUtc)
    {
        if (!Validate(frontier, binding, plan)
            || frontier!.Payload.Status is GovernedLoopFrontierStatus.Completed or GovernedLoopFrontierStatus.Failed or GovernedLoopFrontierStatus.Cancelled)
        {
            return Invalid("Only a valid nonterminal canonical frontier can be cancelled.");
        }

        try
        {
            var successor = CreatePosture(
                binding!,
                checked(frontier.Payload.FrontierVersion + 1),
                GovernedLoopFrontierStatus.Cancelled,
                frontier.Payload.Nodes,
                updatedAtUtc);
            return TransitionIsValid(frontier, successor, binding, plan)
                ? Applied(successor, "The canonical frontier retained its reached prefix and entered Cancelled.")
                : Invalid("The cancellation successor violates the exact canonical frontier transition contract.");
        }
        catch (Exception exception) when (IsContractFailure(exception))
        {
            return Invalid($"The canonical cancellation transition was rejected by its bounded contract: {exception.GetType().Name}.");
        }
    }

    private static GovernedLoopSequentialFrontierTransitionResult ResolveRunning(
        GovernedLoopFrontierPosture? frontier,
        GovernedLoopSequentialAdapterBinding? binding,
        GovernedLoopSequentialPlan? plan,
        GovernedLoopSequentialPlanNode? node,
        int attempt,
        string? attemptOperationId,
        GovernedLoopNodeExecutionStatus resolution,
        string? outcomeEvidenceId,
        string? outcomeEvidenceHash,
        DateTimeOffset updatedAtUtc)
    {
        var selected = Select(frontier, binding, plan);
        if (selected.Status != GovernedLoopSequentialFrontierSelectionStatus.Running
            || !SamePlanNode(selected.Node, node)
            || selected.Attempt != attempt
            || !string.Equals(selected.AttemptOperationId, attemptOperationId, StringComparison.Ordinal)
            || resolution is not (GovernedLoopNodeExecutionStatus.Completed or GovernedLoopNodeExecutionStatus.Failed or GovernedLoopNodeExecutionStatus.ReviewBlocked))
        {
            return Invalid("Only the exact committed Running attempt can resolve the canonical frontier.");
        }

        try
        {
            var resolved = CreateNode(node!, resolution, attempt, attemptOperationId, outcomeEvidenceId, outcomeEvidenceHash);
            var nodes = frontier!.Payload.Nodes.Take(frontier.Payload.Nodes.Count - 1).Append(resolved).ToList();
            GovernedLoopFrontierStatus aggregate;
            if (resolution == GovernedLoopNodeExecutionStatus.Completed && node!.Ordinal + 1 < plan!.Nodes.Count)
            {
                if (node.Ordinal + 1 != frontier.Payload.Nodes.Count)
                {
                    return Invalid("A successful frontier transition may append only the next exact admitted plan ordinal.");
                }

                nodes.Add(CreateNode(plan.Nodes[node.Ordinal + 1], GovernedLoopNodeExecutionStatus.Ready, null, null, null, null));
                aggregate = GovernedLoopFrontierStatus.Active;
            }
            else
            {
                aggregate = resolution switch
                {
                    GovernedLoopNodeExecutionStatus.Completed => GovernedLoopFrontierStatus.Completed,
                    GovernedLoopNodeExecutionStatus.Failed => GovernedLoopFrontierStatus.Failed,
                    GovernedLoopNodeExecutionStatus.ReviewBlocked => GovernedLoopFrontierStatus.ReviewBlocked,
                    _ => throw new InvalidOperationException("Unsupported canonical node resolution."),
                };
            }

            var successor = CreatePosture(
                binding!,
                checked(frontier.Payload.FrontierVersion + 1),
                aggregate,
                nodes,
                updatedAtUtc);
            return TransitionIsValid(frontier, successor, binding, plan)
                ? Applied(successor, resolution == GovernedLoopNodeExecutionStatus.Completed
                    ? "The exact outcome was committed and only the next admitted plan ordinal became Ready."
                    : "The exact Running attempt entered its durable terminal frontier posture.")
                : Invalid("The Running resolution violates the exact canonical frontier transition contract.");
        }
        catch (Exception exception) when (IsContractFailure(exception))
        {
            return Invalid($"The Running resolution was rejected by its bounded contract: {exception.GetType().Name}.");
        }
    }

    private static GovernedLoopFrontierPosture ReplaceLast(
        GovernedLoopFrontierPosture current,
        GovernedLoopSequentialAdapterBinding binding,
        GovernedLoopNodeExecutionEvidence replacement,
        GovernedLoopFrontierStatus status,
        DateTimeOffset updatedAtUtc)
        => CreatePosture(
            binding,
            checked(current.Payload.FrontierVersion + 1),
            status,
            current.Payload.Nodes.Take(current.Payload.Nodes.Count - 1).Append(replacement),
            updatedAtUtc);

    private static GovernedLoopFrontierPosture CreatePosture(
        GovernedLoopSequentialAdapterBinding binding,
        long frontierVersion,
        GovernedLoopFrontierStatus status,
        IEnumerable<GovernedLoopNodeExecutionEvidence> nodes,
        DateTimeOffset updatedAtUtc)
    {
        var payload = GovernedLoopFrontierPayload.Create(
            1,
            frontierVersion,
            ConcurrencyCeiling,
            status,
            nodes,
            updatedAtUtc,
            string.Empty);
        return GovernedLoopFrontierPosture.Create(
            binding.ExecutionBinding,
            binding.WorkspaceId,
            binding.GraphArtifactHash,
            binding.GraphLayoutHash,
            binding.AdmissionReceiptHash,
            payload);
    }

    private static GovernedLoopNodeExecutionEvidence CreateNode(
        GovernedLoopSequentialPlanNode node,
        GovernedLoopNodeExecutionStatus status,
        int? attempt,
        string? attemptOperationId,
        string? outcomeEvidenceId,
        string? outcomeEvidenceHash)
        => GovernedLoopNodeExecutionEvidence.Create(
            node.Ordinal,
            node.NodeId,
            node.Descriptor,
            node.IncomingControlEdgeId is null ? [] : [node.IncomingControlEdgeId],
            node.OutgoingControlEdgeId is null ? [] : [node.OutgoingControlEdgeId],
            status,
            attempt,
            attemptOperationId,
            outcomeEvidenceId,
            outcomeEvidenceHash);

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
            && plan.Nodes.Count >= 2
            && plan.Nodes.Select((node, ordinal) => node.Ordinal == ordinal).All(matches => matches);

    private static bool MatchesPlanNode(GovernedLoopNodeExecutionEvidence reached, GovernedLoopSequentialPlanNode planned)
        => reached.SchemaVersion == 1
            && reached.PlanOrdinal == planned.Ordinal
            && string.Equals(reached.NodeId, planned.NodeId, StringComparison.Ordinal)
            && Equals(reached.Descriptor, planned.Descriptor)
            && reached.IncomingControlEdgeIds.SequenceEqual(planned.IncomingControlEdgeId is null ? [] : [planned.IncomingControlEdgeId], StringComparer.Ordinal)
            && reached.OutgoingControlEdgeIds.SequenceEqual(planned.OutgoingControlEdgeId is null ? [] : [planned.OutgoingControlEdgeId], StringComparer.Ordinal);

    private static bool SamePlanNode(GovernedLoopSequentialPlanNode? left, GovernedLoopSequentialPlanNode? right)
        => left is not null
            && right is not null
            && left.Ordinal == right.Ordinal
            && string.Equals(left.NodeId, right.NodeId, StringComparison.Ordinal)
            && Equals(left.Descriptor, right.Descriptor);

    private static GovernedLoopSequentialFrontierTransitionResult Applied(GovernedLoopFrontierPosture frontier, string detail)
        => new(GovernedLoopSequentialFrontierTransitionStatus.Applied, frontier, detail);

    private static GovernedLoopSequentialFrontierTransitionResult Invalid(string detail)
        => new(GovernedLoopSequentialFrontierTransitionStatus.Invalid, null, detail);

    private static GovernedLoopSequentialFrontierSelectionResult Selection(
        GovernedLoopSequentialFrontierSelectionStatus status,
        GovernedLoopSequentialPlanNode? node,
        int? attempt,
        string? operationId,
        string detail)
        => new(status, node, attempt, operationId, detail);

    private static bool IsContractFailure(Exception exception)
        => exception is ArgumentException or InvalidOperationException or OverflowException;
}
