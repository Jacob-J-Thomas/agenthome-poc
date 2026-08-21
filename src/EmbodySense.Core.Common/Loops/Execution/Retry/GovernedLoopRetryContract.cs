using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Execution.Retry.Models;
using EmbodySense.Core.Common.Loops.Failures;
using EmbodySense.Core.Common.Loops.Failures.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Inference.Profiles;

namespace EmbodySense.Core.Common.Loops.Execution.Retry;

/// <summary>Creates, validates, hashes, copies, and evaluates deterministic schema-1 retry contracts.</summary>
public static class GovernedLoopRetryContract
{
    private static readonly HashSet<GovernedLoopFailureClass> _retryableClasses =
    [
        GovernedLoopFailureClass.DependencyUnavailableBeforeDispatch,
        GovernedLoopFailureClass.DispatchProvedNotStarted,
        GovernedLoopFailureClass.RetryableNoEffect,
        GovernedLoopFailureClass.TimeoutCancellationNoEffect,
    ];

    /// <summary>Creates one canonical policy and computes its exact content hash.</summary>
    public static GovernedLoopRetryPolicy CreatePolicy(
        string policyId,
        string nodeId,
        IReadOnlyList<GovernedLoopFailureClass> failureClasses,
        IReadOnlyList<string> serverCodes,
        int maximumAttempts,
        long perAttemptTimeoutMilliseconds,
        long maximumElapsedMilliseconds,
        GovernedLoopRetryBackoffStrategy backoffStrategy,
        long initialDelayMilliseconds,
        long maximumDelayMilliseconds,
        GovernedLoopRetryJitterStrategy jitterStrategy,
        long maximumJitterMilliseconds,
        long? maximumTokens = null,
        int? maximumToolCalls = null,
        long? maximumCostMicrounits = null,
        string? maximumCostCurrency = null,
        int? maximumResourceUnits = null)
    {
        ArgumentNullException.ThrowIfNull(failureClasses);
        ArgumentNullException.ThrowIfNull(serverCodes);
        var canonical = new GovernedLoopRetryPolicy(
            GovernedLoopRetryPolicy.CurrentSchemaVersion,
            policyId,
            nodeId,
            Array.AsReadOnly(failureClasses.Order().ToArray()),
            Array.AsReadOnly(serverCodes.Order(StringComparer.Ordinal).ToArray()),
            maximumAttempts,
            perAttemptTimeoutMilliseconds,
            maximumElapsedMilliseconds,
            backoffStrategy,
            initialDelayMilliseconds,
            maximumDelayMilliseconds,
            jitterStrategy,
            maximumJitterMilliseconds,
            maximumTokens,
            maximumToolCalls,
            maximumCostMicrounits,
            maximumCostCurrency,
            maximumResourceUnits,
            string.Empty);
        RequirePolicy(canonical);
        return canonical with { ContentHash = ComputePolicyHash(canonical) };
    }

    /// <summary>Creates one immutable series identity bound to exact failure evidence and the earliest enclosing deadline.</summary>
    public static GovernedLoopRetrySeriesIdentity CreateSeries(
        GovernedLoopRetryPolicy policy,
        GovernedLoopFailureEvidence failure,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? enclosingDeadlineUtc = null)
    {
        if (!IsValid(policy))
        {
            throw new ArgumentException("The retry policy is not hash-authenticated.", nameof(policy));
        }
        if (!GovernedLoopFailureEvidenceContract.IsValid(failure))
        {
            throw new ArgumentException("The failure evidence is malformed or unauthenticated.", nameof(failure));
        }
        if (!string.Equals(policy.NodeId, failure.NodeId, StringComparison.Ordinal)
            || failure.RetrySafety != GovernedLoopFailureRetrySafety.RetryableWithExactIntent
            || !_retryableClasses.Contains(failure.FailureClass)
            || !policy.FailureClasses.Contains(failure.FailureClass)
            || policy.ServerCodes.Count > 0 && !policy.ServerCodes.Contains(failure.ServerCode, StringComparer.Ordinal))
        {
            throw new ArgumentException("The failure evidence is not affirmatively retry-safe and admitted by this exact policy.", nameof(failure));
        }

        var normalizedStart = RequireUtc(startedAtUtc, nameof(startedAtUtc));
        var policyDeadline = AddMilliseconds(normalizedStart, policy.MaximumElapsedMilliseconds, nameof(policy));
        var deadline = enclosingDeadlineUtc is null
            ? policyDeadline
            : Earlier(policyDeadline, RequireUtc(enclosingDeadlineUtc.Value, nameof(enclosingDeadlineUtc)));
        if (deadline <= normalizedStart)
        {
            throw new ArgumentException("The immutable retry deadline must be later than the series start.", nameof(enclosingDeadlineUtc));
        }

        var seriesSeed = $"{failure.WorkspaceId}\n{failure.RunId}\n{failure.Revision.ExecutableHash}\n{failure.ExecutionGeneration}\n{failure.ActivationOrdinal}\n{failure.VisitOrdinal}\n{failure.NodeId}\n{failure.EvidenceId}\n{failure.ContentHash}\n{policy.PolicyId}\n{policy.ContentHash}\n{normalizedStart:O}\n{deadline:O}";
        var seriesId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seriesSeed)));
        var candidate = new GovernedLoopRetrySeriesIdentity(
            GovernedLoopRetrySeriesIdentity.CurrentSchemaVersion,
            seriesId,
            failure.WorkspaceId,
            failure.RunId,
            failure.Revision,
            failure.ExecutionGeneration,
            failure.ActivationOrdinal,
            failure.VisitOrdinal,
            failure.NodeId,
            failure.EvidenceId,
            failure.ContentHash,
            policy.PolicyId,
            policy.ContentHash,
            normalizedStart,
            deadline,
            string.Empty);
        return candidate with { ContentHash = ComputeSeriesHash(candidate) };
    }

    /// <summary>Computes the exact deterministic delay for one next-attempt ordinal.</summary>
    public static TimeSpan ComputeDelay(GovernedLoopRetryPolicy policy, string seriesId, int nextAttempt)
    {
        if (!IsValid(policy)) throw new ArgumentException("The retry policy is not hash-authenticated.", nameof(policy));
        RequireHash(seriesId, nameof(seriesId));
        if (nextAttempt is < 2 || nextAttempt > policy.MaximumAttempts)
        {
            throw new ArgumentOutOfRangeException(nameof(nextAttempt));
        }

        long delay = policy.BackoffStrategy switch
        {
            GovernedLoopRetryBackoffStrategy.None => 0,
            GovernedLoopRetryBackoffStrategy.Fixed => policy.InitialDelayMilliseconds,
            GovernedLoopRetryBackoffStrategy.Exponential => ExponentialDelay(policy, nextAttempt),
            _ => throw new ArgumentException("The retry strategy is undefined.", nameof(policy)),
        };
        if (policy.JitterStrategy == GovernedLoopRetryJitterStrategy.DeterministicBounded)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{seriesId}\n{nextAttempt}"));
            var value = BinaryPrimitives.ReadUInt64BigEndian(bytes);
            var jitter = value % ((ulong)policy.MaximumJitterMilliseconds + 1);
            delay = Math.Min(policy.MaximumDelayMilliseconds, checked(delay + (long)jitter));
        }

        return TimeSpan.FromMilliseconds(delay);
    }

    /// <summary>Creates the stable distinct idempotency identity reserved for one exact next retry attempt.</summary>
    public static string CreateAttemptOperationId(string seriesId, int nextAttempt)
    {
        RequireHash(seriesId, nameof(seriesId));
        if (nextAttempt is < 2 or > GovernedLoopRetryContractLimits.MaximumAttempts)
        {
            throw new ArgumentOutOfRangeException(nameof(nextAttempt));
        }

        return "retry-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{seriesId}\n{nextAttempt}")))[..48];
    }

    /// <summary>Creates one authenticated retry-state version after validating its complete shape.</summary>
    public static GovernedLoopRetryState CreateState(
        GovernedLoopRetrySeriesIdentity identity,
        long stateVersion,
        GovernedLoopRetryStateDisposition disposition,
        int currentAttempt,
        string currentAttemptOperationId,
        int? nextAttempt,
        string? attemptOperationId,
        GovernedLoopRetryBudgetSnapshot budget,
        DateTimeOffset? nextRetryAtUtc,
        string? wakeCheckpointId,
        string? wakeCheckpointHash,
        string failureEvidenceId,
        string failureEvidenceHash,
        DateTimeOffset recordedAtUtc)
    {
        var candidate = new GovernedLoopRetryState(
            GovernedLoopRetryState.CurrentSchemaVersion,
            identity,
            stateVersion,
            disposition,
            currentAttempt,
            currentAttemptOperationId,
            nextAttempt,
            attemptOperationId,
            budget,
            nextRetryAtUtc,
            wakeCheckpointId,
            wakeCheckpointHash,
            failureEvidenceId,
            failureEvidenceHash,
            recordedAtUtc,
            string.Empty);
        RequireState(candidate);
        return candidate with { ContentHash = ComputeStateHash(candidate) };
    }

    /// <summary>Creates one defensive immutable copy of a validated retry policy.</summary>
    public static GovernedLoopRetryPolicy CopyPolicy(GovernedLoopRetryPolicy policy)
    {
        if (!IsValid(policy))
        {
            throw new ArgumentException("The retry policy is not hash-authenticated.", nameof(policy));
        }

        return policy with
        {
            FailureClasses = Array.AsReadOnly(policy.FailureClasses.ToArray()),
            ServerCodes = Array.AsReadOnly(policy.ServerCodes.ToArray()),
        };
    }

    /// <summary>Returns whether the next state is one exact contiguous monotonic successor.</summary>
    public static bool IsValidTransition(GovernedLoopRetryState? current, GovernedLoopRetryState? next)
    {
        if (!IsValid(current) || !IsValid(next)
            || current!.Identity.ContentHash != next!.Identity.ContentHash
            || next.StateVersion != current.StateVersion + 1
            || next.RecordedAtUtc < current.RecordedAtUtc
            || next.CurrentAttempt < current.CurrentAttempt
            || next.CurrentAttempt == current.CurrentAttempt
                && !string.Equals(next.CurrentAttemptOperationId, current.CurrentAttemptOperationId, StringComparison.Ordinal)
            || next.CurrentAttempt > current.CurrentAttempt
                && (next.CurrentAttempt != current.NextAttempt
                    || !string.Equals(next.CurrentAttemptOperationId, current.AttemptOperationId, StringComparison.Ordinal))
            || next.Budget.Attempts < current.Budget.Attempts
            || Decreased(current.Budget.Tokens, next.Budget.Tokens)
            || Decreased(current.Budget.ToolCalls, next.Budget.ToolCalls)
            || Decreased(current.Budget.CostMicrounits, next.Budget.CostMicrounits)
            || !string.Equals(current.Budget.CostCurrency, next.Budget.CostCurrency, StringComparison.Ordinal)
            || Decreased(current.Budget.ResourceUnits, next.Budget.ResourceUnits)
            || !PreservesFailureEvidence(current, next)
            || !PreservesAttemptReservation(current, next)
            || !PreservesWakeCheckpoint(current, next)
            || RequiresExactBudget(current, next) && !Equals(current.Budget, next.Budget))
        {
            return false;
        }

        return current.Disposition switch
        {
            GovernedLoopRetryStateDisposition.FailureRetained => next.Disposition is GovernedLoopRetryStateDisposition.Scheduled or GovernedLoopRetryStateDisposition.Due or GovernedLoopRetryStateDisposition.Exhausted or GovernedLoopRetryStateDisposition.Stopped or GovernedLoopRetryStateDisposition.NeedsReview,
            GovernedLoopRetryStateDisposition.Scheduled => next.Disposition == GovernedLoopRetryStateDisposition.Scheduled
                ? ScheduledCheckpointAttachment(current, next)
                : next.Disposition is GovernedLoopRetryStateDisposition.Due or GovernedLoopRetryStateDisposition.Stopped or GovernedLoopRetryStateDisposition.NeedsReview,
            GovernedLoopRetryStateDisposition.Due => next.Disposition is GovernedLoopRetryStateDisposition.Reserved or GovernedLoopRetryStateDisposition.Exhausted or GovernedLoopRetryStateDisposition.Stopped or GovernedLoopRetryStateDisposition.NeedsReview,
            GovernedLoopRetryStateDisposition.Reserved => next.Disposition is GovernedLoopRetryStateDisposition.Dispatched or GovernedLoopRetryStateDisposition.AttemptCompleted or GovernedLoopRetryStateDisposition.Stopped or GovernedLoopRetryStateDisposition.NeedsReview,
            GovernedLoopRetryStateDisposition.Dispatched => next.Disposition is GovernedLoopRetryStateDisposition.AttemptCompleted or GovernedLoopRetryStateDisposition.NeedsReview,
            GovernedLoopRetryStateDisposition.AttemptCompleted => next.Disposition is GovernedLoopRetryStateDisposition.Scheduled or GovernedLoopRetryStateDisposition.Due or GovernedLoopRetryStateDisposition.Exhausted or GovernedLoopRetryStateDisposition.Stopped or GovernedLoopRetryStateDisposition.NeedsReview,
            _ => false,
        };
    }

    /// <summary>Returns whether one policy is structurally and cryptographically valid.</summary>
    public static bool IsValid(GovernedLoopRetryPolicy? policy)
    {
        try
        {
            RequirePolicy(policy);
            return string.Equals(policy!.ContentHash, ComputePolicyHash(policy), StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>Returns whether one retry-series identity is structurally and cryptographically valid.</summary>
    public static bool IsValid(GovernedLoopRetrySeriesIdentity? identity)
    {
        try
        {
            RequireSeries(identity);
            return string.Equals(identity!.ContentHash, ComputeSeriesHash(identity), StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>Returns whether one durable retry state is structurally and cryptographically valid.</summary>
    public static bool IsValid(GovernedLoopRetryState? state)
    {
        try
        {
            RequireState(state);
            return string.Equals(state!.ContentHash, ComputeStateHash(state), StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            return false;
        }
    }

    /// <summary>Computes the canonical policy digest with its digest field excluded.</summary>
    public static string ComputePolicyHash(GovernedLoopRetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", policy.SchemaVersion);
        writer.WriteString("policyId", policy.PolicyId);
        writer.WriteString("nodeId", policy.NodeId);
        writer.WritePropertyName("failureClasses");
        writer.WriteStartArray();
        foreach (var value in policy.FailureClasses) writer.WriteStringValue(Token(value));
        writer.WriteEndArray();
        WriteStrings(writer, "serverCodes", policy.ServerCodes);
        writer.WriteNumber("maximumAttempts", policy.MaximumAttempts);
        writer.WriteNumber("perAttemptTimeoutMilliseconds", policy.PerAttemptTimeoutMilliseconds);
        writer.WriteNumber("maximumElapsedMilliseconds", policy.MaximumElapsedMilliseconds);
        writer.WriteString("backoffStrategy", Token(policy.BackoffStrategy));
        writer.WriteNumber("initialDelayMilliseconds", policy.InitialDelayMilliseconds);
        writer.WriteNumber("maximumDelayMilliseconds", policy.MaximumDelayMilliseconds);
        writer.WriteString("jitterStrategy", Token(policy.JitterStrategy));
        writer.WriteNumber("maximumJitterMilliseconds", policy.MaximumJitterMilliseconds);
        WriteNullable(writer, "maximumTokens", policy.MaximumTokens);
        WriteNullable(writer, "maximumToolCalls", policy.MaximumToolCalls);
        WriteNullable(writer, "maximumCostMicrounits", policy.MaximumCostMicrounits);
        writer.WriteString("maximumCostCurrency", policy.MaximumCostCurrency);
        WriteNullable(writer, "maximumResourceUnits", policy.MaximumResourceUnits);
        writer.WriteEndObject();
        writer.Flush();
        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    /// <summary>Computes the canonical retry-series digest with its digest field excluded.</summary>
    public static string ComputeSeriesHash(GovernedLoopRetrySeriesIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", identity.SchemaVersion);
        writer.WriteString("seriesId", identity.SeriesId);
        writer.WriteString("workspaceId", identity.WorkspaceId);
        writer.WriteString("runId", identity.RunId);
        WriteRevision(writer, identity.Revision);
        writer.WriteNumber("executionGeneration", identity.ExecutionGeneration);
        writer.WriteNumber("activationOrdinal", identity.ActivationOrdinal);
        writer.WriteNumber("visitOrdinal", identity.VisitOrdinal);
        writer.WriteString("nodeId", identity.NodeId);
        writer.WriteString("originatingFailureEvidenceId", identity.OriginatingFailureEvidenceId);
        writer.WriteString("originatingFailureEvidenceHash", identity.OriginatingFailureEvidenceHash);
        writer.WriteString("policyId", identity.PolicyId);
        writer.WriteString("policyHash", identity.PolicyHash);
        writer.WriteString("startedAtUtc", identity.StartedAtUtc.ToUniversalTime());
        writer.WriteString("deadlineUtc", identity.DeadlineUtc.ToUniversalTime());
        writer.WriteEndObject();
        writer.Flush();
        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    /// <summary>Computes the canonical retry-state digest with its digest field excluded.</summary>
    public static string ComputeStateHash(GovernedLoopRetryState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", state.SchemaVersion);
        writer.WriteString("identityHash", state.Identity.ContentHash);
        writer.WriteNumber("stateVersion", state.StateVersion);
        writer.WriteString("disposition", Token(state.Disposition));
        writer.WriteNumber("currentAttempt", state.CurrentAttempt);
        writer.WriteString("currentAttemptOperationId", state.CurrentAttemptOperationId);
        WriteNullable(writer, "nextAttempt", state.NextAttempt);
        writer.WriteString("attemptOperationId", state.AttemptOperationId);
        writer.WritePropertyName("budget");
        writer.WriteStartObject();
        writer.WriteNumber("attempts", state.Budget.Attempts);
        WriteNullable(writer, "tokens", state.Budget.Tokens);
        WriteNullable(writer, "toolCalls", state.Budget.ToolCalls);
        WriteNullable(writer, "costMicrounits", state.Budget.CostMicrounits);
        writer.WriteString("costCurrency", state.Budget.CostCurrency);
        WriteNullable(writer, "resourceUnits", state.Budget.ResourceUnits);
        writer.WriteEndObject();
        if (state.NextRetryAtUtc is { } nextRetry) writer.WriteString("nextRetryAtUtc", nextRetry.ToUniversalTime()); else writer.WriteNull("nextRetryAtUtc");
        writer.WriteString("wakeCheckpointId", state.WakeCheckpointId);
        writer.WriteString("wakeCheckpointHash", state.WakeCheckpointHash);
        writer.WriteString("failureEvidenceId", state.FailureEvidenceId);
        writer.WriteString("failureEvidenceHash", state.FailureEvidenceHash);
        writer.WriteString("recordedAtUtc", state.RecordedAtUtc.ToUniversalTime());
        writer.WriteEndObject();
        writer.Flush();
        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    private static void RequirePolicy(GovernedLoopRetryPolicy? policy)
    {
        if (policy is null
            || policy.SchemaVersion != GovernedLoopRetryPolicy.CurrentSchemaVersion
            || !CustomLoopArtifactIdentifier.IsValid(policy.PolicyId)
            || !CustomLoopArtifactIdentifier.IsValid(policy.NodeId)
            || policy.FailureClasses is null
            || policy.ServerCodes is null
            || policy.FailureClasses.Count is < 1 or > GovernedLoopRetryContractLimits.MaximumFailureClasses
            || policy.FailureClasses.Any(value => !_retryableClasses.Contains(value))
            || !policy.FailureClasses.SequenceEqual(policy.FailureClasses.Order())
            || policy.FailureClasses.Distinct().Count() != policy.FailureClasses.Count
            || policy.ServerCodes.Count > GovernedLoopRetryContractLimits.MaximumServerCodes
            || !policy.ServerCodes.SequenceEqual(policy.ServerCodes.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || policy.ServerCodes.Distinct(StringComparer.Ordinal).Count() != policy.ServerCodes.Count
            || policy.ServerCodes.Any(value => !GovernedLoopFailureEvidenceContract.IsServerCode(value))
            || policy.MaximumAttempts is < 2 or > GovernedLoopRetryContractLimits.MaximumAttempts
            || policy.PerAttemptTimeoutMilliseconds is < 1 or > GovernedLoopRetryContractLimits.MaximumPerAttemptTimeoutMilliseconds
            || policy.MaximumElapsedMilliseconds < policy.PerAttemptTimeoutMilliseconds
            || policy.MaximumElapsedMilliseconds > GovernedLoopRetryContractLimits.MaximumElapsedMilliseconds
            || !Enum.IsDefined(policy.BackoffStrategy) || policy.BackoffStrategy == GovernedLoopRetryBackoffStrategy.Unknown
            || !Enum.IsDefined(policy.JitterStrategy) || policy.JitterStrategy == GovernedLoopRetryJitterStrategy.Unknown
            || policy.InitialDelayMilliseconds is < 0 or > GovernedLoopRetryContractLimits.MaximumDelayMilliseconds
            || policy.MaximumDelayMilliseconds is < 0 or > GovernedLoopRetryContractLimits.MaximumDelayMilliseconds
            || policy.MaximumJitterMilliseconds is < 0 or > GovernedLoopRetryContractLimits.MaximumDelayMilliseconds
            || policy.BackoffStrategy == GovernedLoopRetryBackoffStrategy.None && (policy.InitialDelayMilliseconds != 0 || policy.MaximumDelayMilliseconds != 0)
            || policy.BackoffStrategy != GovernedLoopRetryBackoffStrategy.None && (policy.InitialDelayMilliseconds < 1 || policy.MaximumDelayMilliseconds < policy.InitialDelayMilliseconds)
            || policy.JitterStrategy == GovernedLoopRetryJitterStrategy.None && policy.MaximumJitterMilliseconds != 0
            || policy.JitterStrategy == GovernedLoopRetryJitterStrategy.DeterministicBounded && policy.MaximumJitterMilliseconds < 1
            || policy.MaximumTokens is not null and (< 1 or > GovernedLoopRetryContractLimits.MaximumTokens)
            || policy.MaximumToolCalls is not null and (< 1 or > GovernedLoopRetryContractLimits.MaximumToolCalls)
            || policy.MaximumCostMicrounits is not null and (< 1 or > GovernedLoopRetryContractLimits.MaximumCostMicrounits)
            || (policy.MaximumCostMicrounits is null) != (policy.MaximumCostCurrency is null)
            || policy.MaximumCostCurrency is not null && GovernedModelContractRules.RequireCurrency(policy.MaximumCostCurrency, nameof(policy)) != policy.MaximumCostCurrency
            || policy.MaximumResourceUnits is not null and (< 1 or > GovernedLoopRetryContractLimits.MaximumResourceUnits))
        {
            throw new ArgumentException("The retry policy is malformed, noncanonical, unbounded, or admits a failure that is not retry-safe.", nameof(policy));
        }
    }

    private static void RequireSeries(GovernedLoopRetrySeriesIdentity? identity)
    {
        if (identity is null
            || identity.SchemaVersion != GovernedLoopRetrySeriesIdentity.CurrentSchemaVersion
            || !IsHash(identity.SeriesId)
            || !ContextualRoleWorkspaceId.IsValid(identity.WorkspaceId)
            || !CustomLoopArtifactIdentifier.IsValid(identity.RunId)
            || identity.Revision is null
            || identity.Revision.SchemaVersion != GovernedLoopRevisionReference.CurrentSchemaVersion
            || !CustomLoopArtifactIdentifier.IsValid(identity.Revision.GraphId)
            || !CustomLoopArtifactIdentifier.IsValid(identity.Revision.RevisionId)
            || !IsHash(identity.Revision.ExecutableHash)
            || identity.ExecutionGeneration < 1
            || identity.ActivationOrdinal is < 0 or >= GovernedLoopExecutionLimits.MaxFrontierNodes
            || identity.VisitOrdinal is < 1 or > GovernedLoopExecutionLimits.MaxNodeVisits
            || !CustomLoopArtifactIdentifier.IsValid(identity.NodeId)
            || !CustomLoopArtifactIdentifier.IsValid(identity.OriginatingFailureEvidenceId)
            || !IsHash(identity.OriginatingFailureEvidenceHash)
            || !CustomLoopArtifactIdentifier.IsValid(identity.PolicyId)
            || !IsHash(identity.PolicyHash)
            || identity.StartedAtUtc.Offset != TimeSpan.Zero
            || identity.DeadlineUtc.Offset != TimeSpan.Zero
            || identity.DeadlineUtc <= identity.StartedAtUtc)
        {
            throw new ArgumentException("The retry-series identity is malformed or unbounded.", nameof(identity));
        }
    }

    private static void RequireState(GovernedLoopRetryState? state)
    {
        if (state is null || !IsValid(state.Identity)
            || state.SchemaVersion != GovernedLoopRetryState.CurrentSchemaVersion
            || state.StateVersion < 1
            || !Enum.IsDefined(state.Disposition) || state.Disposition == GovernedLoopRetryStateDisposition.Unknown
            || state.CurrentAttempt is < 1 or > GovernedLoopRetryContractLimits.MaximumAttempts
            || !CustomLoopArtifactIdentifier.IsValid(state.CurrentAttemptOperationId)
            || state.NextAttempt is not null && state.NextAttempt != state.CurrentAttempt + 1
            || (state.NextAttempt is null) != (state.AttemptOperationId is null)
            || state.AttemptOperationId is not null && !CustomLoopArtifactIdentifier.IsValid(state.AttemptOperationId)
            || state.Budget is null
            || state.Budget.Attempts != (state.Disposition is GovernedLoopRetryStateDisposition.Reserved or GovernedLoopRetryStateDisposition.Dispatched ? state.NextAttempt : state.CurrentAttempt)
            || state.Budget.Tokens is < 0 or > GovernedLoopRetryContractLimits.MaximumTokens
            || state.Budget.ToolCalls is < 0 or > GovernedLoopRetryContractLimits.MaximumToolCalls
            || state.Budget.CostMicrounits is < 0 or > GovernedLoopRetryContractLimits.MaximumCostMicrounits
            || (state.Budget.CostMicrounits is null) != (state.Budget.CostCurrency is null)
            || state.Budget.CostCurrency is not null && GovernedModelContractRules.RequireCurrency(state.Budget.CostCurrency, nameof(state)) != state.Budget.CostCurrency
            || state.Budget.ResourceUnits is < 0 or > GovernedLoopRetryContractLimits.MaximumResourceUnits
            || state.RecordedAtUtc.Offset != TimeSpan.Zero
            || state.RecordedAtUtc < state.Identity.StartedAtUtc
            || !CustomLoopArtifactIdentifier.IsValid(state.FailureEvidenceId)
            || !IsHash(state.FailureEvidenceHash)
            || (state.NextRetryAtUtc is null) != (state.Disposition != GovernedLoopRetryStateDisposition.Scheduled)
            || state.NextRetryAtUtc is { } nextRetryAtUtc && nextRetryAtUtc.Offset != TimeSpan.Zero
            || state.NextRetryAtUtc > state.Identity.DeadlineUtc
            || state.Disposition == GovernedLoopRetryStateDisposition.Scheduled && state.WakeCheckpointId is null && state.NextRetryAtUtc < state.RecordedAtUtc
            || (state.WakeCheckpointId is null) != (state.WakeCheckpointHash is null)
            || state.WakeCheckpointId is not null && (!IsHash(state.WakeCheckpointId) || !IsHash(state.WakeCheckpointHash!))
            || !HasDispositionShape(state))
        {
            throw new ArgumentException("The retry state is malformed, noncanonical, or internally inconsistent.", nameof(state));
        }
    }

    private static bool HasDispositionShape(GovernedLoopRetryState state)
        => state.Disposition switch
        {
            GovernedLoopRetryStateDisposition.FailureRetained => state.NextAttempt is null
                && state.AttemptOperationId is null
                && state.NextRetryAtUtc is null
                && state.WakeCheckpointId is null,
            GovernedLoopRetryStateDisposition.Scheduled => state.NextAttempt is not null
                && state.AttemptOperationId is not null
                && state.NextRetryAtUtc is not null,
            GovernedLoopRetryStateDisposition.Due or GovernedLoopRetryStateDisposition.Reserved or GovernedLoopRetryStateDisposition.Dispatched => state.NextAttempt is not null
                && state.AttemptOperationId is not null
                && state.NextRetryAtUtc is null
                && state.WakeCheckpointId is not null,
            GovernedLoopRetryStateDisposition.AttemptCompleted or GovernedLoopRetryStateDisposition.Exhausted or GovernedLoopRetryStateDisposition.Stopped or GovernedLoopRetryStateDisposition.NeedsReview => state.NextAttempt is null
                && state.AttemptOperationId is null
                && state.NextRetryAtUtc is null
                && state.WakeCheckpointId is null,
            _ => false,
        };

    private static long ExponentialDelay(GovernedLoopRetryPolicy policy, int nextAttempt)
    {
        var value = policy.InitialDelayMilliseconds;
        for (var ordinal = 2; ordinal < nextAttempt && value < policy.MaximumDelayMilliseconds; ordinal++)
        {
            value = value > policy.MaximumDelayMilliseconds / 2 ? policy.MaximumDelayMilliseconds : value * 2;
        }
        return Math.Min(value, policy.MaximumDelayMilliseconds);
    }

    private static bool IsHash(string? value)
        => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void RequireHash(string? value, string parameterName)
    {
        if (!IsHash(value)) throw new ArgumentException("A lowercase SHA-256 digest is required.", parameterName);
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
        => value.Offset == TimeSpan.Zero ? value : throw new ArgumentException("A UTC timestamp is required.", parameterName);

    private static DateTimeOffset AddMilliseconds(DateTimeOffset value, long milliseconds, string parameterName)
    {
        try { return value.AddMilliseconds(milliseconds); }
        catch (ArgumentOutOfRangeException exception) { throw new ArgumentException("Retry deadline arithmetic overflowed.", parameterName, exception); }
    }

    private static DateTimeOffset Earlier(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    private static bool Decreased(long? current, long? next)
        => current.HasValue && (!next.HasValue || next.Value < current.Value);

    private static bool Decreased(int? current, int? next)
        => current.HasValue && (!next.HasValue || next.Value < current.Value);

    private static bool ScheduledCheckpointAttachment(GovernedLoopRetryState current, GovernedLoopRetryState next)
        => current.WakeCheckpointId is null
            && current.WakeCheckpointHash is null
            && next.WakeCheckpointId is not null
            && next.WakeCheckpointHash is not null
            && current.CurrentAttempt == next.CurrentAttempt
            && current.NextAttempt == next.NextAttempt
            && string.Equals(current.AttemptOperationId, next.AttemptOperationId, StringComparison.Ordinal)
            && Equals(current.Budget, next.Budget)
            && current.NextRetryAtUtc == next.NextRetryAtUtc
            && string.Equals(current.FailureEvidenceId, next.FailureEvidenceId, StringComparison.Ordinal)
            && string.Equals(current.FailureEvidenceHash, next.FailureEvidenceHash, StringComparison.Ordinal);

    private static bool PreservesFailureEvidence(GovernedLoopRetryState current, GovernedLoopRetryState next)
        => next.Disposition == GovernedLoopRetryStateDisposition.AttemptCompleted
            || string.Equals(current.FailureEvidenceId, next.FailureEvidenceId, StringComparison.Ordinal)
                && string.Equals(current.FailureEvidenceHash, next.FailureEvidenceHash, StringComparison.Ordinal);

    private static bool PreservesAttemptReservation(GovernedLoopRetryState current, GovernedLoopRetryState next)
        => current.NextAttempt is null
            || next.NextAttempt is null
            || current.NextAttempt == next.NextAttempt
                && string.Equals(current.AttemptOperationId, next.AttemptOperationId, StringComparison.Ordinal);

    private static bool PreservesWakeCheckpoint(GovernedLoopRetryState current, GovernedLoopRetryState next)
        => current.WakeCheckpointId is null
            || next.WakeCheckpointId is null
            || string.Equals(current.WakeCheckpointId, next.WakeCheckpointId, StringComparison.Ordinal)
                && string.Equals(current.WakeCheckpointHash, next.WakeCheckpointHash, StringComparison.Ordinal);

    private static bool RequiresExactBudget(GovernedLoopRetryState current, GovernedLoopRetryState next)
        => current.Disposition is GovernedLoopRetryStateDisposition.FailureRetained or GovernedLoopRetryStateDisposition.AttemptCompleted
            || current.Disposition == GovernedLoopRetryStateDisposition.Reserved
                && next.Disposition == GovernedLoopRetryStateDisposition.Dispatched;

    private static string Token<T>(T value) where T : struct, Enum
        => string.Concat(value.ToString().Select((character, index) => char.IsUpper(character) && index > 0 ? $"-{char.ToLowerInvariant(character)}" : char.ToLowerInvariant(character).ToString()));

    private static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, long? value)
    {
        if (value.HasValue) writer.WriteNumber(name, value.Value); else writer.WriteNull(name);
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, int? value)
    {
        if (value.HasValue) writer.WriteNumber(name, value.Value); else writer.WriteNull(name);
    }

    private static void WriteRevision(Utf8JsonWriter writer, EmbodySense.Core.Common.Loops.Models.Custom.Graph.GovernedLoopRevisionReference revision)
    {
        writer.WritePropertyName("revision");
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", revision.SchemaVersion);
        writer.WriteString("graphId", revision.GraphId);
        writer.WriteString("revisionId", revision.RevisionId);
        writer.WriteString("executableHash", revision.ExecutableHash);
        writer.WriteEndObject();
    }
}
