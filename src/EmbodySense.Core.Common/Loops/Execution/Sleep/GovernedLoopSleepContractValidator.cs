using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Common.Loops.Revisions;

namespace EmbodySense.Core.Common.Loops.Execution.Sleep;

/// <summary>Validates bounded schema-1 sleep, wake, and local-coordinator evidence without executing work.</summary>
public static class GovernedLoopSleepContractValidator
{
    /// <summary>Validates one exact coordinator-repair readiness evidence object.</summary>
    public static GovernedLoopSleepValidationResult Validate(GovernedLoopCoordinatorRepairReadiness? readiness)
    {
        var errors = new List<GovernedLoopSleepValidationError>();
        if (readiness is null)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.Required, "readiness");
            return GovernedLoopSleepValidationResult.FromErrors(errors);
        }

        if (readiness.SchemaVersion != GovernedLoopCoordinatorRepairReadiness.CurrentSchemaVersion)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.UnsupportedSchemaVersion, "schemaVersion");
        }
        if (!IsWorkspaceId(readiness.WorkspaceId))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.InvalidIdentity, "workspaceId");
        }
        if (!CustomLoopArtifactIdentifier.IsValid(readiness.CoordinatorId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.InvalidIdentity, "coordinatorId");
        }
        if (!IsUtc(readiness.EvaluatedAtUtc))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.InvalidTimestamp, "evaluatedAtUtc");
        }
        if (!GovernedLoopSleepContractHash.Matches(readiness))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.IntegrityMismatch, "contentHash");
        }
        return GovernedLoopSleepValidationResult.FromErrors(errors);
    }

    /// <summary>Validates one immutable repair authorization against its exact failed evidence.</summary>
    public static GovernedLoopSleepValidationResult Validate(GovernedLoopCoordinatorRepairDisposition? disposition)
    {
        var errors = new List<GovernedLoopSleepValidationError>();
        if (disposition is null)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.Required, "disposition");
            return GovernedLoopSleepValidationResult.FromErrors(errors);
        }

        if (disposition.SchemaVersion != GovernedLoopCoordinatorRepairDisposition.CurrentSchemaVersion)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.UnsupportedSchemaVersion, "schemaVersion");
        }
        if (!IsWorkspaceId(disposition.WorkspaceId))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.InvalidIdentity, "workspaceId");
        }
        if (!CustomLoopArtifactIdentifier.IsValid(disposition.CoordinatorId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.InvalidIdentity, "coordinatorId");
        }
        if (!CustomLoopArtifactIdentifier.IsValid(disposition.OperationId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.InvalidIdentity, "operationId");
        }
        if (!CustomLoopArtifactIdentifier.IsValid(disposition.ActorId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.InvalidIdentity, "actorId");
        }
        if (!Validate(disposition.FailedOwnership).IsValid)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.InvalidComposition, "failedOwnership");
        }
        if (!IsCanonicalHash(disposition.TerminalLifecycleHash))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.InvalidHash, "terminalLifecycleHash");
        }
        if (!IsCanonicalHash(disposition.LatestHeartbeatHash))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.InvalidHash, "latestHeartbeatHash");
        }
        if (!IsCanonicalHash(disposition.LatestFailureHash))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.InvalidHash, "latestFailureHash");
        }
        if (!Validate(disposition.DependencyReadiness).IsValid
            || !string.Equals(disposition.WorkspaceId, disposition.DependencyReadiness.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(disposition.CoordinatorId, disposition.DependencyReadiness.CoordinatorId, StringComparison.Ordinal)
            || !GovernedLoopCoordinatorRepairReadinessContract.IsReady(disposition.DependencyReadiness))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.BindingMismatch, "dependencyReadiness");
        }
        if (!IsUtc(disposition.RecordedAtUtc) || disposition.RecordedAtUtc < disposition.DependencyReadiness.EvaluatedAtUtc)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.InvalidTimestamp, "recordedAtUtc");
        }
        if (!GovernedLoopSleepContractHash.Matches(disposition))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.IntegrityMismatch, "contentHash");
        }
        return GovernedLoopSleepValidationResult.FromErrors(errors);
    }
    /// <summary>Validates one complete immutable sleeping checkpoint.</summary>
    public static GovernedLoopSleepValidationResult Validate(GovernedLoopSleepCheckpoint? checkpoint)
    {
        var errors = new List<GovernedLoopSleepValidationError>();
        ValidateCheckpoint(checkpoint, "$", errors);
        return Result(errors);
    }

    /// <summary>Validates one deterministic wake identity.</summary>
    public static GovernedLoopSleepValidationResult Validate(GovernedLoopWakeIdentity? identity)
    {
        var errors = new List<GovernedLoopSleepValidationError>();
        ValidateWakeIdentity(identity, "$", errors);
        return Result(errors);
    }

    /// <summary>Validates one complete immutable wake-evidence state.</summary>
    public static GovernedLoopSleepValidationResult Validate(GovernedLoopWakeEvidence? evidence)
    {
        var errors = new List<GovernedLoopSleepValidationError>();
        ValidateWakeEvidence(evidence, "$", errors);
        return Result(errors);
    }

    /// <summary>Validates one complete immutable coordinator ownership claim.</summary>
    public static GovernedLoopSleepValidationResult Validate(GovernedLoopCoordinatorOwnership? ownership)
    {
        var errors = new List<GovernedLoopSleepValidationError>();
        ValidateOwnership(ownership, "$", errors);
        return Result(errors);
    }

    /// <summary>Validates one complete immutable coordinator lifecycle state.</summary>
    public static GovernedLoopSleepValidationResult Validate(GovernedLoopCoordinatorLifecycle? lifecycle)
    {
        var errors = new List<GovernedLoopSleepValidationError>();
        ValidateLifecycle(lifecycle, "$", errors);
        return Result(errors);
    }

    /// <summary>Validates one complete immutable coordinator heartbeat.</summary>
    public static GovernedLoopSleepValidationResult Validate(GovernedLoopCoordinatorHeartbeat? heartbeat)
    {
        var errors = new List<GovernedLoopSleepValidationError>();
        ValidateHeartbeat(heartbeat, "$", errors);
        return Result(errors);
    }

    /// <summary>Validates one complete immutable coordinator failure.</summary>
    public static GovernedLoopSleepValidationResult Validate(GovernedLoopCoordinatorFailure? failure)
    {
        var errors = new List<GovernedLoopSleepValidationError>();
        ValidateFailure(failure, "$", errors);
        return Result(errors);
    }

    /// <summary>Validates that authenticated wake coordinates target one exact immutable checkpoint.</summary>
    public static GovernedLoopSleepValidationResult ValidateComposition(
        GovernedLoopSleepCheckpoint? checkpoint,
        GovernedLoopWakeIdentity? identity)
    {
        var errors = new List<GovernedLoopSleepValidationError>();
        ValidateCheckpoint(checkpoint, "$.checkpoint", errors);
        ValidateWakeIdentity(identity, "$.identity", errors);
        if (checkpoint is not null && identity is not null && errors.Count == 0
            && !IsWakeBoundToCheckpoint(checkpoint, identity))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.BindingMismatch, "$.identity");
        }

        return Result(errors);
    }

    /// <summary>Validates that one wake-evidence state targets and occurs within the chronology of one exact checkpoint.</summary>
    public static GovernedLoopSleepValidationResult ValidateComposition(
        GovernedLoopSleepCheckpoint? checkpoint,
        GovernedLoopWakeEvidence? evidence)
    {
        var errors = new List<GovernedLoopSleepValidationError>();
        ValidateCheckpoint(checkpoint, "$.checkpoint", errors);
        ValidateWakeEvidence(evidence, "$.evidence", errors);
        if (checkpoint is not null && evidence is not null && errors.Count == 0)
        {
            if (!IsWakeBoundToCheckpoint(checkpoint, evidence.Identity))
            {
                Add(errors, GovernedLoopSleepValidationErrorCode.BindingMismatch, "$.evidence.identity");
            }

            if (evidence.RecordedAtUtc < checkpoint.PublishedAtUtc)
            {
                Add(errors, GovernedLoopSleepValidationErrorCode.InvalidTimestamp, "$.evidence.recordedAtUtc");
            }

            if (checkpoint.WakeMode == GovernedLoopWakeMode.Timestamp
                && checkpoint.WakeDeadlineUtc is { } deadlineUtc
                && RequiresTimestampEligibility(evidence)
                && evidence.RecordedAtUtc < deadlineUtc)
            {
                Add(errors, GovernedLoopSleepValidationErrorCode.InvalidTimestamp, "$.evidence.recordedAtUtc");
            }
        }

        return Result(errors);
    }

    /// <summary>Validates that one coordinator lifecycle state belongs to the exact authoritative ownership claim.</summary>
    public static GovernedLoopSleepValidationResult ValidateComposition(
        GovernedLoopCoordinatorOwnership? authoritativeOwnership,
        GovernedLoopCoordinatorLifecycle? lifecycle)
    {
        var errors = new List<GovernedLoopSleepValidationError>();
        ValidateOwnership(authoritativeOwnership, "$.authoritativeOwnership", errors);
        ValidateLifecycle(lifecycle, "$.lifecycle", errors);
        if (authoritativeOwnership is not null && lifecycle is not null && errors.Count == 0
            && !SameOwnership(authoritativeOwnership, lifecycle.Ownership))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.BindingMismatch, "$.lifecycle.ownership");
        }

        return Result(errors);
    }

    /// <summary>Validates that one coordinator heartbeat belongs to the exact authoritative ownership claim.</summary>
    public static GovernedLoopSleepValidationResult ValidateComposition(
        GovernedLoopCoordinatorOwnership? authoritativeOwnership,
        GovernedLoopCoordinatorHeartbeat? heartbeat)
    {
        var errors = new List<GovernedLoopSleepValidationError>();
        ValidateOwnership(authoritativeOwnership, "$.authoritativeOwnership", errors);
        ValidateHeartbeat(heartbeat, "$.heartbeat", errors);
        if (authoritativeOwnership is not null && heartbeat is not null && errors.Count == 0
            && !SameOwnership(authoritativeOwnership, heartbeat.Ownership))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.BindingMismatch, "$.heartbeat.ownership");
        }

        return Result(errors);
    }

    /// <summary>Validates that one coordinator failure belongs to the exact authoritative ownership claim.</summary>
    public static GovernedLoopSleepValidationResult ValidateComposition(
        GovernedLoopCoordinatorOwnership? authoritativeOwnership,
        GovernedLoopCoordinatorFailure? failure)
    {
        var errors = new List<GovernedLoopSleepValidationError>();
        ValidateOwnership(authoritativeOwnership, "$.authoritativeOwnership", errors);
        ValidateFailure(failure, "$.failure", errors);
        if (authoritativeOwnership is not null && failure is not null && errors.Count == 0
            && !SameOwnership(authoritativeOwnership, failure.Ownership))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.BindingMismatch, "$.failure.ownership");
        }

        return Result(errors);
    }

    /// <summary>Validates one exact contiguous coordinator-ownership successor.</summary>
    public static GovernedLoopSleepValidationResult ValidateTransition(
        GovernedLoopCoordinatorOwnership? current,
        GovernedLoopCoordinatorOwnership? next)
    {
        var errors = new List<GovernedLoopSleepValidationError>();
        ValidateOwnership(current, "$.current", errors);
        ValidateOwnership(next, "$.next", errors);
        if (current is not null && next is not null && errors.Count == 0)
        {
            ValidateOwnershipTransition(current, next, errors);
        }

        return Result(errors);
    }

    /// <summary>
    /// Validates one fenced ownership handoff after the exact current owner's exclusive heartbeat lease has expired.
    /// </summary>
    public static GovernedLoopSleepValidationResult ValidateHandoff(
        GovernedLoopCoordinatorOwnership? current,
        GovernedLoopCoordinatorHeartbeat? currentHeartbeat,
        GovernedLoopCoordinatorOwnership? next)
    {
        var errors = new List<GovernedLoopSleepValidationError>();
        ValidateOwnership(current, "$.current", errors);
        ValidateHeartbeat(currentHeartbeat, "$.currentHeartbeat", errors);
        ValidateOwnership(next, "$.next", errors);
        if (current is not null && currentHeartbeat is not null && next is not null && errors.Count == 0)
        {
            var heartbeatIsCurrent = SameOwnership(current, currentHeartbeat.Ownership);
            if (!heartbeatIsCurrent)
            {
                Add(errors, GovernedLoopSleepValidationErrorCode.BindingMismatch, "$.currentHeartbeat.ownership");
            }

            ValidateOwnershipTransition(current, next, errors);
            if (heartbeatIsCurrent && next.AcquiredAtUtc < currentHeartbeat.LeaseExpiresAtUtc)
            {
                Add(errors, GovernedLoopSleepValidationErrorCode.IllegalTransition, "$.next.acquiredAtUtc");
            }
        }

        return Result(errors);
    }

    /// <summary>
    /// Validates a fenced restart by the exact owner that durably drained its own coordinator to
    /// <see cref="GovernedLoopCoordinatorStatus.Stopped"/>. This is intentionally narrower than a handoff: it never
    /// permits a different owner to bypass the live heartbeat lease and it never restarts a failed lifecycle.
    /// </summary>
    public static GovernedLoopSleepValidationResult ValidateTerminalSameOwnerRestart(
        GovernedLoopCoordinatorOwnership? current,
        GovernedLoopCoordinatorLifecycle? currentLifecycle,
        GovernedLoopCoordinatorHeartbeat? currentHeartbeat,
        GovernedLoopCoordinatorOwnership? next)
    {
        var errors = new List<GovernedLoopSleepValidationError>();
        ValidateOwnership(current, "$.current", errors);
        ValidateLifecycle(currentLifecycle, "$.currentLifecycle", errors);
        ValidateHeartbeat(currentHeartbeat, "$.currentHeartbeat", errors);
        ValidateOwnership(next, "$.next", errors);
        if (current is not null && currentLifecycle is not null && currentHeartbeat is not null && next is not null && errors.Count == 0)
        {
            if (!SameOwnership(current, currentLifecycle.Ownership))
            {
                Add(errors, GovernedLoopSleepValidationErrorCode.BindingMismatch, "$.currentLifecycle.ownership");
            }

            if (!SameOwnership(current, currentHeartbeat.Ownership))
            {
                Add(errors, GovernedLoopSleepValidationErrorCode.BindingMismatch, "$.currentHeartbeat.ownership");
            }

            if (currentLifecycle.Status != GovernedLoopCoordinatorStatus.Stopped
                || currentLifecycle.TerminalAtUtc is null
                || !string.Equals(current.CoordinatorId, next.CoordinatorId, StringComparison.Ordinal)
                || !string.Equals(current.OwnerId, next.OwnerId, StringComparison.Ordinal)
                || next.OwnershipEpoch != current.OwnershipEpoch + 1
                || next.AcquiredAtUtc < current.AcquiredAtUtc
                || next.AcquiredAtUtc < currentLifecycle.UpdatedAtUtc)
            {
                Add(errors, GovernedLoopSleepValidationErrorCode.IllegalTransition, "$.next");
            }
        }

        return Result(errors);
    }

    /// <summary>Validates one contiguous exact wake-evidence transition.</summary>
    public static GovernedLoopSleepValidationResult ValidateTransition(
        GovernedLoopWakeEvidence? current,
        GovernedLoopWakeEvidence? next)
    {
        var errors = new List<GovernedLoopSleepValidationError>();
        ValidateWakeEvidence(current, "$.current", errors);
        ValidateWakeEvidence(next, "$.next", errors);
        if (current is not null && next is not null && errors.Count == 0)
        {
            if (next.EvidenceVersion != current.EvidenceVersion + 1)
            {
                Add(errors, GovernedLoopSleepValidationErrorCode.InvalidSuccessorVersion, "$.next.evidenceVersion");
            }

            if (!string.Equals(current.Identity.ContentHash, next.Identity.ContentHash, StringComparison.Ordinal)
                || !string.Equals(current.Identity.WakeId, next.Identity.WakeId, StringComparison.Ordinal)
                || current.ContinuationOperationId is not null
                    && !string.Equals(current.ContinuationOperationId, next.ContinuationOperationId, StringComparison.Ordinal))
            {
                Add(errors, GovernedLoopSleepValidationErrorCode.ImmutableEvidenceChanged, "$.next.identity");
            }

            if (next.RecordedAtUtc < current.RecordedAtUtc
                || !GovernedLoopSleepStateMatrix.IsWakeTransitionAllowed(current.Disposition, next.Disposition))
            {
                Add(errors, GovernedLoopSleepValidationErrorCode.IllegalTransition, "$.next.disposition");
            }
        }

        return Result(errors);
    }

    /// <summary>Validates one contiguous exact coordinator lifecycle transition.</summary>
    public static GovernedLoopSleepValidationResult ValidateTransition(
        GovernedLoopCoordinatorLifecycle? current,
        GovernedLoopCoordinatorLifecycle? next)
    {
        var errors = new List<GovernedLoopSleepValidationError>();
        ValidateLifecycle(current, "$.current", errors);
        ValidateLifecycle(next, "$.next", errors);
        if (current is not null && next is not null && errors.Count == 0)
        {
            if (next.LifecycleVersion != current.LifecycleVersion + 1)
            {
                Add(errors, GovernedLoopSleepValidationErrorCode.InvalidSuccessorVersion, "$.next.lifecycleVersion");
            }

            if (!SameOwnership(current.Ownership, next.Ownership))
            {
                Add(errors, GovernedLoopSleepValidationErrorCode.ImmutableEvidenceChanged, "$.next.ownership");
            }

            if (next.UpdatedAtUtc < current.UpdatedAtUtc
                || !GovernedLoopSleepStateMatrix.IsCoordinatorTransitionAllowed(current.Status, next.Status))
            {
                Add(errors, GovernedLoopSleepValidationErrorCode.IllegalTransition, "$.next.status");
            }
        }

        return Result(errors);
    }

    /// <summary>Validates one contiguous exact coordinator-heartbeat transition.</summary>
    public static GovernedLoopSleepValidationResult ValidateTransition(
        GovernedLoopCoordinatorHeartbeat? current,
        GovernedLoopCoordinatorHeartbeat? next)
    {
        var errors = new List<GovernedLoopSleepValidationError>();
        ValidateHeartbeat(current, "$.current", errors);
        ValidateHeartbeat(next, "$.next", errors);
        if (current is not null && next is not null && errors.Count == 0)
        {
            if (next.HeartbeatSequence != current.HeartbeatSequence + 1)
            {
                Add(errors, GovernedLoopSleepValidationErrorCode.InvalidSuccessorVersion, "$.next.heartbeatSequence");
            }

            if (!SameOwnership(current.Ownership, next.Ownership))
            {
                Add(errors, GovernedLoopSleepValidationErrorCode.ImmutableEvidenceChanged, "$.next.ownership");
            }

            if (next.RecordedAtUtc < current.RecordedAtUtc
                || next.RecordedAtUtc >= current.LeaseExpiresAtUtc
                || next.LeaseExpiresAtUtc <= current.LeaseExpiresAtUtc)
            {
                Add(errors, GovernedLoopSleepValidationErrorCode.IllegalTransition, "$.next.leaseExpiresAtUtc");
            }
        }

        return Result(errors);
    }

    /// <summary>Validates one contiguous exact coordinator-failure append.</summary>
    public static GovernedLoopSleepValidationResult ValidateTransition(
        GovernedLoopCoordinatorFailure? current,
        GovernedLoopCoordinatorFailure? next)
    {
        var errors = new List<GovernedLoopSleepValidationError>();
        ValidateFailure(current, "$.current", errors);
        ValidateFailure(next, "$.next", errors);
        if (current is not null && next is not null && errors.Count == 0)
        {
            if (next.FailureSequence != current.FailureSequence + 1)
            {
                Add(errors, GovernedLoopSleepValidationErrorCode.InvalidSuccessorVersion, "$.next.failureSequence");
            }

            if (!SameOwnership(current.Ownership, next.Ownership))
            {
                Add(errors, GovernedLoopSleepValidationErrorCode.ImmutableEvidenceChanged, "$.next.ownership");
            }

            if (next.OccurredAtUtc < current.OccurredAtUtc)
            {
                Add(errors, GovernedLoopSleepValidationErrorCode.IllegalTransition, "$.next.occurredAtUtc");
            }
        }

        return Result(errors);
    }

    private static void ValidateCheckpoint(
        GovernedLoopSleepCheckpoint? checkpoint,
        string path,
        List<GovernedLoopSleepValidationError> errors)
    {
        if (checkpoint is null)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.Required, path);
            return;
        }

        var initialErrorCount = errors.Count;
        ValidateSchema(checkpoint.SchemaVersion, $"{path}.schemaVersion", errors);
        ValidateHash(checkpoint.CheckpointId, $"{path}.checkpointId", errors);
        ValidateBinding(checkpoint.Binding, $"{path}.binding", errors);
        ValidateEnumeration(checkpoint.WakeMode, $"{path}.wakeMode", errors);
        ValidateUtc(checkpoint.PublishedAtUtc, $"{path}.publishedAtUtc", errors);
        ValidateWakeCondition(checkpoint, path, errors);
        ValidateHash(checkpoint.ContentHash, $"{path}.contentHash", errors);
        if (errors.Count == initialErrorCount && !GovernedLoopSleepContractHash.Matches(checkpoint))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.IntegrityMismatch, $"{path}.contentHash");
        }
    }

    private static void ValidateBinding(
        GovernedLoopSleepBinding? binding,
        string path,
        List<GovernedLoopSleepValidationError> errors)
    {
        if (binding is null)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.Required, path);
            return;
        }

        if (!IsExecutionBindingValid(binding.Execution)
            || !GovernedLoopRevisionContractValidator.Validate(binding.Publication).IsValid)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.BindingMismatch, path);
            return;
        }

        if (binding.Execution.Revision != binding.Publication.Revision)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.BindingMismatch, $"{path}.publication.revision");
        }

        ValidatePositive(binding.FrontierVersion, GovernedLoopSleepContractLimits.MaxVersion, $"{path}.frontierVersion", errors);
        ValidateHash(binding.FrontierHash, $"{path}.frontierHash", errors);
        if (binding.ActivationOrdinal is < 0 or >= GovernedLoopExecutionLimits.MaxFrontierNodes)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.LimitExceeded, $"{path}.activationOrdinal");
        }

        var hasCycleId = binding.CycleId is not null;
        var hasCycleIteration = binding.CycleIteration is not null;
        if (hasCycleId != hasCycleIteration)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.InvalidComposition, $"{path}.cycleId");
        }
        else if (hasCycleId)
        {
            ValidateIdentifier(binding.CycleId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, $"{path}.cycleId", errors);
            if (binding.CycleIteration is < 1 or > GovernedLoopExecutionLimits.MaxCycleIterations)
            {
                Add(errors, GovernedLoopSleepValidationErrorCode.LimitExceeded, $"{path}.cycleIteration");
            }
        }

        ValidateIdentifier(binding.NodeId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, $"{path}.nodeId", errors);
        if (binding.NodeVisitOrdinal is < 1 or > GovernedLoopExecutionLimits.MaxNodeVisits)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.LimitExceeded, $"{path}.nodeVisitOrdinal");
        }

        if (binding.WaitAttempt is < 1 or > GovernedLoopSleepContractLimits.MaxWaitAttempt)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.LimitExceeded, $"{path}.waitAttempt");
        }

        ValidateIdentifier(binding.WaitOperationId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, $"{path}.waitOperationId", errors);
    }

    private static void ValidateWakeCondition(
        GovernedLoopSleepCheckpoint checkpoint,
        string path,
        List<GovernedLoopSleepValidationError> errors)
    {
        switch (checkpoint.WakeMode)
        {
            case GovernedLoopWakeMode.Timestamp:
                if (checkpoint.WakeDeadlineUtc is not { } deadline
                    || !IsUtc(deadline)
                    || checkpoint.AuthenticatedEventReference is not null)
                {
                    Add(errors, GovernedLoopSleepValidationErrorCode.InvalidComposition, $"{path}.wakeDeadlineUtc");
                }

                break;
            case GovernedLoopWakeMode.AuthenticatedEvent:
                if (checkpoint.WakeDeadlineUtc is not null || checkpoint.AuthenticatedEventReference is null)
                {
                    Add(errors, GovernedLoopSleepValidationErrorCode.InvalidComposition, $"{path}.authenticatedEventReference");
                }
                else
                {
                    ValidateIdentifier(
                        checkpoint.AuthenticatedEventReference,
                        GovernedLoopSleepContractLimits.MaxEvidenceReferenceCharacters,
                        $"{path}.authenticatedEventReference",
                        errors);
                }

                break;
        }
    }

    private static void ValidateWakeIdentity(
        GovernedLoopWakeIdentity? identity,
        string path,
        List<GovernedLoopSleepValidationError> errors)
    {
        if (identity is null)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.Required, path);
            return;
        }

        var initialErrorCount = errors.Count;
        ValidateSchema(identity.SchemaVersion, $"{path}.schemaVersion", errors);
        ValidateHash(identity.WakeId, $"{path}.wakeId", errors);
        ValidateHash(identity.CheckpointId, $"{path}.checkpointId", errors);
        ValidateHash(identity.CheckpointHash, $"{path}.checkpointHash", errors);
        ValidateEnumeration(identity.WakeMode, $"{path}.wakeMode", errors);
        switch (identity.WakeMode)
        {
            case GovernedLoopWakeMode.Timestamp:
                if (identity.AuthenticatedEventReference is not null || identity.AuthenticationEvidenceHash is not null)
                {
                    Add(errors, GovernedLoopSleepValidationErrorCode.InvalidComposition, $"{path}.authenticatedEventReference");
                }

                break;
            case GovernedLoopWakeMode.AuthenticatedEvent:
                ValidateIdentifier(
                    identity.AuthenticatedEventReference,
                    GovernedLoopSleepContractLimits.MaxEvidenceReferenceCharacters,
                    $"{path}.authenticatedEventReference",
                    errors);
                ValidateHash(identity.AuthenticationEvidenceHash, $"{path}.authenticationEvidenceHash", errors);
                break;
        }

        ValidateHash(identity.ContentHash, $"{path}.contentHash", errors);
        if (errors.Count == initialErrorCount && !GovernedLoopSleepContractHash.Matches(identity))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.IntegrityMismatch, $"{path}.contentHash");
        }
    }

    private static void ValidateWakeEvidence(
        GovernedLoopWakeEvidence? evidence,
        string path,
        List<GovernedLoopSleepValidationError> errors)
    {
        if (evidence is null)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.Required, path);
            return;
        }

        var initialErrorCount = errors.Count;
        ValidateSchema(evidence.SchemaVersion, $"{path}.schemaVersion", errors);
        ValidatePositive(evidence.EvidenceVersion, GovernedLoopSleepContractLimits.MaxVersion, $"{path}.evidenceVersion", errors);
        ValidateWakeIdentity(evidence.Identity, $"{path}.identity", errors);
        ValidateEnumeration(evidence.Disposition, $"{path}.disposition", errors);
        ValidateUtc(evidence.RecordedAtUtc, $"{path}.recordedAtUtc", errors);
        if (evidence.ContinuationOperationId is not null)
        {
            ValidateIdentifier(evidence.ContinuationOperationId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, $"{path}.continuationOperationId", errors);
        }

        if (evidence.ContinuationEvidenceHash is not null)
        {
            ValidateHash(evidence.ContinuationEvidenceHash, $"{path}.continuationEvidenceHash", errors);
        }

        if (evidence.DispositionEvidenceReference is not null)
        {
            ValidateIdentifier(
                evidence.DispositionEvidenceReference,
                GovernedLoopSleepContractLimits.MaxEvidenceReferenceCharacters,
                $"{path}.dispositionEvidenceReference",
                errors);
        }

        if (!IsWakeEvidenceShapeValid(evidence))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.InvalidComposition, path);
        }

        ValidateHash(evidence.ContentHash, $"{path}.contentHash", errors);
        if (errors.Count == initialErrorCount && !GovernedLoopSleepContractHash.Matches(evidence))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.IntegrityMismatch, $"{path}.contentHash");
        }
    }

    private static bool IsWakeEvidenceShapeValid(GovernedLoopWakeEvidence evidence)
    {
        var hasOperation = evidence.ContinuationOperationId is not null;
        var hasContinuationHash = evidence.ContinuationEvidenceHash is not null;
        var hasDispositionReference = evidence.DispositionEvidenceReference is not null;
        return evidence.Disposition switch
        {
            GovernedLoopWakeDisposition.Prepared => hasOperation && !hasContinuationHash && !hasDispositionReference,
            GovernedLoopWakeDisposition.Committed => hasOperation && hasContinuationHash && !hasDispositionReference,
            GovernedLoopWakeDisposition.AmbiguousAttempt => hasOperation && !hasContinuationHash && hasDispositionReference,
            GovernedLoopWakeDisposition.Failed => !hasContinuationHash && hasDispositionReference,
            GovernedLoopWakeDisposition.Duplicate
                or GovernedLoopWakeDisposition.Late
                or GovernedLoopWakeDisposition.Stale
                or GovernedLoopWakeDisposition.Conflict
                or GovernedLoopWakeDisposition.Cancelled
                or GovernedLoopWakeDisposition.Expired
                or GovernedLoopWakeDisposition.Paused
                or GovernedLoopWakeDisposition.ReviewBlocked => !hasOperation && !hasContinuationHash && hasDispositionReference,
            _ => false
        };
    }

    private static void ValidateOwnership(
        GovernedLoopCoordinatorOwnership? ownership,
        string path,
        List<GovernedLoopSleepValidationError> errors)
    {
        if (ownership is null)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.Required, path);
            return;
        }

        var initialErrorCount = errors.Count;
        ValidateSchema(ownership.SchemaVersion, $"{path}.schemaVersion", errors);
        ValidateIdentifier(ownership.CoordinatorId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, $"{path}.coordinatorId", errors);
        ValidateIdentifier(ownership.OwnerId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, $"{path}.ownerId", errors);
        ValidatePositive(ownership.OwnershipEpoch, GovernedLoopSleepContractLimits.MaxVersion, $"{path}.ownershipEpoch", errors);
        ValidateUtc(ownership.AcquiredAtUtc, $"{path}.acquiredAtUtc", errors);
        ValidateHash(ownership.ContentHash, $"{path}.contentHash", errors);
        if (errors.Count == initialErrorCount && !GovernedLoopSleepContractHash.Matches(ownership))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.IntegrityMismatch, $"{path}.contentHash");
        }
    }

    private static void ValidateLifecycle(
        GovernedLoopCoordinatorLifecycle? lifecycle,
        string path,
        List<GovernedLoopSleepValidationError> errors)
    {
        if (lifecycle is null)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.Required, path);
            return;
        }

        var initialErrorCount = errors.Count;
        ValidateSchema(lifecycle.SchemaVersion, $"{path}.schemaVersion", errors);
        ValidatePositive(lifecycle.LifecycleVersion, GovernedLoopSleepContractLimits.MaxVersion, $"{path}.lifecycleVersion", errors);
        ValidateOwnership(lifecycle.Ownership, $"{path}.ownership", errors);
        ValidateEnumeration(lifecycle.Status, $"{path}.status", errors);
        ValidateUtc(lifecycle.UpdatedAtUtc, $"{path}.updatedAtUtc", errors);
        if (lifecycle.Ownership is not null && lifecycle.UpdatedAtUtc < lifecycle.Ownership.AcquiredAtUtc)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.InvalidTimestamp, $"{path}.updatedAtUtc");
        }

        var terminal = lifecycle.Status is GovernedLoopCoordinatorStatus.Stopped or GovernedLoopCoordinatorStatus.Failed;
        if (terminal != lifecycle.TerminalAtUtc.HasValue
            || lifecycle.TerminalAtUtc is { } terminalAt && (!IsUtc(terminalAt) || terminalAt != lifecycle.UpdatedAtUtc))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.InvalidComposition, $"{path}.terminalAtUtc");
        }

        ValidateHash(lifecycle.ContentHash, $"{path}.contentHash", errors);
        if (errors.Count == initialErrorCount && !GovernedLoopSleepContractHash.Matches(lifecycle))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.IntegrityMismatch, $"{path}.contentHash");
        }
    }

    private static void ValidateHeartbeat(
        GovernedLoopCoordinatorHeartbeat? heartbeat,
        string path,
        List<GovernedLoopSleepValidationError> errors)
    {
        if (heartbeat is null)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.Required, path);
            return;
        }

        var initialErrorCount = errors.Count;
        ValidateSchema(heartbeat.SchemaVersion, $"{path}.schemaVersion", errors);
        ValidatePositive(heartbeat.HeartbeatSequence, GovernedLoopSleepContractLimits.MaxVersion, $"{path}.heartbeatSequence", errors);
        ValidateOwnership(heartbeat.Ownership, $"{path}.ownership", errors);
        ValidateUtc(heartbeat.RecordedAtUtc, $"{path}.recordedAtUtc", errors);
        ValidateUtc(heartbeat.LeaseExpiresAtUtc, $"{path}.leaseExpiresAtUtc", errors);
        if (heartbeat.Ownership is not null
            && (heartbeat.RecordedAtUtc < heartbeat.Ownership.AcquiredAtUtc || heartbeat.LeaseExpiresAtUtc <= heartbeat.RecordedAtUtc))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.InvalidTimestamp, $"{path}.leaseExpiresAtUtc");
        }

        ValidateHash(heartbeat.ContentHash, $"{path}.contentHash", errors);
        if (errors.Count == initialErrorCount && !GovernedLoopSleepContractHash.Matches(heartbeat))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.IntegrityMismatch, $"{path}.contentHash");
        }
    }

    private static void ValidateFailure(
        GovernedLoopCoordinatorFailure? failure,
        string path,
        List<GovernedLoopSleepValidationError> errors)
    {
        if (failure is null)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.Required, path);
            return;
        }

        var initialErrorCount = errors.Count;
        ValidateSchema(failure.SchemaVersion, $"{path}.schemaVersion", errors);
        ValidatePositive(failure.FailureSequence, GovernedLoopSleepContractLimits.MaxVersion, $"{path}.failureSequence", errors);
        ValidateOwnership(failure.Ownership, $"{path}.ownership", errors);
        ValidateEnumeration(failure.Kind, $"{path}.kind", errors);
        if (failure.DetailEvidenceReference is not null)
        {
            ValidateIdentifier(
                failure.DetailEvidenceReference,
                GovernedLoopSleepContractLimits.MaxEvidenceReferenceCharacters,
                $"{path}.detailEvidenceReference",
                errors);
        }

        ValidateUtc(failure.OccurredAtUtc, $"{path}.occurredAtUtc", errors);
        if (failure.Ownership is not null && failure.OccurredAtUtc < failure.Ownership.AcquiredAtUtc)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.InvalidTimestamp, $"{path}.occurredAtUtc");
        }

        ValidateHash(failure.ContentHash, $"{path}.contentHash", errors);
        if (errors.Count == initialErrorCount && !GovernedLoopSleepContractHash.Matches(failure))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.IntegrityMismatch, $"{path}.contentHash");
        }
    }

    private static bool IsExecutionBindingValid(GovernedLoopExecutionBinding? binding)
    {
        if (binding is null)
        {
            return false;
        }

        try
        {
            _ = GovernedLoopExecutionBinding.Create(binding.SchemaVersion, binding.RunId, binding.Revision, binding.ExecutionGeneration);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentNullException)
        {
            return false;
        }
    }

    private static bool IsWakeBoundToCheckpoint(GovernedLoopSleepCheckpoint checkpoint, GovernedLoopWakeIdentity identity)
        => string.Equals(checkpoint.CheckpointId, identity.CheckpointId, StringComparison.Ordinal)
            && string.Equals(checkpoint.ContentHash, identity.CheckpointHash, StringComparison.Ordinal)
            && checkpoint.WakeMode == identity.WakeMode
            && string.Equals(checkpoint.AuthenticatedEventReference, identity.AuthenticatedEventReference, StringComparison.Ordinal);

    private static bool RequiresTimestampEligibility(GovernedLoopWakeEvidence evidence)
        => evidence.Disposition is GovernedLoopWakeDisposition.Prepared
            or GovernedLoopWakeDisposition.Committed
            or GovernedLoopWakeDisposition.Late
            or GovernedLoopWakeDisposition.AmbiguousAttempt
            || evidence.Disposition == GovernedLoopWakeDisposition.Failed && evidence.ContinuationOperationId is not null;

    private static void ValidateOwnershipTransition(
        GovernedLoopCoordinatorOwnership current,
        GovernedLoopCoordinatorOwnership next,
        List<GovernedLoopSleepValidationError> errors)
    {
        if (!string.Equals(current.CoordinatorId, next.CoordinatorId, StringComparison.Ordinal))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.ImmutableEvidenceChanged, "$.next.coordinatorId");
        }

        if (string.Equals(current.OwnerId, next.OwnerId, StringComparison.Ordinal))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.IllegalTransition, "$.next.ownerId");
        }

        if (next.OwnershipEpoch != current.OwnershipEpoch + 1)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.InvalidSuccessorVersion, "$.next.ownershipEpoch");
        }

        if (next.AcquiredAtUtc < current.AcquiredAtUtc)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.IllegalTransition, "$.next.acquiredAtUtc");
        }
    }

    private static bool SameOwnership(GovernedLoopCoordinatorOwnership current, GovernedLoopCoordinatorOwnership next)
        => string.Equals(current.ContentHash, next.ContentHash, StringComparison.Ordinal)
            && current == next;

    private static void ValidateSchema(int schemaVersion, string path, List<GovernedLoopSleepValidationError> errors)
    {
        if (schemaVersion != GovernedLoopSleepContractLimits.CurrentSchemaVersion)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.UnsupportedSchemaVersion, path);
        }
    }

    private static void ValidateIdentifier(string? value, int maximum, string path, List<GovernedLoopSleepValidationError> errors)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(value, maximum))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.InvalidIdentity, path);
        }
    }

    private static void ValidateHash(string? value, string path, List<GovernedLoopSleepValidationError> errors)
    {
        if (value?.Length != GovernedLoopSleepContractLimits.Sha256HexCharacters
            || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.InvalidHash, path);
        }
    }

    private static void ValidatePositive(long value, long maximum, string path, List<GovernedLoopSleepValidationError> errors)
    {
        if (value is < 1 || value > maximum)
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.LimitExceeded, path);
        }
    }

    private static void ValidateEnumeration<TEnum>(TEnum value, string path, List<GovernedLoopSleepValidationError> errors)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.InvalidEnumeration, path);
        }
    }

    private static void ValidateUtc(DateTimeOffset value, string path, List<GovernedLoopSleepValidationError> errors)
    {
        if (!IsUtc(value))
        {
            Add(errors, GovernedLoopSleepValidationErrorCode.InvalidTimestamp, path);
        }
    }

    private static bool IsUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;

    private static bool IsWorkspaceId(string? value)
        => ContextualRoleWorkspaceId.IsValid(value);

    private static bool IsCanonicalHash(string? value)
        => value is { Length: GovernedLoopSleepContractLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Add(
        List<GovernedLoopSleepValidationError> errors,
        GovernedLoopSleepValidationErrorCode code,
        string path)
    {
        if (errors.Count < GovernedLoopSleepContractLimits.MaxValidationErrors)
        {
            errors.Add(GovernedLoopSleepValidationError.Create(code, path));
        }
    }

    private static GovernedLoopSleepValidationResult Result(IEnumerable<GovernedLoopSleepValidationError> errors)
        => GovernedLoopSleepValidationResult.FromErrors(errors);
}
