namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Projects one exact reconciliation case without raw execution, actuator, input, or authority material.</summary>
/// <param name="Reference">The exact immutable case reference.</param>
/// <param name="Posture">The closed case posture.</param>
/// <param name="Contract">The redacted pinned contract.</param>
/// <param name="EvidenceSources">The bounded source registrations.</param>
/// <param name="Observations">The bounded immutable observation history.</param>
/// <param name="Assessments">The bounded immutable assessment history.</param>
/// <param name="Disposition">The optional immutable disposition.</param>
/// <param name="Resolution">The optional immutable accepted resolution.</param>
/// <param name="ReceiptHashes">The bounded value-free durable receipt hashes.</param>
/// <param name="OpenedAtUtc">The trusted case opening time.</param>
/// <param name="UpdatedAtUtc">The trusted case update time.</param>
public sealed record GovernedLoopEffectReconciliationCaseDetail(
    GovernedLoopEffectReconciliationCaseReference Reference,
    GovernedLoopEffectReconciliationCasePosture Posture,
    GovernedLoopEffectReconciliationContractProjection Contract,
    IReadOnlyList<GovernedLoopEffectReconciliationEvidenceSourceProjection> EvidenceSources,
    IReadOnlyList<GovernedLoopEffectReconciliationObservationProjection> Observations,
    IReadOnlyList<GovernedLoopEffectReconciliationAssessmentProjection> Assessments,
    GovernedLoopEffectReconciliationDispositionProjection? Disposition,
    GovernedLoopEffectReconciliationResolutionProjection? Resolution,
    IReadOnlyList<string> ReceiptHashes,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{

    /// <summary>Gets the exact immutable case reference.</summary>
    public GovernedLoopEffectReconciliationCaseReference Reference { get; } = Reference ?? throw new ArgumentNullException(nameof(Reference));
    /// <summary>Gets the closed case posture.</summary>
    public GovernedLoopEffectReconciliationCasePosture Posture { get; } = Posture != GovernedLoopEffectReconciliationCasePosture.Unknown && Enum.IsDefined(Posture)
        ? Posture
        : throw new ArgumentOutOfRangeException(nameof(Posture));
    /// <summary>Gets the redacted pinned contract.</summary>
    public GovernedLoopEffectReconciliationContractProjection Contract { get; } = Contract ?? throw new ArgumentNullException(nameof(Contract));
    /// <summary>Gets the bounded source registrations.</summary>
    public IReadOnlyList<GovernedLoopEffectReconciliationEvidenceSourceProjection> EvidenceSources { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Items(EvidenceSources, 32, nameof(EvidenceSources));
    /// <summary>Gets the bounded immutable observation history.</summary>
    public IReadOnlyList<GovernedLoopEffectReconciliationObservationProjection> Observations { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Items(Observations, 32, nameof(Observations));
    /// <summary>Gets the bounded immutable assessment history.</summary>
    public IReadOnlyList<GovernedLoopEffectReconciliationAssessmentProjection> Assessments { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Items(Assessments, 32, nameof(Assessments));
    /// <summary>Gets the optional immutable disposition.</summary>
    public GovernedLoopEffectReconciliationDispositionProjection? Disposition { get; } = Disposition;
    /// <summary>Gets the optional immutable accepted resolution.</summary>
    public GovernedLoopEffectReconciliationResolutionProjection? Resolution { get; } = Resolution;
    /// <summary>Gets the bounded value-free durable receipt hashes.</summary>
    public IReadOnlyList<string> ReceiptHashes { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Items(
        (ReceiptHashes ?? throw new ArgumentNullException(nameof(ReceiptHashes))).Select(value => GovernedLoopEffectReconciliationSurfaceGuard.Hash(value, nameof(ReceiptHashes))),
        GovernedLoopEffectReconciliationSurfaceGuard.MaxHistoryEntries,
        nameof(ReceiptHashes));
    /// <summary>Gets the trusted case opening time.</summary>
    public DateTimeOffset OpenedAtUtc { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Utc(OpenedAtUtc, nameof(OpenedAtUtc));
    /// <summary>Gets the trusted case update time.</summary>
    public DateTimeOffset UpdatedAtUtc { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Utc(UpdatedAtUtc, nameof(UpdatedAtUtc));
}
