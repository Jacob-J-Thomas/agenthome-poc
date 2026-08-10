namespace EmbodySense.Core.Common.Loops.Execution;

/// <summary>Aggregates the four canonical execution planes after proving one exact binding and legal composition.</summary>
/// <remarks>Construction fails closed before exposing a canonical aggregate.</remarks>
public sealed record GovernedLoopExecutionEvidenceSet
{
    private GovernedLoopExecutionEvidenceSet(
        int schemaVersion,
        GovernedLoopRunLifecycle lifecycle,
        GovernedLoopFrontierPosture frontier,
        IReadOnlyList<GovernedLoopEffectPosture> effects,
        IReadOnlyList<GovernedLoopProjectionPosture> projections)
    {
        SchemaVersion = schemaVersion;
        Lifecycle = lifecycle;
        Frontier = frontier;
        Effects = effects;
        Projections = projections;
    }

    /// <summary>Gets the schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the canonical bound lifecycle plane.</summary>
    public GovernedLoopRunLifecycle Lifecycle { get; }

    /// <summary>Gets the canonical bound frontier plane.</summary>
    public GovernedLoopFrontierPosture Frontier { get; }

    /// <summary>Gets the sorted unique canonical bound effect postures.</summary>
    public IReadOnlyList<GovernedLoopEffectPosture> Effects { get; }

    /// <summary>Gets the sorted unique canonical bound projection postures.</summary>
    public IReadOnlyList<GovernedLoopProjectionPosture> Projections { get; }

    /// <summary>Creates a canonical aggregate only after all plane and composition invariants pass.</summary>
    /// <param name="schemaVersion">The schema version, which must be 1.</param>
    /// <param name="lifecycle">The canonical bound lifecycle.</param>
    /// <param name="frontier">The canonical bound frontier.</param>
    /// <param name="effects">The effect postures sorted by effect identity.</param>
    /// <param name="projections">The projection postures sorted by projection identity.</param>
    /// <returns>The canonical aggregate.</returns>
    /// <exception cref="ArgumentNullException">Thrown when a required plane or evidence collection is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when an evidence collection exceeds its supported bound.</exception>
    /// <exception cref="ArgumentException">Thrown when the schema, an evidence collection, or the four-plane composition is invalid.</exception>
    public static GovernedLoopExecutionEvidenceSet Create(
        int schemaVersion,
        GovernedLoopRunLifecycle lifecycle,
        GovernedLoopFrontierPosture frontier,
        IEnumerable<GovernedLoopEffectPosture> effects,
        IEnumerable<GovernedLoopProjectionPosture> projections)
    {
        GovernedLoopExecutionContractGuard.RequireSchema(schemaVersion, nameof(schemaVersion));
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(frontier);
        var effectSnapshot = GovernedLoopExecutionContractGuard.SnapshotBounded(effects, nameof(effects), GovernedLoopExecutionLimits.MaxEffects);
        var projectionSnapshot = GovernedLoopExecutionContractGuard.SnapshotBounded(projections, nameof(projections), GovernedLoopExecutionLimits.MaxProjections);
        var validation = GovernedLoopExecutionValidator.ValidateComposition(schemaVersion, lifecycle, frontier, effectSnapshot, projectionSnapshot);
        if (!validation.IsValid)
        {
            throw new ArgumentException("Governed-loop execution evidence planes do not form one legal canonical aggregate.", nameof(lifecycle));
        }

        return new GovernedLoopExecutionEvidenceSet(schemaVersion, lifecycle, frontier, effectSnapshot, projectionSnapshot);
    }
}
