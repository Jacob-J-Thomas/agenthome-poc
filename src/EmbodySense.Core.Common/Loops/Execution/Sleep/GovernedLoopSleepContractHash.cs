using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Sleep;

/// <summary>Computes, applies, and verifies canonical sleep, wake, and local-coordinator hashes.</summary>
public static class GovernedLoopSleepContractHash
{
    /// <summary>Computes the deterministic checkpoint identity from its exact binding and wake condition.</summary>
    public static string ComputeCheckpointId(GovernedLoopSleepCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        RequireCheckpointBounds(checkpoint, includeCheckpointId: false);
        var canonical = Start("governed-loop-sleep-checkpoint-identity-v1");
        Append(canonical, checkpoint.SchemaVersion);
        AppendBinding(canonical, checkpoint.Binding);
        AppendWakeCondition(canonical, checkpoint.WakeMode, checkpoint.WakeDeadlineUtc, checkpoint.AuthenticatedEventReference);
        return Digest(canonical);
    }

    /// <summary>Computes the canonical checkpoint hash, excluding only its content-hash field.</summary>
    public static string Compute(GovernedLoopSleepCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        RequireCheckpointBounds(checkpoint, includeCheckpointId: true);
        var canonical = Start("governed-loop-sleep-checkpoint-v1");
        Append(canonical, checkpoint.SchemaVersion);
        Append(canonical, checkpoint.CheckpointId);
        AppendBinding(canonical, checkpoint.Binding);
        AppendWakeCondition(canonical, checkpoint.WakeMode, checkpoint.WakeDeadlineUtc, checkpoint.AuthenticatedEventReference);
        Append(canonical, checkpoint.PublishedAtUtc);
        return Digest(canonical);
    }

    /// <summary>Returns a checkpoint carrying its deterministic identity and canonical content hash.</summary>
    public static GovernedLoopSleepCheckpoint Apply(GovernedLoopSleepCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var identified = checkpoint with { CheckpointId = ComputeCheckpointId(checkpoint) };
        return identified with { ContentHash = Compute(identified) };
    }

    /// <summary>Gets whether a checkpoint retains its exact deterministic identity and content hash.</summary>
    public static bool Matches(GovernedLoopSleepCheckpoint? checkpoint)
        => checkpoint is not null
            && IsCanonical(checkpoint.CheckpointId)
            && FixedEquals(checkpoint.CheckpointId, () => ComputeCheckpointId(checkpoint))
            && IsCanonical(checkpoint.ContentHash)
            && FixedEquals(checkpoint.ContentHash, () => Compute(checkpoint));

    /// <summary>Computes the deterministic wake identity from an exact checkpoint and authenticated wake coordinates.</summary>
    public static string ComputeWakeId(GovernedLoopWakeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        RequireWakeIdentityBounds(identity, includeWakeId: false, includeContentHash: false);
        var canonical = Start("governed-loop-wake-identity-key-v1");
        Append(canonical, identity.SchemaVersion);
        Append(canonical, identity.CheckpointId);
        Append(canonical, identity.CheckpointHash);
        Append(canonical, (int)identity.WakeMode);
        Append(canonical, identity.AuthenticatedEventReference);
        Append(canonical, identity.AuthenticationEvidenceHash);
        return Digest(canonical);
    }

    /// <summary>Computes the canonical wake-identity hash, excluding only its content-hash field.</summary>
    public static string Compute(GovernedLoopWakeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        RequireWakeIdentityBounds(identity, includeWakeId: true, includeContentHash: false);
        var canonical = Start("governed-loop-wake-identity-v1");
        Append(canonical, identity.SchemaVersion);
        Append(canonical, identity.WakeId);
        Append(canonical, identity.CheckpointId);
        Append(canonical, identity.CheckpointHash);
        Append(canonical, (int)identity.WakeMode);
        Append(canonical, identity.AuthenticatedEventReference);
        Append(canonical, identity.AuthenticationEvidenceHash);
        return Digest(canonical);
    }

    /// <summary>Returns a wake identity carrying its deterministic identity and canonical content hash.</summary>
    public static GovernedLoopWakeIdentity Apply(GovernedLoopWakeIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var identified = identity with { WakeId = ComputeWakeId(identity) };
        return identified with { ContentHash = Compute(identified) };
    }

    /// <summary>Gets whether a wake identity retains its exact deterministic identity and content hash.</summary>
    public static bool Matches(GovernedLoopWakeIdentity? identity)
        => identity is not null
            && IsCanonical(identity.WakeId)
            && FixedEquals(identity.WakeId, () => ComputeWakeId(identity))
            && IsCanonical(identity.ContentHash)
            && FixedEquals(identity.ContentHash, () => Compute(identity));

    /// <summary>Computes the canonical wake-evidence hash, excluding only its content-hash field.</summary>
    public static string Compute(GovernedLoopWakeEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(evidence.Identity, nameof(evidence));
        RequireWakeIdentityBounds(evidence.Identity, includeWakeId: true, includeContentHash: true);
        RequireBounded(evidence.ContinuationOperationId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, nameof(evidence));
        RequireBounded(evidence.ContinuationEvidenceHash, GovernedLoopSleepContractLimits.Sha256HexCharacters, nameof(evidence));
        RequireBounded(evidence.DispositionEvidenceReference, GovernedLoopSleepContractLimits.MaxEvidenceReferenceCharacters, nameof(evidence));
        var canonical = Start("governed-loop-wake-evidence-v1");
        Append(canonical, evidence.SchemaVersion);
        Append(canonical, evidence.EvidenceVersion);
        AppendWakeIdentity(canonical, evidence.Identity);
        Append(canonical, (int)evidence.Disposition);
        Append(canonical, evidence.ContinuationOperationId);
        Append(canonical, evidence.ContinuationEvidenceHash);
        Append(canonical, evidence.DispositionEvidenceReference);
        Append(canonical, evidence.RecordedAtUtc);
        return Digest(canonical);
    }

    /// <summary>Returns wake evidence carrying its canonical content hash.</summary>
    public static GovernedLoopWakeEvidence Apply(GovernedLoopWakeEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return evidence with { ContentHash = Compute(evidence) };
    }

    /// <summary>Gets whether wake evidence retains its exact canonical content hash.</summary>
    public static bool Matches(GovernedLoopWakeEvidence? evidence)
        => evidence is not null
            && IsCanonical(evidence.ContentHash)
            && FixedEquals(evidence.ContentHash, () => Compute(evidence));

    /// <summary>Computes the canonical coordinator-ownership hash, excluding only its content-hash field.</summary>
    public static string Compute(GovernedLoopCoordinatorOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        RequireOwnershipBounds(ownership, includeContentHash: false, nameof(ownership));
        var canonical = Start("governed-loop-coordinator-ownership-v1");
        AppendOwnership(canonical, ownership, includeHash: false);
        return Digest(canonical);
    }

    /// <summary>Returns coordinator ownership carrying its canonical content hash.</summary>
    public static GovernedLoopCoordinatorOwnership Apply(GovernedLoopCoordinatorOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        return ownership with { ContentHash = Compute(ownership) };
    }

    /// <summary>Gets whether coordinator ownership retains its exact canonical content hash.</summary>
    public static bool Matches(GovernedLoopCoordinatorOwnership? ownership)
        => ownership is not null
            && IsCanonical(ownership.ContentHash)
            && FixedEquals(ownership.ContentHash, () => Compute(ownership));

    /// <summary>Computes the canonical coordinator-lifecycle hash, excluding only its content-hash field.</summary>
    public static string Compute(GovernedLoopCoordinatorLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(lifecycle.Ownership, nameof(lifecycle));
        RequireOwnershipBounds(lifecycle.Ownership, includeContentHash: true, nameof(lifecycle));
        var canonical = Start("governed-loop-coordinator-lifecycle-v1");
        Append(canonical, lifecycle.SchemaVersion);
        Append(canonical, lifecycle.LifecycleVersion);
        AppendOwnership(canonical, lifecycle.Ownership, includeHash: true);
        Append(canonical, (int)lifecycle.Status);
        Append(canonical, lifecycle.UpdatedAtUtc);
        Append(canonical, lifecycle.TerminalAtUtc);
        return Digest(canonical);
    }

    /// <summary>Returns coordinator lifecycle carrying its canonical content hash.</summary>
    public static GovernedLoopCoordinatorLifecycle Apply(GovernedLoopCoordinatorLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        return lifecycle with { ContentHash = Compute(lifecycle) };
    }

    /// <summary>Gets whether coordinator lifecycle retains its exact canonical content hash.</summary>
    public static bool Matches(GovernedLoopCoordinatorLifecycle? lifecycle)
        => lifecycle is not null
            && IsCanonical(lifecycle.ContentHash)
            && FixedEquals(lifecycle.ContentHash, () => Compute(lifecycle));

    /// <summary>Computes the canonical coordinator-heartbeat hash, excluding only its content-hash field.</summary>
    public static string Compute(GovernedLoopCoordinatorHeartbeat heartbeat)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        ArgumentNullException.ThrowIfNull(heartbeat.Ownership, nameof(heartbeat));
        RequireOwnershipBounds(heartbeat.Ownership, includeContentHash: true, nameof(heartbeat));
        var canonical = Start("governed-loop-coordinator-heartbeat-v1");
        Append(canonical, heartbeat.SchemaVersion);
        Append(canonical, heartbeat.HeartbeatSequence);
        AppendOwnership(canonical, heartbeat.Ownership, includeHash: true);
        Append(canonical, heartbeat.RecordedAtUtc);
        Append(canonical, heartbeat.LeaseExpiresAtUtc);
        return Digest(canonical);
    }

    /// <summary>Returns a coordinator heartbeat carrying its canonical content hash.</summary>
    public static GovernedLoopCoordinatorHeartbeat Apply(GovernedLoopCoordinatorHeartbeat heartbeat)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        return heartbeat with { ContentHash = Compute(heartbeat) };
    }

    /// <summary>Gets whether a coordinator heartbeat retains its exact canonical content hash.</summary>
    public static bool Matches(GovernedLoopCoordinatorHeartbeat? heartbeat)
        => heartbeat is not null
            && IsCanonical(heartbeat.ContentHash)
            && FixedEquals(heartbeat.ContentHash, () => Compute(heartbeat));

    /// <summary>Computes the canonical coordinator-failure hash, excluding only its content-hash field.</summary>
    public static string Compute(GovernedLoopCoordinatorFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(failure.Ownership, nameof(failure));
        RequireOwnershipBounds(failure.Ownership, includeContentHash: true, nameof(failure));
        RequireBounded(failure.DetailEvidenceReference, GovernedLoopSleepContractLimits.MaxEvidenceReferenceCharacters, nameof(failure));
        var canonical = Start("governed-loop-coordinator-failure-v1");
        Append(canonical, failure.SchemaVersion);
        Append(canonical, failure.FailureSequence);
        AppendOwnership(canonical, failure.Ownership, includeHash: true);
        Append(canonical, (int)failure.Kind);
        Append(canonical, failure.DetailEvidenceReference);
        Append(canonical, failure.OccurredAtUtc);
        return Digest(canonical);
    }

    /// <summary>Returns a coordinator failure carrying its canonical content hash.</summary>
    public static GovernedLoopCoordinatorFailure Apply(GovernedLoopCoordinatorFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure with { ContentHash = Compute(failure) };
    }

    /// <summary>Gets whether a coordinator failure retains its exact canonical content hash.</summary>
    public static bool Matches(GovernedLoopCoordinatorFailure? failure)
        => failure is not null
            && IsCanonical(failure.ContentHash)
            && FixedEquals(failure.ContentHash, () => Compute(failure));

    /// <summary>Computes the canonical coordinator-repair readiness hash, excluding only its content-hash field.</summary>
    public static string Compute(GovernedLoopCoordinatorRepairReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        RequireCoordinatorRepairReadinessBounds(readiness, includeContentHash: false, nameof(readiness));
        var canonical = Start("governed-loop-coordinator-repair-readiness-v1");
        Append(canonical, readiness.SchemaVersion);
        Append(canonical, readiness.WorkspaceId);
        Append(canonical, readiness.CoordinatorId);
        Append(canonical, readiness.ScheduleReady);
        Append(canonical, readiness.TriggerReady);
        Append(canonical, readiness.WakeReady);
        Append(canonical, readiness.HumanInputReady);
        Append(canonical, readiness.HumanReviewReady);
        Append(canonical, readiness.EvaluatedAtUtc);
        return Digest(canonical);
    }

    /// <summary>Returns coordinator-repair readiness carrying its canonical content hash.</summary>
    public static GovernedLoopCoordinatorRepairReadiness Apply(GovernedLoopCoordinatorRepairReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        return readiness with { ContentHash = Compute(readiness) };
    }

    /// <summary>Gets whether coordinator-repair readiness retains its exact canonical content hash.</summary>
    public static bool Matches(GovernedLoopCoordinatorRepairReadiness? readiness)
        => readiness is not null
            && IsCanonical(readiness.ContentHash)
            && FixedEquals(readiness.ContentHash, () => Compute(readiness));

    /// <summary>Computes the canonical coordinator-repair disposition hash, excluding only its content-hash field.</summary>
    public static string Compute(GovernedLoopCoordinatorRepairDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(disposition);
        RequireCoordinatorRepairDispositionBounds(disposition, includeContentHash: false, nameof(disposition));
        var canonical = Start("governed-loop-coordinator-repair-disposition-v1");
        Append(canonical, disposition.SchemaVersion);
        Append(canonical, disposition.WorkspaceId);
        Append(canonical, disposition.CoordinatorId);
        Append(canonical, disposition.OperationId);
        Append(canonical, disposition.ActorId);
        AppendOwnership(canonical, disposition.FailedOwnership, includeHash: true);
        Append(canonical, disposition.TerminalLifecycleHash);
        Append(canonical, disposition.LatestHeartbeatHash);
        Append(canonical, disposition.LatestFailureHash);
        Append(canonical, disposition.DependencyReadiness.ContentHash);
        Append(canonical, disposition.RecordedAtUtc);
        return Digest(canonical);
    }

    /// <summary>Returns a coordinator-repair disposition carrying its canonical content hash.</summary>
    public static GovernedLoopCoordinatorRepairDisposition Apply(GovernedLoopCoordinatorRepairDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(disposition);
        return disposition with { ContentHash = Compute(disposition) };
    }

    /// <summary>Gets whether a coordinator-repair disposition retains its exact canonical content hash.</summary>
    public static bool Matches(GovernedLoopCoordinatorRepairDisposition? disposition)
        => disposition is not null
            && IsCanonical(disposition.ContentHash)
            && FixedEquals(disposition.ContentHash, () => Compute(disposition));

    private static void AppendBinding(StringBuilder canonical, GovernedLoopSleepBinding binding)
    {
        Append(canonical, binding.Execution.SchemaVersion);
        Append(canonical, binding.Execution.RunId);
        Append(canonical, binding.Execution.Revision.SchemaVersion);
        Append(canonical, binding.Execution.Revision.GraphId);
        Append(canonical, binding.Execution.Revision.RevisionId);
        Append(canonical, binding.Execution.Revision.ExecutableHash);
        Append(canonical, binding.Execution.ExecutionGeneration);
        Append(canonical, binding.Publication.SchemaVersion);
        Append(canonical, binding.Publication.Revision.SchemaVersion);
        Append(canonical, binding.Publication.Revision.GraphId);
        Append(canonical, binding.Publication.Revision.RevisionId);
        Append(canonical, binding.Publication.Revision.ExecutableHash);
        Append(canonical, binding.Publication.PublicationOperationId);
        Append(canonical, binding.Publication.ValidationEvidenceHash);
        Append(canonical, binding.FrontierVersion);
        Append(canonical, binding.FrontierHash);
        Append(canonical, binding.ActivationOrdinal);
        Append(canonical, binding.CycleId);
        Append(canonical, binding.CycleIteration);
        Append(canonical, binding.NodeId);
        Append(canonical, binding.NodeVisitOrdinal);
        Append(canonical, binding.WaitAttempt);
        Append(canonical, binding.WaitOperationId);
    }

    private static void AppendWakeCondition(StringBuilder canonical, GovernedLoopWakeMode mode, DateTimeOffset? deadlineUtc, string? eventReference)
    {
        Append(canonical, (int)mode);
        Append(canonical, deadlineUtc);
        Append(canonical, eventReference);
    }

    private static void AppendWakeIdentity(StringBuilder canonical, GovernedLoopWakeIdentity identity)
    {
        Append(canonical, identity.SchemaVersion);
        Append(canonical, identity.WakeId);
        Append(canonical, identity.CheckpointId);
        Append(canonical, identity.CheckpointHash);
        Append(canonical, (int)identity.WakeMode);
        Append(canonical, identity.AuthenticatedEventReference);
        Append(canonical, identity.AuthenticationEvidenceHash);
        Append(canonical, identity.ContentHash);
    }

    private static void AppendOwnership(StringBuilder canonical, GovernedLoopCoordinatorOwnership ownership, bool includeHash)
    {
        Append(canonical, ownership.SchemaVersion);
        Append(canonical, ownership.CoordinatorId);
        Append(canonical, ownership.OwnerId);
        Append(canonical, ownership.OwnershipEpoch);
        Append(canonical, ownership.AcquiredAtUtc);
        if (includeHash)
        {
            Append(canonical, ownership.ContentHash);
        }
    }

    private static void RequireCheckpointBounds(GovernedLoopSleepCheckpoint checkpoint, bool includeCheckpointId)
    {
        ArgumentNullException.ThrowIfNull(checkpoint.Binding, nameof(checkpoint));
        ArgumentNullException.ThrowIfNull(checkpoint.Binding.Execution, nameof(checkpoint));
        ArgumentNullException.ThrowIfNull(checkpoint.Binding.Execution.Revision, nameof(checkpoint));
        ArgumentNullException.ThrowIfNull(checkpoint.Binding.Publication, nameof(checkpoint));
        ArgumentNullException.ThrowIfNull(checkpoint.Binding.Publication.Revision, nameof(checkpoint));
        if (includeCheckpointId)
        {
            RequireBounded(checkpoint.CheckpointId, GovernedLoopSleepContractLimits.Sha256HexCharacters, nameof(checkpoint));
        }

        RequireBounded(checkpoint.Binding.Execution.RunId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, nameof(checkpoint));
        RequireBounded(checkpoint.Binding.Execution.Revision.GraphId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, nameof(checkpoint));
        RequireBounded(checkpoint.Binding.Execution.Revision.RevisionId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, nameof(checkpoint));
        RequireBounded(checkpoint.Binding.Execution.Revision.ExecutableHash, GovernedLoopSleepContractLimits.Sha256HexCharacters, nameof(checkpoint));
        RequireBounded(checkpoint.Binding.Publication.Revision.GraphId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, nameof(checkpoint));
        RequireBounded(checkpoint.Binding.Publication.Revision.RevisionId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, nameof(checkpoint));
        RequireBounded(checkpoint.Binding.Publication.Revision.ExecutableHash, GovernedLoopSleepContractLimits.Sha256HexCharacters, nameof(checkpoint));
        RequireBounded(checkpoint.Binding.Publication.PublicationOperationId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, nameof(checkpoint));
        RequireBounded(checkpoint.Binding.Publication.ValidationEvidenceHash, GovernedLoopSleepContractLimits.Sha256HexCharacters, nameof(checkpoint));
        RequireBounded(checkpoint.Binding.FrontierHash, GovernedLoopSleepContractLimits.Sha256HexCharacters, nameof(checkpoint));
        RequireBounded(checkpoint.Binding.CycleId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, nameof(checkpoint));
        RequireBounded(checkpoint.Binding.NodeId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, nameof(checkpoint));
        RequireBounded(checkpoint.Binding.WaitOperationId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, nameof(checkpoint));
        RequireBounded(checkpoint.AuthenticatedEventReference, GovernedLoopSleepContractLimits.MaxEvidenceReferenceCharacters, nameof(checkpoint));
    }

    private static void RequireWakeIdentityBounds(
        GovernedLoopWakeIdentity identity,
        bool includeWakeId,
        bool includeContentHash)
    {
        if (includeWakeId)
        {
            RequireBounded(identity.WakeId, GovernedLoopSleepContractLimits.Sha256HexCharacters, nameof(identity));
        }

        RequireBounded(identity.CheckpointId, GovernedLoopSleepContractLimits.Sha256HexCharacters, nameof(identity));
        RequireBounded(identity.CheckpointHash, GovernedLoopSleepContractLimits.Sha256HexCharacters, nameof(identity));
        RequireBounded(identity.AuthenticatedEventReference, GovernedLoopSleepContractLimits.MaxEvidenceReferenceCharacters, nameof(identity));
        RequireBounded(identity.AuthenticationEvidenceHash, GovernedLoopSleepContractLimits.Sha256HexCharacters, nameof(identity));
        if (includeContentHash)
        {
            RequireBounded(identity.ContentHash, GovernedLoopSleepContractLimits.Sha256HexCharacters, nameof(identity));
        }
    }

    private static void RequireOwnershipBounds(
        GovernedLoopCoordinatorOwnership ownership,
        bool includeContentHash,
        string parameterName)
    {
        RequireBounded(ownership.CoordinatorId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, parameterName);
        RequireBounded(ownership.OwnerId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, parameterName);
        if (includeContentHash)
        {
            RequireBounded(ownership.ContentHash, GovernedLoopSleepContractLimits.Sha256HexCharacters, parameterName);
        }
    }

    private static void RequireCoordinatorRepairReadinessBounds(
        GovernedLoopCoordinatorRepairReadiness readiness,
        bool includeContentHash,
        string parameterName)
    {
        RequireBounded(readiness.WorkspaceId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, parameterName);
        RequireBounded(readiness.CoordinatorId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, parameterName);
        if (includeContentHash)
        {
            RequireBounded(readiness.ContentHash, GovernedLoopSleepContractLimits.Sha256HexCharacters, parameterName);
        }
    }

    private static void RequireCoordinatorRepairDispositionBounds(
        GovernedLoopCoordinatorRepairDisposition disposition,
        bool includeContentHash,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(disposition.FailedOwnership, parameterName);
        ArgumentNullException.ThrowIfNull(disposition.DependencyReadiness, parameterName);
        RequireBounded(disposition.WorkspaceId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, parameterName);
        RequireBounded(disposition.CoordinatorId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, parameterName);
        RequireBounded(disposition.OperationId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, parameterName);
        RequireBounded(disposition.ActorId, GovernedLoopSleepContractLimits.MaxIdentifierCharacters, parameterName);
        RequireOwnershipBounds(disposition.FailedOwnership, includeContentHash: true, parameterName);
        RequireBounded(disposition.TerminalLifecycleHash, GovernedLoopSleepContractLimits.Sha256HexCharacters, parameterName);
        RequireBounded(disposition.LatestHeartbeatHash, GovernedLoopSleepContractLimits.Sha256HexCharacters, parameterName);
        RequireBounded(disposition.LatestFailureHash, GovernedLoopSleepContractLimits.Sha256HexCharacters, parameterName);
        RequireCoordinatorRepairReadinessBounds(disposition.DependencyReadiness, includeContentHash: true, parameterName);
        if (includeContentHash)
        {
            RequireBounded(disposition.ContentHash, GovernedLoopSleepContractLimits.Sha256HexCharacters, parameterName);
        }
    }

    private static void RequireBounded(string? value, int maximumCharacters, string parameterName)
    {
        if (value?.Length > maximumCharacters)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Canonical hash inputs may contain at most {maximumCharacters} characters per field.");
        }
    }

    private static StringBuilder Start(string domain)
    {
        var canonical = new StringBuilder(1_024);
        Append(canonical, domain);
        return canonical;
    }

    private static string Digest(StringBuilder canonical)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();

    private static bool FixedEquals(string actual, Func<string> compute)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(compute()));
        }
        catch (Exception exception) when (exception is ArgumentException or NullReferenceException)
        {
            return false;
        }
    }

    private static bool IsCanonical(string? value)
        => value?.Length == GovernedLoopSleepContractLimits.Sha256HexCharacters
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Append(StringBuilder canonical, DateTimeOffset value)
        => Append(canonical, value.ToString("O", CultureInfo.InvariantCulture));

    private static void Append(StringBuilder canonical, DateTimeOffset? value)
    {
        if (value is null)
        {
            canonical.Append("-1:");
        }
        else
        {
            Append(canonical, value.Value);
        }
    }

    private static void Append(StringBuilder canonical, int? value)
    {
        if (value is null)
        {
            canonical.Append("-1:");
        }
        else
        {
            Append(canonical, value.Value);
        }
    }

    private static void Append(StringBuilder canonical, int value)
        => Append(canonical, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder canonical, bool value)
        => Append(canonical, value ? "1" : "0");

    private static void Append(StringBuilder canonical, long value)
        => Append(canonical, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder canonical, string? value)
    {
        if (value is null)
        {
            canonical.Append("-1:");
            return;
        }

        var normalized = value.Normalize(NormalizationForm.FormC);
        canonical.Append(Encoding.UTF8.GetByteCount(normalized).ToString(CultureInfo.InvariantCulture));
        canonical.Append(':');
        canonical.Append(normalized);
    }
}
