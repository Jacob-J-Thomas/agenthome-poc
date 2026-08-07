namespace EmbodySense.Core.Common.Authority.Models;

/// <summary>
/// Captures bounded, non-secret evidence for one evaluated authority boundary decision.
/// </summary>
public sealed record AuthorityBoundaryReceipt
{
    /// <summary>Gets the only supported experimental boundary-receipt schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    internal AuthorityBoundaryReceipt(int schemaVersion, AuthorityBoundaryDecision decision, IReadOnlyList<AuthorityBoundaryCondition> conditions, IReadOnlyList<AuthorityProfileReference> profiles, DateTimeOffset evaluatedAtUtc)
    {
        SchemaVersion = schemaVersion;
        Decision = decision;
        Conditions = conditions;
        Profiles = profiles;
        EvaluatedAtUtc = evaluatedAtUtc;
    }

    /// <summary>Gets the boundary-receipt schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the highest-precedence boundary decision.</summary>
    public AuthorityBoundaryDecision Decision { get; }

    /// <summary>Gets the exact closed reasons that contributed to the decision.</summary>
    public IReadOnlyList<AuthorityBoundaryCondition> Conditions { get; }

    /// <summary>Gets the unique profile revisions considered by the evaluation.</summary>
    public IReadOnlyList<AuthorityProfileReference> Profiles { get; }

    /// <summary>Gets the exact UTC evaluation time.</summary>
    public DateTimeOffset EvaluatedAtUtc { get; }
}
