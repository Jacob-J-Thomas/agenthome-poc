using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Execution.Custom;
using EmbodySense.Core.Application.Loops.TraceRetention;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed class CustomLoopRunToolEvidenceSink : ICustomLoopToolEvidenceSink
{
    private static readonly TimeSpan IntegrityWriteTimeout = TimeSpan.FromSeconds(30);
    private readonly ICustomLoopRunStore _runStore;
    private readonly TimeProvider _timeProvider;

    public CustomLoopRunToolEvidenceSink(ICustomLoopRunStore runStore, TimeProvider? timeProvider = null)
    {
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task RecordAsync(string runId, int iteration, string stepId, int attempt, CustomLoopToolTraceEvidence evidence, CancellationToken cancellationToken = default)
    {
        CustomLoopArtifactIdentifier.Require(runId, nameof(runId));
        CustomLoopArtifactIdentifier.Require(stepId, nameof(stepId));
        ArgumentNullException.ThrowIfNull(evidence);
        if (iteration < 1 || attempt < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(iteration), "Tool evidence coordinates must be positive.");
        }

        using var integrityWindow = evidence.Phase is CustomLoopToolEvidencePhase.OutcomeObserved or CustomLoopToolEvidencePhase.IntegrityFailed
            ? new CancellationTokenSource()
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        integrityWindow.CancelAfter(IntegrityWriteTimeout);
        for (var retry = 0; retry < 12; retry++)
        {
            CustomLoopRunRecord? run;
            try
            {
                run = await _runStore.GetAsync(runId, integrityWindow.Token);
            }
            catch (Exception exception) when (exception is not CustomLoopToolEvidenceIntegrityException)
            {
                throw new CustomLoopToolEvidenceIntegrityException("The custom-loop run trace could not be loaded for mandatory tool evidence.", exception);
            }

            if (run is null || run.IsTerminal)
            {
                throw new CustomLoopToolEvidenceIntegrityException("Mandatory tool evidence cannot be appended because the run is missing or terminal.");
            }

            var now = _timeProvider.GetUtcNow().ToUniversalTime();
            if (now < run.UpdatedAtUtc)
            {
                now = run.UpdatedAtUtc;
            }

            var kind = evidence.Phase switch
            {
                CustomLoopToolEvidencePhase.RequestReserved => CustomLoopRunEventKind.ToolRequestReserved,
                CustomLoopToolEvidencePhase.GovernanceDecided => CustomLoopRunEventKind.ToolGovernanceDecided,
                CustomLoopToolEvidencePhase.OutcomeObserved => CustomLoopRunEventKind.ToolOutcomeObserved,
                CustomLoopToolEvidencePhase.IntegrityFailed => CustomLoopRunEventKind.ToolIntegrityFailed,
                _ => throw new CustomLoopToolEvidenceIntegrityException("Unsupported mandatory tool-evidence phase.")
            };
            var detail = evidence.Phase switch
            {
                CustomLoopToolEvidencePhase.RequestReserved => "Exact tool request retained and worst-case result evidence reserved before governance or actuation.",
                CustomLoopToolEvidencePhase.GovernanceDecided => "Current authority, permission, and approval posture retained before any permitted actuator call.",
                CustomLoopToolEvidencePhase.OutcomeObserved when evidence.ReturnedToModel => "Exact canonical governed tool result retained before returning it to the model.",
                CustomLoopToolEvidencePhase.OutcomeObserved => "Exact governed tool outcome retained before post-outcome audit completion.",
                _ => "Exact repeated tool request retained as a bounded non-actuating integrity failure; no automatic retry is permitted."
            };
            var traceEvent = new CustomLoopRunEvent(
                run.Events.Length + 1,
                "event-" + Guid.NewGuid().ToString("N"),
                now,
                kind,
                iteration,
                stepId,
                attempt,
                detail,
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
                evidence.Authority,
                evidence);
            var candidate = run with
            {
                LifecycleVersion = run.LifecycleVersion + 1,
                UpdatedAtUtc = now,
                Events = [.. run.Events, traceEvent]
            };
            var validation = CustomLoopRunValidator.ValidateUpdate(run, candidate);
            if (!validation.IsValid)
            {
                throw new CustomLoopToolEvidenceIntegrityException("Mandatory tool evidence did not form a valid append-only run-trace successor.");
            }

            CustomLoopRunStoreResult stored;
            try
            {
                stored = await _runStore.UpdateAsync(candidate, run.LifecycleVersion, integrityWindow.Token);
            }
            catch (Exception exception) when (exception is not CustomLoopToolEvidenceIntegrityException)
            {
                throw new CustomLoopToolEvidenceIntegrityException("The run store could not atomically reserve or consume mandatory tool-evidence capacity.", exception);
            }
            if (stored.Status == CustomLoopRunStoreStatus.Updated)
            {
                return;
            }

            if (stored.Status != CustomLoopRunStoreStatus.Conflict)
            {
                throw new CustomLoopToolEvidenceIntegrityException($"The run store rejected mandatory tool evidence with status `{stored.Status}`.");
            }
        }

        throw new CustomLoopToolEvidenceIntegrityException("Mandatory tool evidence lost its durable compare-and-swap race repeatedly; no actuator result was returned to the model.");
    }
}
