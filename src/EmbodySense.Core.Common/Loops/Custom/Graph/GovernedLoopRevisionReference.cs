using EmbodySense.Core.Common.Loops.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

/// <summary>Identifies one immutable executable loop revision without owning revision lifecycle.</summary>
/// <remarks>This schema-1 reference is the stable hand-off contract for revision storage and trigger consumers.</remarks>
public sealed record GovernedLoopRevisionReference
{
    private GovernedLoopRevisionReference(string graphId, string revisionId, string executableHash)
    {
        GraphId = graphId;
        RevisionId = revisionId;
        ExecutableHash = executableHash;
    }

    /// <summary>Schema version required by the stable governed loop revision reference.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the reference schema version.</summary>
    /// <value>Always <see cref="CurrentSchemaVersion"/>.</value>
    public int SchemaVersion => CurrentSchemaVersion;

    /// <summary>Gets the stable graph identifier.</summary>
    /// <value>The canonical identifier.</value>
    public string GraphId { get; }
    /// <summary>Gets the stable immutable revision identifier.</summary>
    /// <value>The canonical identifier.</value>
    public string RevisionId { get; }
    /// <summary>Gets the lowercase SHA-256 executable content hash.</summary>
    /// <value>The executable digest.</value>
    public string ExecutableHash { get; }

    /// <summary>Creates a validated immutable revision reference.</summary>
    /// <param name="schemaVersion">The reference schema version, which must be 1.</param>
    /// <param name="graphId">The stable graph identifier.</param>
    /// <param name="revisionId">The stable immutable revision identifier.</param>
    /// <param name="executableHash">The lowercase SHA-256 executable hash.</param>
    /// <returns>A canonical revision reference.</returns>
    /// <exception cref="ArgumentException">Thrown when an identifier or hash is non-canonical.</exception>
    public static GovernedLoopRevisionReference Create(int schemaVersion, string graphId, string revisionId, string executableHash)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentException($"Revision reference schema version must be {CurrentSchemaVersion}.", nameof(schemaVersion));
        }

        GovernedLoopGraphRules.RequireId(graphId, nameof(graphId));
        GovernedLoopGraphRules.RequireId(revisionId, nameof(revisionId));
        GovernedLoopGraphRules.RequireSha256(executableHash, nameof(executableHash));
        return new GovernedLoopRevisionReference(graphId, revisionId, executableHash);
    }
}
