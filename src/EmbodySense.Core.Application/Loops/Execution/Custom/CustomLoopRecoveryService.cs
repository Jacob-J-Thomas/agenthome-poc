using System.Text;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public sealed class CustomLoopRecoveryService
{
    private static readonly TimeSpan IntegrityWriteTimeout = TimeSpan.FromSeconds(30);

    private readonly ICustomLoopRunStore _runStore;
    private readonly IAuditLog _auditLog;
    private readonly TimeProvider _timeProvider;

    public CustomLoopRecoveryService(ICustomLoopRunStore runStore, IAuditLog auditLog, TimeProvider? timeProvider = null)
    {
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<CustomLoopRecoveryResult>> RecoverAsync(string actor, CancellationToken cancellationToken = default)
    {
        ValidateActor(actor);
        var runs = await _runStore.ListNonterminalAsync(cancellationToken);
        var results = new List<CustomLoopRecoveryResult>(runs.Count);
        foreach (var run in runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RecoverOneAsync(run, actor, cancellationToken));
        }

        return results;
    }

    private async Task<CustomLoopRecoveryResult> RecoverOneAsync(CustomLoopRunRecord run, string actor, CancellationToken cancellationToken)
    {
        var validation = CustomLoopRunValidator.Validate(run);
        if (!validation.IsValid)
        {
            return Result(CustomLoopRecoveryStatus.Failed, run, "The persisted custom-loop run is invalid and recovery did not mutate it.");
        }

        var admissionAuditComplete = CustomLoopRunValidator.HasCompleteAdmissionAudit(run);
        if (run.Status == CustomLoopRunStatus.Paused && admissionAuditComplete)
        {
            return Result(CustomLoopRecoveryStatus.Unchanged, run, "The run is already Paused; restart recovery never starts execution automatically.");
        }

        var hasOpenAttempt = HasOpenAttemptSinceCheckpoint(run);
        var target = !admissionAuditComplete
            ? CustomLoopRunStatus.NeedsReview
            : run.Status switch
        {
            CustomLoopRunStatus.Admitted => CustomLoopRunStatus.Paused,
            CustomLoopRunStatus.Running or CustomLoopRunStatus.PauseRequested when hasOpenAttempt => CustomLoopRunStatus.NeedsReview,
            CustomLoopRunStatus.Running or CustomLoopRunStatus.PauseRequested => CustomLoopRunStatus.Paused,
            CustomLoopRunStatus.CancelRequested when hasOpenAttempt => CustomLoopRunStatus.NeedsReview,
            CustomLoopRunStatus.CancelRequested => CustomLoopRunStatus.Cancelled,
            _ => CustomLoopRunStatus.Unknown
        };

        if (target == CustomLoopRunStatus.Unknown)
        {
            return Result(CustomLoopRecoveryStatus.Failed, run, $"Recovery does not recognize nonterminal state {run.Status}; no mutation was attempted.");
        }

        var detail = (run.Status, target) switch
        {
            (_, CustomLoopRunStatus.NeedsReview) when !admissionAuditComplete => "Restart recovery found no valid durable admission-audit completion marker; execution is permanently stopped for review.",
            (CustomLoopRunStatus.Admitted, CustomLoopRunStatus.Paused) => "Restart recovery parked the admitted run at Paused without dispatch.",
            (_, CustomLoopRunStatus.NeedsReview) => "Restart recovery found provider-attempt evidence after the last committed checkpoint; execution remains stopped for review.",
            (CustomLoopRunStatus.CancelRequested, CustomLoopRunStatus.Cancelled) => "Restart recovery proved there was no open attempt after the checkpoint and completed cancellation without dispatch.",
            _ => "Restart recovery parked the interrupted run at its last proved checkpoint without dispatch."
        };
        var now = Now(run);
        var failureCode = !admissionAuditComplete ? "recovery_incomplete_admission_audit" : target == CustomLoopRunStatus.NeedsReview ? "recovery_open_attempt" : null;
        var candidate = CreateCandidate(run, target, failureCode, detail, now);
        var metadata = RecoveryMetadata(run, candidate, hasOpenAttempt, admissionAuditComplete);

        try
        {
            await _auditLog.AppendAsync(
                AuditEvent.Create(
                    actor,
                    AuditSchema.Actions.LoopRunLifecycle,
                    run.Id,
                    AuditSchema.Outcomes.Requested,
                    $"Restart recovery durably recorded its intent to transition {run.Status} to {target} before mutating the run.",
                    metadata),
                IntegrityToken());
        }
        catch (Exception exception)
        {
            return Result(CustomLoopRecoveryStatus.Failed, run, $"The recovery intent audit failed before lifecycle mutation: {SafeExceptionClass(exception)}.");
        }

        CustomLoopRunStoreResult stored;
        try
        {
            stored = await _runStore.UpdateAsync(candidate, run.LifecycleVersion, IntegrityToken());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result(CustomLoopRecoveryStatus.Failed, run, $"The recovery transition failed: {SafeExceptionClass(exception)}.");
        }

        if (stored.Status is CustomLoopRunStoreStatus.Conflict or CustomLoopRunStoreStatus.TerminalImmutable)
        {
            var latest = await TryLoadAsync(run.Id);
            return Result(CustomLoopRecoveryStatus.Conflict, latest ?? run, "The run changed concurrently; restart recovery did not retry or start execution.");
        }

        if (stored.Status != CustomLoopRunStoreStatus.Updated || stored.Run is null)
        {
            return Result(CustomLoopRecoveryStatus.Failed, run, "The recovery transition was rejected; restart recovery did not start execution.");
        }

        var recovered = stored.Run;
        var outcome = target == CustomLoopRunStatus.NeedsReview ? AuditSchema.Outcomes.NeedsReview : AuditSchema.Outcomes.Succeeded;
        try
        {
            await _auditLog.AppendAsync(AuditEvent.Create(actor, AuditSchema.Actions.LoopRunLifecycle, recovered.Id, outcome, detail, metadata), IntegrityToken());
        }
        catch (Exception exception)
        {
            return Result(CustomLoopRecoveryStatus.Failed, recovered, $"The recovery transition is durable, but its lifecycle audit failed: {SafeExceptionClass(exception)}.");
        }

        var status = target switch
        {
            CustomLoopRunStatus.Paused => CustomLoopRecoveryStatus.Paused,
            CustomLoopRunStatus.Cancelled => CustomLoopRecoveryStatus.Cancelled,
            CustomLoopRunStatus.NeedsReview => CustomLoopRecoveryStatus.NeedsReview,
            _ => CustomLoopRecoveryStatus.Failed
        };
        return Result(status, recovered, detail);
    }

    private static Dictionary<string, object?> RecoveryMetadata(CustomLoopRunRecord current, CustomLoopRunRecord candidate, bool hasOpenAttempt, bool admissionAuditComplete)
    {
        return new Dictionary<string, object?>
        {
            ["runId"] = current.Id,
            ["loopId"] = current.LoopId,
            ["definitionVersion"] = current.AdmittedDefinition.DefinitionVersion,
            ["definitionHash"] = current.AdmittedDefinition.ContentHash,
            ["recovery"] = true,
            ["previousStatus"] = current.Status.ToString().ToLowerInvariant(),
            ["runStatus"] = candidate.Status.ToString().ToLowerInvariant(),
            ["previousLifecycleVersion"] = current.LifecycleVersion,
            ["lifecycleVersion"] = candidate.LifecycleVersion,
            ["recoveryEventId"] = candidate.Events[^1].EventId,
            ["openAttemptAfterCheckpoint"] = hasOpenAttempt,
            ["admissionAuditComplete"] = admissionAuditComplete,
            ["automaticExecution"] = false
        };
    }

    private static CustomLoopRunRecord CreateCandidate(CustomLoopRunRecord run, CustomLoopRunStatus status, string? failureCode, string detail, DateTimeOffset now)
    {
        var terminal = status is CustomLoopRunStatus.Cancelled or CustomLoopRunStatus.NeedsReview;
        var lifecycle = new CustomLoopRunEvent(run.Events.Length + 1, $"recovery-{Guid.NewGuid():N}", now, CustomLoopRunEventKind.LifecycleChanged, null, null, null, detail, [], null, null, null, null, null, null, null, null, null, null);
        return run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            Status = status,
            UpdatedAtUtc = now,
            CompletedAtUtc = terminal ? now : null,
            ExecutionClock = StopAtLastDurableUpdate(run.ExecutionClock, run.UpdatedAtUtc),
            Events = [.. run.Events, lifecycle],
            FinalOutput = null,
            FailureCode = status == CustomLoopRunStatus.NeedsReview ? failureCode : null,
            FailureDetail = status == CustomLoopRunStatus.NeedsReview ? detail : null
        };
    }

    private static bool HasOpenAttemptSinceCheckpoint(CustomLoopRunRecord run)
    {
        return run.Events.Any(item => item.Sequence > run.Checkpoint.LastCommittedSequence && item.Kind is CustomLoopRunEventKind.NodeAttemptStarted or CustomLoopRunEventKind.ExitDecisionStarted);
    }

    private static CustomLoopExecutionClock StopAtLastDurableUpdate(CustomLoopExecutionClock clock, DateTimeOffset durableStop)
    {
        var accumulated = clock.AccumulatedRunningMilliseconds;
        if (clock.ActiveSinceUtc is { } activeSince)
        {
            accumulated = checked(accumulated + Math.Max(0, (long)(durableStop - activeSince).TotalMilliseconds));
        }

        return new CustomLoopExecutionClock(Math.Min(accumulated, CustomLoopLimits.MaxRunExecutionMilliseconds), null);
    }

    private async Task<CustomLoopRunRecord?> TryLoadAsync(string runId)
    {
        try
        {
            return await _runStore.GetAsync(runId, IntegrityToken());
        }
        catch
        {
            return null;
        }
    }

    private DateTimeOffset Now(CustomLoopRunRecord run)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        return now < run.UpdatedAtUtc ? run.UpdatedAtUtc : now;
    }

    private static void ValidateActor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > CustomLoopLimits.MaxTraceReferenceCharacters || !actor.IsNormalized(NormalizationForm.FormC) || actor.Any(character => char.IsControl(character) || char.IsSurrogate(character)))
        {
            throw new ArgumentException("Actor must be bounded normalized text without control or invalid surrogate characters.", nameof(actor));
        }
    }

    private static CancellationToken IntegrityToken()
    {
        return new CancellationTokenSource(IntegrityWriteTimeout).Token;
    }

    private static string SafeExceptionClass(Exception exception)
    {
        return exception.GetType().Name;
    }

    private static CustomLoopRecoveryResult Result(CustomLoopRecoveryStatus status, CustomLoopRunRecord run, string detail)
    {
        return new CustomLoopRecoveryResult(status, run, detail);
    }
}
