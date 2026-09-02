using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation;

/// <summary>Defines the closed schema-1 assessment, disposition, resolution, and successor mappings.</summary>
public static class GovernedLoopEffectReconciliationStateMatrix
{
    /// <summary>Gets whether an assessment kind is supported.</summary>
    public static bool IsSupported(GovernedLoopEffectReconciliationAssessmentKind kind)
        => kind != GovernedLoopEffectReconciliationAssessmentKind.Unknown && Enum.IsDefined(kind);

    /// <summary>Gets whether a disposition kind is supported.</summary>
    public static bool IsSupported(GovernedLoopEffectReconciliationDispositionKind kind)
        => kind != GovernedLoopEffectReconciliationDispositionKind.Unknown && Enum.IsDefined(kind);

    /// <summary>Gets whether an observed outcome is supported.</summary>
    public static bool IsSupported(GovernedLoopEffectReconciliationObservedOutcome outcome)
        => outcome != GovernedLoopEffectReconciliationObservedOutcome.Unknown && Enum.IsDefined(outcome);

    /// <summary>Determines whether one disposition is legal for the exact current assessment.</summary>
    public static bool IsDispositionAllowed(GovernedLoopEffectReconciliationAssessmentKind assessment, GovernedLoopEffectReconciliationDispositionKind disposition)
        => assessment switch
        {
            GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied => disposition == GovernedLoopEffectReconciliationDispositionKind.AcceptProvedNotApplied,
            GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedSucceeded or GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedFailed => disposition == GovernedLoopEffectReconciliationDispositionKind.AcceptProvedApplied,
            GovernedLoopEffectReconciliationAssessmentKind.Inconclusive or GovernedLoopEffectReconciliationAssessmentKind.Conflicting or GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedOutcomeUnknown => disposition == GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved,
            _ => false
        };

    /// <summary>Gets the sole typed successor outcome for an accepted assessment, or <see langword="null"/> when quarantine is required.</summary>
    public static GovernedLoopEffectOutcome? GetAcceptedOutcome(GovernedLoopEffectReconciliationAssessmentKind assessment)
        => assessment switch
        {
            GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied => GovernedLoopEffectOutcome.NotApplied,
            GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedSucceeded => GovernedLoopEffectOutcome.Succeeded,
            GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedFailed => GovernedLoopEffectOutcome.Failed,
            _ => null
        };

    /// <summary>Gets whether an accepted resolution outcome exactly matches the current assessment.</summary>
    public static bool IsResolutionOutcomeAllowed(GovernedLoopEffectReconciliationAssessmentKind assessment, GovernedLoopEffectOutcome outcome)
        => GetAcceptedOutcome(assessment) == outcome;
}
