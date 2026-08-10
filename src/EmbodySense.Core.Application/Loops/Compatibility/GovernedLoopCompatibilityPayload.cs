using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Application.Loops.Compatibility;

/// <summary>
/// Carries validated unbound lifecycle/frontier payloads plus explicitly noncanonical effect and projection observations regenerated from one legacy source.
/// </summary>
/// <remarks>
/// This compatibility value deliberately has no revision or execution binding and therefore cannot enter a canonical
/// runtime, persistence, recovery, or mutation port. Missing payload planes are represented by explicit result gaps.
/// </remarks>
public sealed class GovernedLoopCompatibilityPayload
{
    private readonly IReadOnlyList<GovernedLoopCompatibilityEffectObservation> _effects;
    private readonly IReadOnlyList<GovernedLoopCompatibilityProjectionObservation> _projections;

    internal GovernedLoopCompatibilityPayload(
        GovernedLoopRunLifecyclePayload lifecycle,
        GovernedLoopFrontierPayload? frontier,
        IEnumerable<GovernedLoopCompatibilityEffectObservation> effects,
        IEnumerable<GovernedLoopCompatibilityProjectionObservation> projections)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(projections);
        var boundedEffects = effects.Take(GovernedLoopCompatibilityLimits.MaxEffectObservations + 1).ToArray();
        var boundedProjections = projections.Take(GovernedLoopCompatibilityLimits.MaxProjectionObservations + 1).ToArray();
        if (boundedEffects.Length > GovernedLoopCompatibilityLimits.MaxEffectObservations)
        {
            throw new ArgumentOutOfRangeException(nameof(effects), $"Compatibility payloads cannot contain more than {GovernedLoopCompatibilityLimits.MaxEffectObservations} effect observations.");
        }

        if (boundedProjections.Length > GovernedLoopCompatibilityLimits.MaxProjectionObservations)
        {
            throw new ArgumentOutOfRangeException(nameof(projections), $"Compatibility payloads cannot contain more than {GovernedLoopCompatibilityLimits.MaxProjectionObservations} projection observations.");
        }

        if (boundedEffects.Any(effect => effect is null) || boundedProjections.Any(projection => projection is null))
        {
            throw new ArgumentException("Compatibility observations cannot contain null entries.");
        }

        Lifecycle = lifecycle;
        Frontier = frontier;
        _effects = Array.AsReadOnly(boundedEffects
            .OrderBy(effect => effect.EffectId, StringComparer.Ordinal)
            .ThenBy(effect => effect.SourceGeneration)
            .ThenBy(effect => effect.OperationId, StringComparer.Ordinal)
            .ToArray());
        _projections = Array.AsReadOnly(boundedProjections
            .OrderBy(projection => projection.ProjectionId, StringComparer.Ordinal)
            .ThenBy(projection => projection.OperationId, StringComparer.Ordinal)
            .ToArray());
    }

    /// <summary>Gets the unbound lifecycle payload.</summary>
    public GovernedLoopRunLifecyclePayload Lifecycle { get; }

    /// <summary>Gets an unbound frontier payload when the source can prove one; current adapters return <see langword="null"/>.</summary>
    public GovernedLoopFrontierPayload? Frontier { get; }

    /// <summary>Gets defensive, identity-sorted compatibility effect observations that contain no canonical intent hash.</summary>
    public IReadOnlyList<GovernedLoopCompatibilityEffectObservation> Effects => _effects;

    /// <summary>Gets defensive, identity-sorted compatibility projection observations that contain no optimistic versions.</summary>
    public IReadOnlyList<GovernedLoopCompatibilityProjectionObservation> Projections => _projections;
}
