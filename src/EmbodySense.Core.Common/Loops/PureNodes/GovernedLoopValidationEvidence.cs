using System.Collections.ObjectModel;
using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Common.Loops.PureNodes;

/// <summary>Contains a deterministic Boolean validation result and its bounded path/code evidence.</summary>
public sealed class GovernedLoopValidationEvidence
{
    private GovernedLoopValidationEvidence(bool passed, GovernedLoopValidationObservation[] observations)
    {
        Passed = passed;
        Observations = new ReadOnlyCollection<GovernedLoopValidationObservation>(observations);
    }

    /// <summary>The only supported validation-evidence schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the schema version.</summary>
    /// <value>Always <see cref="CurrentSchemaVersion"/>.</value>
    public int SchemaVersion => CurrentSchemaVersion;
    /// <summary>Gets the exact Boolean validator result.</summary>
    /// <value><see langword="true"/> when validation passed.</value>
    public bool Passed { get; }
    /// <summary>Gets sorted unique failure observations.</summary>
    /// <value>An empty immutable collection on success, otherwise one or more bounded observations.</value>
    public IReadOnlyList<GovernedLoopValidationObservation> Observations { get; }

    /// <summary>Creates internally consistent validation evidence.</summary>
    /// <param name="schemaVersion">The schema version, which must be 1.</param>
    /// <param name="passed">The exact Boolean result.</param>
    /// <param name="observations">Failure observations; empty on success and non-empty on failure.</param>
    /// <returns>An immutable canonical evidence value.</returns>
    /// <exception cref="ArgumentException">Thrown for an unsupported version, invalid count, duplicates, or inconsistent success posture.</exception>
    public static GovernedLoopValidationEvidence Create(int schemaVersion, bool passed, IEnumerable<GovernedLoopValidationObservation> observations)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentException("Validation evidence supports schema version 1 only.", nameof(schemaVersion));
        }

        ArgumentNullException.ThrowIfNull(observations);
        GovernedLoopValidationObservation[] values;
        try
        {
            values = observations.Take(CustomLoopLimits.MaxGraphPureNodeObservations + 1).ToArray();
        }
        catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
        {
            throw new ArgumentException("Validation evidence observations must be inspectable within the bounded contract.", nameof(observations), exception);
        }

        if (values.Any(value => value is null) || values.Length > CustomLoopLimits.MaxGraphPureNodeObservations || passed != (values.Length == 0) || values.Distinct().Count() != values.Length)
        {
            throw new ArgumentException("Validation evidence must be bounded, unique, empty only on success, and non-empty on failure.", nameof(observations));
        }

        var canonical = values.OrderBy(value => value.Path, StringComparer.Ordinal).ThenBy(value => value.Code, StringComparer.Ordinal).ToArray();
        return new GovernedLoopValidationEvidence(passed, canonical);
    }
}
