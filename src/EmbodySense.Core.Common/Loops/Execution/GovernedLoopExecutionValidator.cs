using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Common.Loops.Execution;

/// <summary>Validates bounded schema-1 governed-loop execution contracts and cross-plane composition.</summary>
public static class GovernedLoopExecutionValidator
{
    /// <summary>Validates an exact execution binding.</summary>
    /// <param name="binding">The binding to validate.</param>
    /// <returns>A bounded value-free validation result.</returns>
    public static GovernedLoopExecutionValidationResult Validate(GovernedLoopExecutionBinding? binding)
    {
        var errors = new List<GovernedLoopExecutionValidationError>();
        if (binding is null)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.ContractRequired, "$binding");
        }
        else if (binding.SchemaVersion != GovernedLoopExecutionLimits.CurrentSchemaVersion || binding.Revision.SchemaVersion != GovernedLoopExecutionLimits.CurrentSchemaVersion)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.UnsupportedSchemaVersion, "$binding.schemaVersion");
        }

        return Result(errors);
    }

    /// <summary>Validates a reusable unbound lifecycle payload.</summary>
    /// <param name="payload">The payload to validate.</param>
    /// <returns>A bounded value-free validation result.</returns>
    public static GovernedLoopExecutionValidationResult Validate(GovernedLoopRunLifecyclePayload? payload)
    {
        var errors = new List<GovernedLoopExecutionValidationError>();
        if (payload is null)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.ContractRequired, "$lifecycle.payload");
        }
        else if (payload.SchemaVersion != GovernedLoopExecutionLimits.CurrentSchemaVersion)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.UnsupportedSchemaVersion, "$lifecycle.payload.schemaVersion");
        }

        return Result(errors);
    }

    /// <summary>Validates a reusable unbound frontier payload.</summary>
    /// <param name="payload">The payload to validate.</param>
    /// <returns>A bounded value-free validation result.</returns>
    public static GovernedLoopExecutionValidationResult Validate(GovernedLoopFrontierPayload? payload)
    {
        var errors = new List<GovernedLoopExecutionValidationError>();
        if (payload is null)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.ContractRequired, "$frontier.payload");
        }
        else if (payload.SchemaVersion != GovernedLoopExecutionLimits.CurrentSchemaVersion)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.UnsupportedSchemaVersion, "$frontier.payload.schemaVersion");
        }

        return Result(errors);
    }

    /// <summary>Validates a reusable unbound effect payload.</summary>
    /// <param name="payload">The payload to validate.</param>
    /// <returns>A bounded value-free validation result.</returns>
    public static GovernedLoopExecutionValidationResult Validate(GovernedLoopEffectPayload? payload)
    {
        var errors = new List<GovernedLoopExecutionValidationError>();
        if (payload is null)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.ContractRequired, "$effect.payload");
        }
        else if (payload.SchemaVersion != GovernedLoopExecutionLimits.CurrentSchemaVersion)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.UnsupportedSchemaVersion, "$effect.payload.schemaVersion");
        }

        return Result(errors);
    }

    /// <summary>Validates a reusable unbound projection payload.</summary>
    /// <param name="payload">The payload to validate.</param>
    /// <returns>A bounded value-free validation result.</returns>
    public static GovernedLoopExecutionValidationResult Validate(GovernedLoopProjectionPayload? payload)
    {
        var errors = new List<GovernedLoopExecutionValidationError>();
        if (payload is null)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.ContractRequired, "$projection.payload");
        }
        else if (payload.SchemaVersion != GovernedLoopExecutionLimits.CurrentSchemaVersion)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.UnsupportedSchemaVersion, "$projection.payload.schemaVersion");
        }

        return Result(errors);
    }

    /// <summary>Validates all canonical execution planes and their exact-binding composition.</summary>
    /// <param name="schemaVersion">The aggregate schema version.</param>
    /// <param name="lifecycle">The bound lifecycle plane.</param>
    /// <param name="frontier">The bound frontier plane.</param>
    /// <param name="effects">The sorted unique bound effect postures.</param>
    /// <param name="projections">The sorted unique bound projection postures.</param>
    /// <returns>A bounded value-free validation result.</returns>
    public static GovernedLoopExecutionValidationResult ValidateComposition(
        int schemaVersion,
        GovernedLoopRunLifecycle? lifecycle,
        GovernedLoopFrontierPosture? frontier,
        IReadOnlyList<GovernedLoopEffectPosture>? effects,
        IReadOnlyList<GovernedLoopProjectionPosture>? projections)
    {
        var errors = new List<GovernedLoopExecutionValidationError>();
        if (schemaVersion != GovernedLoopExecutionLimits.CurrentSchemaVersion)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.UnsupportedSchemaVersion, "$.schemaVersion");
        }

        if (lifecycle is null)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.ContractRequired, "$.lifecycle");
        }

        if (frontier is null)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.ContractRequired, "$.frontier");
        }

        if (effects is null)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.ContractRequired, "$.effects");
        }
        else if (effects.Count > GovernedLoopExecutionLimits.MaxEffects)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.CollectionTooLarge, "$.effects");
        }

        if (projections is null)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.ContractRequired, "$.projections");
        }
        else if (projections.Count > GovernedLoopExecutionLimits.MaxProjections)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.CollectionTooLarge, "$.projections");
        }

        if (lifecycle is null || frontier is null || effects is null || projections is null)
        {
            return Result(errors);
        }

        if (effects.Count > GovernedLoopExecutionLimits.MaxEffects || projections.Count > GovernedLoopExecutionLimits.MaxProjections)
        {
            return Result(errors);
        }

        ValidateBindings(lifecycle, frontier, effects, projections, errors);
        ValidateCanonicalCollections(effects, projections, errors);
        ValidateLifecycleFrontier(lifecycle.Payload.Status, frontier.Payload, errors);
        ValidateEvidenceTimes(lifecycle.Payload, frontier, effects, projections, errors);
        ValidateEffectOrigins(frontier.Payload, effects, errors);
        ValidateProjectionSources(lifecycle.Binding, frontier.Payload, effects, projections, errors);
        ValidateTerminalEvidence(lifecycle.Payload.Status, effects, projections, errors);
        return Result(errors);
    }

    /// <summary>Validates an already-created canonical aggregate.</summary>
    /// <param name="evidence">The aggregate to validate.</param>
    /// <returns>A bounded value-free validation result.</returns>
    public static GovernedLoopExecutionValidationResult Validate(GovernedLoopExecutionEvidenceSet? evidence)
    {
        return evidence is null
            ? Result([Error(GovernedLoopExecutionValidationErrorCode.ContractRequired, "$")])
            : ValidateComposition(evidence.SchemaVersion, evidence.Lifecycle, evidence.Frontier, evidence.Effects, evidence.Projections);
    }

    /// <summary>Validates one append-only aggregate successor without selecting dispatch, recovery, or reconciliation policy.</summary>
    /// <param name="current">The currently retained canonical execution evidence.</param>
    /// <param name="next">The proposed canonical successor evidence.</param>
    /// <returns>A bounded value-free validation result.</returns>
    /// <remarks>Exact unchanged planes are accepted so later evidence may compose with immutable terminal lifecycle and frontier snapshots. Every changed retained item must make one legal transition. New node activations must first be exposed as Ready or durably pruned as Skipped; other new canonical identities may be appended in their valid evidence posture.</remarks>
    public static GovernedLoopExecutionValidationResult ValidateTransition(GovernedLoopExecutionEvidenceSet? current, GovernedLoopExecutionEvidenceSet? next)
    {
        var errors = new List<GovernedLoopExecutionValidationError>();
        if (current is null || next is null)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.ContractRequired, "$transition");
            return Result(errors);
        }

        AppendErrors(Validate(next), errors);
        if (current.SchemaVersion != next.SchemaVersion)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.UnsupportedSchemaVersion, "$transition.schemaVersion");
        }

        RequireSameBinding(current.Lifecycle.Binding, next.Lifecycle.Binding, "$transition.binding", errors);
        if (!Equals(current.Lifecycle, next.Lifecycle))
        {
            AppendErrors(ValidateTransition(current.Lifecycle, next.Lifecycle), errors);
        }

        if (!SameFrontier(current.Frontier, next.Frontier))
        {
            AppendErrors(ValidateTransition(current.Frontier, next.Frontier), errors);
        }

        ValidateEffectHistory(current.Effects, next.Effects, errors);
        ValidateProjectionHistory(current.Projections, next.Projections, errors);
        return Result(errors);
    }

    /// <summary>Validates a proposed bound lifecycle successor.</summary>
    /// <param name="current">The current lifecycle posture.</param>
    /// <param name="next">The proposed successor.</param>
    /// <returns>A bounded value-free validation result.</returns>
    public static GovernedLoopExecutionValidationResult ValidateTransition(GovernedLoopRunLifecycle? current, GovernedLoopRunLifecycle? next)
    {
        var errors = new List<GovernedLoopExecutionValidationError>();
        if (current is null || next is null)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.ContractRequired, "$lifecycle.transition");
            return Result(errors);
        }

        RequireSameBinding(current.Binding, next.Binding, "$lifecycle.binding", errors);
        if (next.Payload.LifecycleVersion != current.Payload.LifecycleVersion + 1)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.InvalidSuccessorVersion, "$lifecycle.payload.lifecycleVersion");
        }

        if (current.Payload.CreatedAtUtc != next.Payload.CreatedAtUtc)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.ImmutableEvidenceChanged, "$lifecycle.payload.createdAtUtc");
        }

        if (next.Payload.UpdatedAtUtc < current.Payload.UpdatedAtUtc || !GovernedLoopExecutionStateMatrix.IsRunTransitionAllowed(current.Payload.Status, next.Payload.Status))
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.IllegalTransition, "$lifecycle.payload.status");
        }

        return Result(errors);
    }

    /// <summary>Validates a proposed bound frontier successor.</summary>
    /// <param name="current">The current frontier posture.</param>
    /// <param name="next">The proposed successor.</param>
    /// <returns>A bounded value-free validation result.</returns>
    public static GovernedLoopExecutionValidationResult ValidateTransition(GovernedLoopFrontierPosture? current, GovernedLoopFrontierPosture? next)
    {
        var errors = new List<GovernedLoopExecutionValidationError>();
        if (current is null || next is null)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.ContractRequired, "$frontier.transition");
            return Result(errors);
        }

        AppendErrors(GovernedLoopFrontierContractValidator.Validate(next), errors);
        RequireSameBinding(current.Binding, next.Binding, "$frontier.binding", errors);
        if (current.SchemaVersion != next.SchemaVersion
            || !string.Equals(current.WorkspaceId, next.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(current.GraphArtifactHash, next.GraphArtifactHash, StringComparison.Ordinal)
            || !string.Equals(current.GraphLayoutHash, next.GraphLayoutHash, StringComparison.Ordinal)
            || !string.Equals(current.AdmissionReceiptHash, next.AdmissionReceiptHash, StringComparison.Ordinal)
            || current.Payload.SchemaVersion != next.Payload.SchemaVersion
            || current.Payload.ConcurrencyCeiling != next.Payload.ConcurrencyCeiling)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.ImmutableEvidenceChanged, "$frontier");
        }

        if (next.Payload.FrontierVersion != current.Payload.FrontierVersion + 1)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.InvalidSuccessorVersion, "$frontier.payload.frontierVersion");
        }

        if (next.Payload.UpdatedAtUtc < current.Payload.UpdatedAtUtc || !GovernedLoopExecutionStateMatrix.IsFrontierTransitionAllowed(current.Payload.Status, next.Payload.Status))
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.IllegalTransition, "$frontier.payload.status");
        }

        if (next.Payload.Status == GovernedLoopFrontierStatus.Cancelled)
        {
            if (!SameNodeCollection(current.Payload.Nodes, next.Payload.Nodes))
            {
                Add(errors, GovernedLoopExecutionValidationErrorCode.ImmutableEvidenceChanged, "$frontier.payload.nodes");
            }
        }
        else
        {
            ValidateNodeHistory(current.Payload.Nodes, next.Payload.Nodes, errors);
        }

        return Result(errors);
    }

    /// <summary>Validates a proposed bound effect successor without choosing retry or reconciliation policy.</summary>
    /// <param name="current">The current effect posture.</param>
    /// <param name="next">The proposed successor.</param>
    /// <returns>A bounded value-free validation result.</returns>
    public static GovernedLoopExecutionValidationResult ValidateTransition(GovernedLoopEffectPosture? current, GovernedLoopEffectPosture? next)
    {
        var errors = new List<GovernedLoopExecutionValidationError>();
        if (current is null || next is null)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.ContractRequired, "$effect.transition");
            return Result(errors);
        }

        RequireSameBinding(current.Binding, next.Binding, "$effect.binding", errors);
        if (!SameEffectIdentity(current.Payload, next.Payload))
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.ImmutableEvidenceChanged, "$effect.payload");
        }

        if (next.Payload.UpdatedAtUtc < current.Payload.UpdatedAtUtc
            || !GovernedLoopExecutionStateMatrix.IsEffectTransitionAllowed(current.Payload.Phase, next.Payload.Phase)
            || !IsEffectEvidenceTransitionAllowed(current.Payload, next.Payload))
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.IllegalTransition, "$effect.payload.phase");
        }

        return Result(errors);
    }

    /// <summary>Validates a proposed bound projection successor.</summary>
    /// <param name="current">The current projection posture.</param>
    /// <param name="next">The proposed successor.</param>
    /// <returns>A bounded value-free validation result.</returns>
    public static GovernedLoopExecutionValidationResult ValidateTransition(GovernedLoopProjectionPosture? current, GovernedLoopProjectionPosture? next)
    {
        var errors = new List<GovernedLoopExecutionValidationError>();
        if (current is null || next is null)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.ContractRequired, "$projection.transition");
            return Result(errors);
        }

        RequireSameBinding(current.Binding, next.Binding, "$projection.binding", errors);
        if (!SameProjectionIdentity(current.Payload, next.Payload) || !string.Equals(current.Payload.ExpectedVersion, next.Payload.ExpectedVersion, StringComparison.Ordinal))
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.ImmutableEvidenceChanged, "$projection.payload");
        }

        if (next.Payload.UpdatedAtUtc < current.Payload.UpdatedAtUtc
            || !GovernedLoopExecutionStateMatrix.IsProjectionTransitionAllowed(current.Payload.Status, next.Payload.Status)
            || current.Payload.Status == next.Payload.Status && !Equals(current.Payload, next.Payload)
            || current.Payload.Status is GovernedLoopProjectionStatus.Committed or GovernedLoopProjectionStatus.Reconciled && !Equals(current.Payload, next.Payload))
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.IllegalTransition, "$projection.payload.status");
        }

        return Result(errors);
    }

    private static void ValidateBindings(
        GovernedLoopRunLifecycle lifecycle,
        GovernedLoopFrontierPosture frontier,
        IReadOnlyList<GovernedLoopEffectPosture> effects,
        IReadOnlyList<GovernedLoopProjectionPosture> projections,
        List<GovernedLoopExecutionValidationError> errors)
    {
        RequireSameBinding(lifecycle.Binding, frontier.Binding, "$.frontier.binding", errors);
        for (var index = 0; index < effects.Count; index++)
        {
            if (effects[index] is null)
            {
                Add(errors, GovernedLoopExecutionValidationErrorCode.ContractRequired, $"$.effects[{index}]");
                continue;
            }

            RequireSameBinding(lifecycle.Binding, effects[index].Binding, $"$.effects[{index}].binding", errors);
        }

        for (var index = 0; index < projections.Count; index++)
        {
            if (projections[index] is null)
            {
                Add(errors, GovernedLoopExecutionValidationErrorCode.ContractRequired, $"$.projections[{index}]");
                continue;
            }

            RequireSameBinding(lifecycle.Binding, projections[index].Binding, $"$.projections[{index}].binding", errors);
        }
    }

    private static void ValidateCanonicalCollections(IReadOnlyList<GovernedLoopEffectPosture> effects, IReadOnlyList<GovernedLoopProjectionPosture> projections, List<GovernedLoopExecutionValidationError> errors)
    {
        var operationGenerations = new HashSet<(string OperationId, long EffectGeneration)>();
        for (var index = 0; index < effects.Count; index++)
        {
            var effect = effects[index];
            if (effect is not null && !operationGenerations.Add((effect.Payload.OperationId, effect.Payload.EffectGeneration)))
            {
                Add(errors, GovernedLoopExecutionValidationErrorCode.EffectOperationGenerationNotUnique, $"$.effects[{index}].payload.operationId");
            }
        }

        for (var index = 1; index < effects.Count; index++)
        {
            if (effects[index - 1] is null || effects[index] is null || string.CompareOrdinal(effects[index - 1].Payload.EffectId, effects[index].Payload.EffectId) >= 0)
            {
                Add(errors, GovernedLoopExecutionValidationErrorCode.CollectionNotCanonical, "$.effects");
                break;
            }
        }

        for (var index = 1; index < projections.Count; index++)
        {
            if (projections[index - 1] is null || projections[index] is null || string.CompareOrdinal(projections[index - 1].Payload.ProjectionId, projections[index].Payload.ProjectionId) >= 0)
            {
                Add(errors, GovernedLoopExecutionValidationErrorCode.CollectionNotCanonical, "$.projections");
                break;
            }
        }
    }

    private static void ValidateLifecycleFrontier(GovernedLoopRunStatus lifecycle, GovernedLoopFrontierPayload frontier, List<GovernedLoopExecutionValidationError> errors)
    {
        var valid = lifecycle switch
        {
            GovernedLoopRunStatus.Admitted or GovernedLoopRunStatus.Running => frontier.Status == GovernedLoopFrontierStatus.Active,
            GovernedLoopRunStatus.Waiting => frontier.Status is GovernedLoopFrontierStatus.Waiting or GovernedLoopFrontierStatus.ReviewBlocked,
            GovernedLoopRunStatus.PauseRequested or GovernedLoopRunStatus.CancelRequested => frontier.Status is GovernedLoopFrontierStatus.Active or GovernedLoopFrontierStatus.Waiting or GovernedLoopFrontierStatus.ReviewBlocked,
            GovernedLoopRunStatus.Paused => frontier.Status is GovernedLoopFrontierStatus.Waiting or GovernedLoopFrontierStatus.ReviewBlocked
                || frontier.Status == GovernedLoopFrontierStatus.Active && frontier.Nodes.All(node => node.Status != GovernedLoopNodeExecutionStatus.Running),
            GovernedLoopRunStatus.Completed => frontier.Status == GovernedLoopFrontierStatus.Completed,
            GovernedLoopRunStatus.Failed => frontier.Status == GovernedLoopFrontierStatus.Failed,
            GovernedLoopRunStatus.Cancelled => frontier.Status == GovernedLoopFrontierStatus.Cancelled,
            GovernedLoopRunStatus.NeedsReview => frontier.Status is GovernedLoopFrontierStatus.ReviewBlocked
                or GovernedLoopFrontierStatus.Completed
                or GovernedLoopFrontierStatus.Failed
                or GovernedLoopFrontierStatus.Cancelled,
            _ => false
        };
        if (!valid)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.LifecycleFrontierMismatch, "$.frontier.payload.status");
        }
    }

    private static void ValidateNodeHistory(IReadOnlyList<GovernedLoopNodeExecutionEvidence> current, IReadOnlyList<GovernedLoopNodeExecutionEvidence> next, List<GovernedLoopExecutionValidationError> errors)
    {
        var successors = next.ToDictionary(node => node.ActivationOrdinal);
        for (var index = 0; index < current.Count; index++)
        {
            if (!successors.TryGetValue(current[index].ActivationOrdinal, out var successor))
            {
                Add(errors, GovernedLoopExecutionValidationErrorCode.ImmutableEvidenceChanged, $"$frontier.payload.nodes[{index}]");
            }
            else if (!SameNodeActivationIdentity(current[index], successor))
            {
                Add(errors, GovernedLoopExecutionValidationErrorCode.ImmutableEvidenceChanged, $"$frontier.payload.nodes[{index}]");
            }
            else if (!GovernedLoopExecutionStateMatrix.IsNodeEvidenceTransitionAllowed(current[index], successor))
            {
                Add(errors, GovernedLoopExecutionValidationErrorCode.IllegalTransition, $"$frontier.payload.nodes[{index}].status");
            }
        }

        for (var index = current.Count; index < next.Count; index++)
        {
            if (next[index].Status is not (GovernedLoopNodeExecutionStatus.Ready or GovernedLoopNodeExecutionStatus.Skipped))
            {
                Add(errors, GovernedLoopExecutionValidationErrorCode.IllegalTransition, $"$frontier.payload.nodes[{index}].status");
            }
        }
    }

    private static bool SameNodeCollection(IReadOnlyList<GovernedLoopNodeExecutionEvidence> current, IReadOnlyList<GovernedLoopNodeExecutionEvidence> next)
    {
        return current.Count == next.Count && current.Zip(next).All(pair => SameNodeEvidence(pair.First, pair.Second));
    }

    private static bool SameFrontier(GovernedLoopFrontierPosture current, GovernedLoopFrontierPosture next)
    {
        return current.SchemaVersion == next.SchemaVersion
            && string.Equals(current.WorkspaceId, next.WorkspaceId, StringComparison.Ordinal)
            && Equals(current.Binding, next.Binding)
            && string.Equals(current.GraphArtifactHash, next.GraphArtifactHash, StringComparison.Ordinal)
            && string.Equals(current.GraphLayoutHash, next.GraphLayoutHash, StringComparison.Ordinal)
            && string.Equals(current.AdmissionReceiptHash, next.AdmissionReceiptHash, StringComparison.Ordinal)
            && current.Payload.SchemaVersion == next.Payload.SchemaVersion
            && current.Payload.FrontierVersion == next.Payload.FrontierVersion
            && current.Payload.ConcurrencyCeiling == next.Payload.ConcurrencyCeiling
            && current.Payload.Status == next.Payload.Status
            && current.Payload.UpdatedAtUtc == next.Payload.UpdatedAtUtc
            && string.Equals(current.Payload.ContentHash, next.Payload.ContentHash, StringComparison.Ordinal)
            && SameNodeCollection(current.Payload.Nodes, next.Payload.Nodes);
    }

    private static void ValidateEffectHistory(
        IReadOnlyList<GovernedLoopEffectPosture> current,
        IReadOnlyList<GovernedLoopEffectPosture> next,
        List<GovernedLoopExecutionValidationError> errors)
    {
        var successors = next.ToDictionary(effect => effect.Payload.EffectId, StringComparer.Ordinal);
        for (var index = 0; index < current.Count; index++)
        {
            var effect = current[index];
            if (!successors.TryGetValue(effect.Payload.EffectId, out var successor))
            {
                Add(errors, GovernedLoopExecutionValidationErrorCode.HistoricalEvidenceMissing, $"$transition.effects[{index}]");
            }
            else if (!Equals(effect, successor))
            {
                AppendErrors(ValidateTransition(effect, successor), errors);
            }
        }
    }

    private static void ValidateProjectionHistory(
        IReadOnlyList<GovernedLoopProjectionPosture> current,
        IReadOnlyList<GovernedLoopProjectionPosture> next,
        List<GovernedLoopExecutionValidationError> errors)
    {
        var successors = next.ToDictionary(projection => projection.Payload.ProjectionId, StringComparer.Ordinal);
        for (var index = 0; index < current.Count; index++)
        {
            var projection = current[index];
            if (!successors.TryGetValue(projection.Payload.ProjectionId, out var successor))
            {
                Add(errors, GovernedLoopExecutionValidationErrorCode.HistoricalEvidenceMissing, $"$transition.projections[{index}]");
            }
            else if (!Equals(projection, successor))
            {
                AppendErrors(ValidateTransition(projection, successor), errors);
            }
        }
    }

    private static bool SameNodeEvidence(GovernedLoopNodeExecutionEvidence current, GovernedLoopNodeExecutionEvidence next)
    {
        return string.Equals(current.NodeId, next.NodeId, StringComparison.Ordinal)
            && current.ActivationOrdinal == next.ActivationOrdinal
            && current.PlanOrdinal == next.PlanOrdinal
            && current.VisitOrdinal == next.VisitOrdinal
            && current.Descriptor == next.Descriptor
            && current.IncomingControlEdgeIds.SequenceEqual(next.IncomingControlEdgeIds, StringComparer.Ordinal)
            && current.OutgoingControlEdgeIds.SequenceEqual(next.OutgoingControlEdgeIds, StringComparer.Ordinal)
            && string.Equals(current.CycleId, next.CycleId, StringComparison.Ordinal)
            && current.CycleIteration == next.CycleIteration
            && current.ControlOutcome == next.ControlOutcome
            && current.SelectedControlEdgeIds.SequenceEqual(next.SelectedControlEdgeIds, StringComparer.Ordinal)
            && current.SkippedControlEdgeIds.SequenceEqual(next.SkippedControlEdgeIds, StringComparer.Ordinal)
            && current.JoinArrivals.Count == next.JoinArrivals.Count
            && current.JoinArrivals.Zip(next.JoinArrivals).All(pair => pair.First.SchemaVersion == pair.Second.SchemaVersion
                && pair.First.SourceActivationOrdinal == pair.Second.SourceActivationOrdinal
                && string.Equals(pair.First.ControlEdgeId, pair.Second.ControlEdgeId, StringComparison.Ordinal))
            && current.Attempt == next.Attempt
            && string.Equals(current.AttemptOperationId, next.AttemptOperationId, StringComparison.Ordinal)
            && current.Status == next.Status
            && string.Equals(current.OutcomeEvidenceId, next.OutcomeEvidenceId, StringComparison.Ordinal)
            && string.Equals(current.OutcomeEvidenceHash, next.OutcomeEvidenceHash, StringComparison.Ordinal);
    }

    private static bool SameNodeActivationIdentity(GovernedLoopNodeExecutionEvidence current, GovernedLoopNodeExecutionEvidence next)
    {
        return current.SchemaVersion == next.SchemaVersion
            && current.ActivationOrdinal == next.ActivationOrdinal
            && current.PlanOrdinal == next.PlanOrdinal
            && current.VisitOrdinal == next.VisitOrdinal
            && string.Equals(current.NodeId, next.NodeId, StringComparison.Ordinal)
            && current.Descriptor == next.Descriptor
            && current.IncomingControlEdgeIds.SequenceEqual(next.IncomingControlEdgeIds, StringComparer.Ordinal)
            && current.OutgoingControlEdgeIds.SequenceEqual(next.OutgoingControlEdgeIds, StringComparer.Ordinal)
            && string.Equals(current.CycleId, next.CycleId, StringComparison.Ordinal)
            && current.CycleIteration == next.CycleIteration
            && current.JoinArrivals.Count == next.JoinArrivals.Count
            && current.JoinArrivals.Zip(next.JoinArrivals).All(pair => pair.First.SchemaVersion == pair.Second.SchemaVersion
                && pair.First.SourceActivationOrdinal == pair.Second.SourceActivationOrdinal
                && string.Equals(pair.First.ControlEdgeId, pair.Second.ControlEdgeId, StringComparison.Ordinal));
    }

    private static void ValidateEvidenceTimes(
        GovernedLoopRunLifecyclePayload lifecycle,
        GovernedLoopFrontierPosture frontier,
        IReadOnlyList<GovernedLoopEffectPosture> effects,
        IReadOnlyList<GovernedLoopProjectionPosture> projections,
        List<GovernedLoopExecutionValidationError> errors)
    {
        RequireTimeInLifecycle(frontier.Payload.UpdatedAtUtc, lifecycle, false, "$.frontier.payload.updatedAtUtc", errors);
        var allowPostTerminalEvidence = GovernedLoopExecutionStateMatrix.IsTerminal(lifecycle.Status);
        for (var index = 0; index < effects.Count; index++)
        {
            if (effects[index] is not null)
            {
                RequireTimeInLifecycle(effects[index].Payload.UpdatedAtUtc, lifecycle, allowPostTerminalEvidence, $"$.effects[{index}].payload.updatedAtUtc", errors);
            }
        }

        for (var index = 0; index < projections.Count; index++)
        {
            if (projections[index] is not null)
            {
                RequireTimeInLifecycle(projections[index].Payload.UpdatedAtUtc, lifecycle, allowPostTerminalEvidence, $"$.projections[{index}].payload.updatedAtUtc", errors);
            }
        }
    }

    private static void ValidateEffectOrigins(GovernedLoopFrontierPayload frontier, IReadOnlyList<GovernedLoopEffectPosture> effects, List<GovernedLoopExecutionValidationError> errors)
    {
        var nodesById = frontier.Nodes
            .GroupBy(node => node.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        for (var index = 0; index < effects.Count; index++)
        {
            var effect = effects[index]?.Payload;
            if (effect?.OriginNodeId is not { } nodeId)
            {
                continue;
            }

            if (!nodesById.TryGetValue(nodeId, out var nodes))
            {
                Add(errors, GovernedLoopExecutionValidationErrorCode.EffectOriginNodeMissing, $"$.effects[{index}].payload.originNodeId");
                continue;
            }

            if (nodes.All(node => node.Status is GovernedLoopNodeExecutionStatus.Ready or GovernedLoopNodeExecutionStatus.Skipped))
            {
                Add(errors, GovernedLoopExecutionValidationErrorCode.EffectOriginNodeNotExecutable, $"$.effects[{index}].payload.originNodeId");
            }
        }
    }

    private static void ValidateProjectionSources(
        GovernedLoopExecutionBinding binding,
        GovernedLoopFrontierPayload frontier,
        IReadOnlyList<GovernedLoopEffectPosture> effects,
        IReadOnlyList<GovernedLoopProjectionPosture> projections,
        List<GovernedLoopExecutionValidationError> errors)
    {
        var sources = new HashSet<string>(StringComparer.Ordinal) { binding.RunId };
        foreach (var outcomeId in frontier.Nodes.Select(node => node.OutcomeEvidenceId).Where(value => value is not null))
        {
            sources.Add(outcomeId!);
        }

        foreach (var effect in effects.Where(effect => effect is not null))
        {
            sources.Add(effect.Payload.EffectId);
            AddOptionalSource(effect.Payload.OutcomeEvidenceId, sources);
            AddOptionalSource(effect.Payload.ReconciliationEvidenceId, sources);
        }

        for (var index = 0; index < projections.Count; index++)
        {
            var projection = projections[index];
            if (projection is null)
            {
                continue;
            }

            if (!sources.Contains(projection.Payload.SourceEvidenceId))
            {
                Add(errors, GovernedLoopExecutionValidationErrorCode.ProjectionSourceMissing, $"$.projections[{index}].payload.sourceEvidenceId");
            }

            if (projection.Payload.EffectId is null)
            {
                continue;
            }

            var effect = effects.FirstOrDefault(candidate => candidate is not null && string.Equals(candidate.Payload.EffectId, projection.Payload.EffectId, StringComparison.Ordinal));
            if (effect is null || !IsSourceFromEffect(projection.Payload.SourceEvidenceId, effect.Payload))
            {
                Add(errors, GovernedLoopExecutionValidationErrorCode.ProjectionEffectMismatch, $"$.projections[{index}].payload.effectId");
            }
        }
    }

    private static void ValidateTerminalEvidence(
        GovernedLoopRunStatus lifecycle,
        IReadOnlyList<GovernedLoopEffectPosture> effects,
        IReadOnlyList<GovernedLoopProjectionPosture> projections,
        List<GovernedLoopExecutionValidationError> errors)
    {
        var hasAmbiguity = effects.Any(effect => effect is not null && IsAmbiguous(effect.Payload))
            || projections.Any(projection => projection is not null && projection.Payload.Status is GovernedLoopProjectionStatus.Conflict or GovernedLoopProjectionStatus.ReconciliationRequired or GovernedLoopProjectionStatus.Reconciled);
        if (lifecycle == GovernedLoopRunStatus.NeedsReview && !hasAmbiguity)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.ReviewEvidenceRequired, "$.lifecycle.payload.status");
        }

        if (lifecycle is not (GovernedLoopRunStatus.Completed or GovernedLoopRunStatus.Failed or GovernedLoopRunStatus.Cancelled))
        {
            return;
        }

        if (effects.Any(effect => effect is not null && IsUnresolved(effect.Payload))
            || projections.Any(projection => projection is not null && projection.Payload.Status is not (GovernedLoopProjectionStatus.Committed or GovernedLoopProjectionStatus.Reconciled)))
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.TerminalEvidenceUnresolved, "$.lifecycle.payload.status");
        }
    }

    private static bool IsAmbiguous(GovernedLoopEffectPayload effect)
    {
        return effect.Outcome is GovernedLoopEffectOutcome.OutcomeUnknown or GovernedLoopEffectOutcome.Conflicted
            || effect.Phase is GovernedLoopEffectPhase.DispatchBoundaryReached or GovernedLoopEffectPhase.ReconciliationRequired or GovernedLoopEffectPhase.Reconciled
            || effect.EvidenceStatus is GovernedLoopEffectEvidenceStatus.Incomplete or GovernedLoopEffectEvidenceStatus.Conflicting;
    }

    private static bool IsUnresolved(GovernedLoopEffectPayload effect)
    {
        return effect.Phase switch
        {
            GovernedLoopEffectPhase.DispatchNotStarted or GovernedLoopEffectPhase.Committed or GovernedLoopEffectPhase.Reconciled => false,
            GovernedLoopEffectPhase.OutcomeObserved => effect.Outcome == GovernedLoopEffectOutcome.Conflicted || effect.EvidenceStatus != GovernedLoopEffectEvidenceStatus.Complete,
            _ => true
        };
    }

    private static bool SameEffectIdentity(GovernedLoopEffectPayload current, GovernedLoopEffectPayload next)
    {
        return string.Equals(current.EffectId, next.EffectId, StringComparison.Ordinal)
            && string.Equals(current.OperationId, next.OperationId, StringComparison.Ordinal)
            && current.EffectGeneration == next.EffectGeneration
            && current.Origin == next.Origin
            && string.Equals(current.OriginNodeId, next.OriginNodeId, StringComparison.Ordinal)
            && string.Equals(current.IntentHash, next.IntentHash, StringComparison.Ordinal);
    }

    private static bool IsEffectEvidenceTransitionAllowed(GovernedLoopEffectPayload current, GovernedLoopEffectPayload next)
    {
        if (current.Phase == next.Phase)
        {
            return Equals(current, next);
        }

        if (current.Phase is GovernedLoopEffectPhase.Committed or GovernedLoopEffectPhase.Reconciled)
        {
            return false;
        }

        if (next.Phase == GovernedLoopEffectPhase.ReconciliationRequired)
        {
            var preservedObservation = current.Phase switch
            {
                GovernedLoopEffectPhase.DispatchBoundaryReached => next.Outcome == GovernedLoopEffectOutcome.OutcomeUnknown
                    && next.OutcomeEvidenceId is null,
                GovernedLoopEffectPhase.OutcomeObserved => current.Outcome == next.Outcome
                    && string.Equals(current.OutcomeEvidenceId, next.OutcomeEvidenceId, StringComparison.Ordinal)
                    && (current.Outcome is not (GovernedLoopEffectOutcome.Succeeded or GovernedLoopEffectOutcome.Failed)
                        || current.EvidenceStatus == GovernedLoopEffectEvidenceStatus.Incomplete),
                _ => false
            };
            if (!preservedObservation)
            {
                return false;
            }
        }
        else if (current.Outcome is GovernedLoopEffectOutcome.Succeeded or GovernedLoopEffectOutcome.Failed or GovernedLoopEffectOutcome.Conflicted)
        {
            if (current.Phase != GovernedLoopEffectPhase.ReconciliationRequired
                && (current.Outcome != next.Outcome || !string.Equals(current.OutcomeEvidenceId, next.OutcomeEvidenceId, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        if (current.Phase == GovernedLoopEffectPhase.ReconciliationRequired && next.Phase == GovernedLoopEffectPhase.Reconciled)
        {
            return next.ReconciliationEvidenceId is not null
                && current.Outcome == next.Outcome
                && string.Equals(current.OutcomeEvidenceId, next.OutcomeEvidenceId, StringComparison.Ordinal)
                && current.Outcome != GovernedLoopEffectOutcome.None;
        }

        return current.ReconciliationEvidenceId is null && next.ReconciliationEvidenceId is null;
    }

    private static bool SameProjectionIdentity(GovernedLoopProjectionPayload current, GovernedLoopProjectionPayload next)
    {
        return string.Equals(current.ProjectionId, next.ProjectionId, StringComparison.Ordinal)
            && string.Equals(current.OperationId, next.OperationId, StringComparison.Ordinal)
            && current.Class == next.Class
            && string.Equals(current.SourceEvidenceId, next.SourceEvidenceId, StringComparison.Ordinal)
            && string.Equals(current.EffectId, next.EffectId, StringComparison.Ordinal);
    }

    private static bool IsSourceFromEffect(string sourceEvidenceId, GovernedLoopEffectPayload effect)
    {
        return string.Equals(sourceEvidenceId, effect.EffectId, StringComparison.Ordinal)
            || string.Equals(sourceEvidenceId, effect.OutcomeEvidenceId, StringComparison.Ordinal)
            || string.Equals(sourceEvidenceId, effect.ReconciliationEvidenceId, StringComparison.Ordinal);
    }

    private static void RequireTimeInLifecycle(
        DateTimeOffset timestamp,
        GovernedLoopRunLifecyclePayload lifecycle,
        bool allowAfterLifecycleUpdate,
        string path,
        List<GovernedLoopExecutionValidationError> errors)
    {
        if (timestamp < lifecycle.CreatedAtUtc
            || !allowAfterLifecycleUpdate && timestamp > lifecycle.UpdatedAtUtc)
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.TimestampOutsideLifecycle, path);
        }
    }

    private static void RequireSameBinding(GovernedLoopExecutionBinding expected, GovernedLoopExecutionBinding actual, string path, List<GovernedLoopExecutionValidationError> errors)
    {
        if (!Equals(expected, actual))
        {
            Add(errors, GovernedLoopExecutionValidationErrorCode.BindingMismatch, path);
        }
    }

    private static void AddOptionalSource(string? source, HashSet<string> sources)
    {
        if (source is not null)
        {
            sources.Add(source);
        }
    }

    private static void Add(List<GovernedLoopExecutionValidationError> errors, GovernedLoopExecutionValidationErrorCode code, string path)
    {
        if (errors.Count < GovernedLoopExecutionLimits.MaxValidationErrors)
        {
            errors.Add(Error(code, path));
        }
    }

    private static void AppendErrors(GovernedLoopExecutionValidationResult validation, List<GovernedLoopExecutionValidationError> errors)
    {
        foreach (var error in validation.Errors)
        {
            Add(errors, error.Code, error.Path);
        }
    }

    private static GovernedLoopExecutionValidationError Error(GovernedLoopExecutionValidationErrorCode code, string path)
    {
        return GovernedLoopExecutionValidationError.Create(code, path);
    }

    private static GovernedLoopExecutionValidationResult Result(IEnumerable<GovernedLoopExecutionValidationError> errors)
    {
        return GovernedLoopExecutionValidationResult.FromErrors(errors);
    }
}
