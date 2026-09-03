using AppModels = EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;
using CommonModels = EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;
using SurfaceModels = EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;

namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation;

internal static class GovernedLoopEffectReconciliationProjectionMapper
{
    internal static SurfaceModels.GovernedLoopEffectReconciliationCaseReference Reference(AppModels.GovernedLoopEffectReconciliationCaseReference value)
        => new(value.CaseId, value.CaseVersion, value.ContentHash, value.BindingHash);

    internal static AppModels.GovernedLoopEffectReconciliationCaseReference Reference(SurfaceModels.GovernedLoopEffectReconciliationCaseReference value)
        => new(value.CaseId, value.CaseVersion, value.ContentHash, value.BindingHash);

    internal static SurfaceModels.GovernedLoopEffectReconciliationCaseSummary Summary(AppModels.GovernedLoopEffectReconciliationCaseSummary value)
        => new(new SurfaceModels.GovernedLoopEffectReconciliationCaseReference(value.CaseId, value.CaseVersion, value.ContentHash, value.BindingHash), Posture(value.Status));

    internal static SurfaceModels.GovernedLoopEffectReconciliationCaseDetail Detail(CommonModels.GovernedLoopEffectReconciliationCase value)
    {
        if (!GovernedLoopEffectReconciliationContract.Validate(value).IsValid)
        {
            throw new ArgumentException("The canonical reconciliation case is invalid.", nameof(value));
        }

        return new SurfaceModels.GovernedLoopEffectReconciliationCaseDetail(
            new SurfaceModels.GovernedLoopEffectReconciliationCaseReference(value.CaseId, value.CaseVersion, value.ContentHash, value.Binding.ContentHash),
            Posture(value),
            Contract(value.ContractMetadata),
            value.EvidenceSources.Select(Source).ToArray(),
            value.ObservationHistory.Select(Observation).ToArray(),
            value.AssessmentHistory.Select(Assessment).ToArray(),
            value.Disposition is null ? null : Disposition(value.Disposition),
            value.Resolution is null ? null : Resolution(value.Resolution),
            value.CaseReceiptHashes,
            value.OpenedAtUtc,
            value.UpdatedAtUtc);
    }

    internal static SurfaceModels.GovernedLoopEffectReconciliationContractProjection Contract(CommonModels.GovernedLoopEffectReconciliationContractMetadata value)
        => new(value.ContractId, value.ContractVersion, value.ContentHash, value.ProbeContractId, value.ProbeContractVersion, value.ProbeContractHash);

    internal static SurfaceModels.GovernedLoopEffectReconciliationResolutionProjection Resolution(CommonModels.GovernedLoopEffectReconciliationResolution value)
        => new(value.ResolutionId, value.AssessmentHash, value.DispositionHash, ResolutionOutcome(value.Outcome), value.OutcomeEvidenceId, value.OutcomeEvidenceHash, value.ResolvedAtUtc, value.ContentHash);

    internal static SurfaceModels.GovernedLoopEffectReconciliationOperationStatus OperationStatus(AppModels.GovernedLoopEffectReconciliationOperationStatus value)
        => value switch
        {
            AppModels.GovernedLoopEffectReconciliationOperationStatus.Applied => SurfaceModels.GovernedLoopEffectReconciliationOperationStatus.Applied,
            AppModels.GovernedLoopEffectReconciliationOperationStatus.Replayed => SurfaceModels.GovernedLoopEffectReconciliationOperationStatus.Replayed,
            AppModels.GovernedLoopEffectReconciliationOperationStatus.Found => SurfaceModels.GovernedLoopEffectReconciliationOperationStatus.Found,
            AppModels.GovernedLoopEffectReconciliationOperationStatus.NotFound => SurfaceModels.GovernedLoopEffectReconciliationOperationStatus.NotFound,
            AppModels.GovernedLoopEffectReconciliationOperationStatus.Denied => SurfaceModels.GovernedLoopEffectReconciliationOperationStatus.Denied,
            AppModels.GovernedLoopEffectReconciliationOperationStatus.Conflict => SurfaceModels.GovernedLoopEffectReconciliationOperationStatus.Conflict,
            AppModels.GovernedLoopEffectReconciliationOperationStatus.Invalid => SurfaceModels.GovernedLoopEffectReconciliationOperationStatus.Invalid,
            AppModels.GovernedLoopEffectReconciliationOperationStatus.Corrupt => SurfaceModels.GovernedLoopEffectReconciliationOperationStatus.Corrupt,
            AppModels.GovernedLoopEffectReconciliationOperationStatus.CapacityExceeded => SurfaceModels.GovernedLoopEffectReconciliationOperationStatus.CapacityExceeded,
            AppModels.GovernedLoopEffectReconciliationOperationStatus.RepairRequired => SurfaceModels.GovernedLoopEffectReconciliationOperationStatus.RepairRequired,
            _ => SurfaceModels.GovernedLoopEffectReconciliationOperationStatus.Unavailable,
        };

    internal static SurfaceModels.GovernedLoopEffectReconciliationDispositionKind DispositionKind(CommonModels.GovernedLoopEffectReconciliationDispositionKind value)
        => value switch
        {
            CommonModels.GovernedLoopEffectReconciliationDispositionKind.AcceptProvedNotApplied => SurfaceModels.GovernedLoopEffectReconciliationDispositionKind.AcceptProvedNotApplied,
            CommonModels.GovernedLoopEffectReconciliationDispositionKind.AcceptProvedApplied => SurfaceModels.GovernedLoopEffectReconciliationDispositionKind.AcceptProvedApplied,
            CommonModels.GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved => SurfaceModels.GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved,
            _ => SurfaceModels.GovernedLoopEffectReconciliationDispositionKind.Unknown,
        };

    internal static CommonModels.GovernedLoopEffectReconciliationDispositionKind DispositionKind(SurfaceModels.GovernedLoopEffectReconciliationDispositionKind value)
        => value switch
        {
            SurfaceModels.GovernedLoopEffectReconciliationDispositionKind.AcceptProvedNotApplied => CommonModels.GovernedLoopEffectReconciliationDispositionKind.AcceptProvedNotApplied,
            SurfaceModels.GovernedLoopEffectReconciliationDispositionKind.AcceptProvedApplied => CommonModels.GovernedLoopEffectReconciliationDispositionKind.AcceptProvedApplied,
            SurfaceModels.GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved => CommonModels.GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved,
            _ => CommonModels.GovernedLoopEffectReconciliationDispositionKind.Unknown,
        };

    private static SurfaceModels.GovernedLoopEffectReconciliationEvidenceSourceProjection Source(CommonModels.GovernedLoopEffectReconciliationEvidenceSource value)
        => new(value.SourceId, SourceKind(value.Kind), ReliabilityPosture(value.ReliabilityPosture), value.ReconciliationContractHash, value.RegisteredAtUtc, value.RetiredAtUtc, value.ContentHash);

    private static SurfaceModels.GovernedLoopEffectReconciliationObservationProjection Observation(CommonModels.GovernedLoopEffectReconciliationObservation value)
        => new(value.ObservationId, value.SourceId, value.SourceRegistrationHash, ObservationKind(value.Kind), ReliabilityPosture(value.ReliabilityPosture), ObservedOutcome(value.ObservedOutcome), value.EvidenceReference, value.EvidenceHash, value.ObservedAtUtc, value.RecordedAtUtc, value.ContentHash);

    private static SurfaceModels.GovernedLoopEffectReconciliationAssessmentProjection Assessment(CommonModels.GovernedLoopEffectReconciliationAssessment value)
        => new(value.AssessmentId, AssessmentKind(value.Kind), value.ObservationHashes, value.AssessedAtUtc, value.ContentHash);

    private static SurfaceModels.GovernedLoopEffectReconciliationDispositionProjection Disposition(CommonModels.GovernedLoopEffectReconciliationDisposition value)
        => new(value.DispositionId, DispositionKind(value.Kind), value.AssessmentHash, value.DisposedAtUtc, value.ContentHash);

    private static SurfaceModels.GovernedLoopEffectReconciliationCasePosture Posture(AppModels.GovernedLoopEffectReconciliationCaseSummaryStatus value)
        => value switch
        {
            AppModels.GovernedLoopEffectReconciliationCaseSummaryStatus.Open => SurfaceModels.GovernedLoopEffectReconciliationCasePosture.Open,
            AppModels.GovernedLoopEffectReconciliationCaseSummaryStatus.Assessed => SurfaceModels.GovernedLoopEffectReconciliationCasePosture.Assessed,
            AppModels.GovernedLoopEffectReconciliationCaseSummaryStatus.Accepted => SurfaceModels.GovernedLoopEffectReconciliationCasePosture.Accepted,
            AppModels.GovernedLoopEffectReconciliationCaseSummaryStatus.Quarantined => SurfaceModels.GovernedLoopEffectReconciliationCasePosture.Quarantined,
            AppModels.GovernedLoopEffectReconciliationCaseSummaryStatus.Resolved => SurfaceModels.GovernedLoopEffectReconciliationCasePosture.Resolved,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static SurfaceModels.GovernedLoopEffectReconciliationCasePosture Posture(CommonModels.GovernedLoopEffectReconciliationCase value)
        => value.Resolution is not null
            ? SurfaceModels.GovernedLoopEffectReconciliationCasePosture.Resolved
            : value.Disposition?.Kind == CommonModels.GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved
                ? SurfaceModels.GovernedLoopEffectReconciliationCasePosture.Quarantined
                : value.Disposition is not null
                    ? SurfaceModels.GovernedLoopEffectReconciliationCasePosture.Accepted
                    : value.CurrentAssessmentHash is not null
                        ? SurfaceModels.GovernedLoopEffectReconciliationCasePosture.Assessed
                        : SurfaceModels.GovernedLoopEffectReconciliationCasePosture.Open;

    private static SurfaceModels.GovernedLoopEffectReconciliationEvidenceSourceKind SourceKind(CommonModels.GovernedLoopEffectReconciliationEvidenceSourceKind value)
        => value switch
        {
            CommonModels.GovernedLoopEffectReconciliationEvidenceSourceKind.Authoritative => SurfaceModels.GovernedLoopEffectReconciliationEvidenceSourceKind.Authoritative,
            CommonModels.GovernedLoopEffectReconciliationEvidenceSourceKind.Informational => SurfaceModels.GovernedLoopEffectReconciliationEvidenceSourceKind.Informational,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static SurfaceModels.GovernedLoopEffectReconciliationReliabilityPosture ReliabilityPosture(CommonModels.GovernedLoopEffectReconciliationReliabilityPosture value)
        => value switch
        {
            CommonModels.GovernedLoopEffectReconciliationReliabilityPosture.Authoritative => SurfaceModels.GovernedLoopEffectReconciliationReliabilityPosture.Authoritative,
            CommonModels.GovernedLoopEffectReconciliationReliabilityPosture.Corroborating => SurfaceModels.GovernedLoopEffectReconciliationReliabilityPosture.Corroborating,
            CommonModels.GovernedLoopEffectReconciliationReliabilityPosture.Untrusted => SurfaceModels.GovernedLoopEffectReconciliationReliabilityPosture.Untrusted,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static SurfaceModels.GovernedLoopEffectReconciliationObservationKind ObservationKind(CommonModels.GovernedLoopEffectReconciliationObservationKind value)
        => value switch
        {
            CommonModels.GovernedLoopEffectReconciliationObservationKind.Evidence => SurfaceModels.GovernedLoopEffectReconciliationObservationKind.Evidence,
            CommonModels.GovernedLoopEffectReconciliationObservationKind.Missing => SurfaceModels.GovernedLoopEffectReconciliationObservationKind.Missing,
            CommonModels.GovernedLoopEffectReconciliationObservationKind.TimedOut => SurfaceModels.GovernedLoopEffectReconciliationObservationKind.TimedOut,
            CommonModels.GovernedLoopEffectReconciliationObservationKind.Cancelled => SurfaceModels.GovernedLoopEffectReconciliationObservationKind.Cancelled,
            CommonModels.GovernedLoopEffectReconciliationObservationKind.Prose => SurfaceModels.GovernedLoopEffectReconciliationObservationKind.Prose,
            CommonModels.GovernedLoopEffectReconciliationObservationKind.CallerAssertion => SurfaceModels.GovernedLoopEffectReconciliationObservationKind.CallerAssertion,
            CommonModels.GovernedLoopEffectReconciliationObservationKind.UnprovenHash => SurfaceModels.GovernedLoopEffectReconciliationObservationKind.UnprovenHash,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static SurfaceModels.GovernedLoopEffectReconciliationObservedOutcome ObservedOutcome(CommonModels.GovernedLoopEffectReconciliationObservedOutcome value)
        => value switch
        {
            CommonModels.GovernedLoopEffectReconciliationObservedOutcome.Unknown => SurfaceModels.GovernedLoopEffectReconciliationObservedOutcome.Unknown,
            CommonModels.GovernedLoopEffectReconciliationObservedOutcome.NotApplied => SurfaceModels.GovernedLoopEffectReconciliationObservedOutcome.NotApplied,
            CommonModels.GovernedLoopEffectReconciliationObservedOutcome.AppliedSucceeded => SurfaceModels.GovernedLoopEffectReconciliationObservedOutcome.AppliedSucceeded,
            CommonModels.GovernedLoopEffectReconciliationObservedOutcome.AppliedFailed => SurfaceModels.GovernedLoopEffectReconciliationObservedOutcome.AppliedFailed,
            CommonModels.GovernedLoopEffectReconciliationObservedOutcome.AppliedOutcomeUnknown => SurfaceModels.GovernedLoopEffectReconciliationObservedOutcome.AppliedOutcomeUnknown,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static SurfaceModels.GovernedLoopEffectReconciliationAssessmentKind AssessmentKind(CommonModels.GovernedLoopEffectReconciliationAssessmentKind value)
        => value switch
        {
            CommonModels.GovernedLoopEffectReconciliationAssessmentKind.Inconclusive => SurfaceModels.GovernedLoopEffectReconciliationAssessmentKind.Inconclusive,
            CommonModels.GovernedLoopEffectReconciliationAssessmentKind.Conflicting => SurfaceModels.GovernedLoopEffectReconciliationAssessmentKind.Conflicting,
            CommonModels.GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied => SurfaceModels.GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied,
            CommonModels.GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedSucceeded => SurfaceModels.GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedSucceeded,
            CommonModels.GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedFailed => SurfaceModels.GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedFailed,
            CommonModels.GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedOutcomeUnknown => SurfaceModels.GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedOutcomeUnknown,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private static SurfaceModels.GovernedLoopEffectReconciliationResolutionOutcome ResolutionOutcome(EmbodySense.Core.Common.Loops.Execution.Models.GovernedLoopEffectOutcome value)
        => value switch
        {
            EmbodySense.Core.Common.Loops.Execution.Models.GovernedLoopEffectOutcome.NotApplied => SurfaceModels.GovernedLoopEffectReconciliationResolutionOutcome.NotApplied,
            EmbodySense.Core.Common.Loops.Execution.Models.GovernedLoopEffectOutcome.Succeeded => SurfaceModels.GovernedLoopEffectReconciliationResolutionOutcome.Succeeded,
            EmbodySense.Core.Common.Loops.Execution.Models.GovernedLoopEffectOutcome.Failed => SurfaceModels.GovernedLoopEffectReconciliationResolutionOutcome.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
}
