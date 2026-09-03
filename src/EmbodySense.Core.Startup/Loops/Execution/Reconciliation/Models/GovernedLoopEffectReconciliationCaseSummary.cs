namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Projects one value-free reconciliation attention item.</summary>
/// <param name="Reference">The exact immutable redacted case reference.</param>
/// <param name="Posture">The current closed case posture.</param>
public sealed record GovernedLoopEffectReconciliationCaseSummary(GovernedLoopEffectReconciliationCaseReference Reference, GovernedLoopEffectReconciliationCasePosture Posture)
{

    /// <summary>Gets the exact immutable case reference.</summary>
    public GovernedLoopEffectReconciliationCaseReference Reference { get; } = Reference ?? throw new ArgumentNullException(nameof(Reference));

    /// <summary>Gets the closed case posture.</summary>
    public GovernedLoopEffectReconciliationCasePosture Posture { get; } = Posture != GovernedLoopEffectReconciliationCasePosture.Unknown && Enum.IsDefined(Posture)
        ? Posture
        : throw new ArgumentOutOfRangeException(nameof(Posture));
}
