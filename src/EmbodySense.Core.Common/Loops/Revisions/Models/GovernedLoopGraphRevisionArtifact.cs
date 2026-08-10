using EmbodySense.Core.Common.Loops.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.Revisions.Models;

/// <summary>Composes one immutable governed-loop graph payload with its exact revision lineage and derived content identities.</summary>
/// <remarks>Instances can only be created by <see cref="GovernedLoopGraphRevisionArtifactFactory"/>, so callers cannot forge the derived layout or full-artifact hashes.</remarks>
public sealed class GovernedLoopGraphRevisionArtifact
{
    internal GovernedLoopGraphRevisionArtifact(
        int schemaVersion,
        GovernedLoopRevisionArtifact revisionArtifact,
        GovernedLoopGraphDefinition graph,
        string layoutHash,
        string artifactHash)
    {
        SchemaVersion = schemaVersion;
        RevisionArtifact = revisionArtifact;
        Graph = graph;
        LayoutHash = layoutHash;
        ArtifactHash = artifactHash;
    }

    /// <summary>Schema version required by the immutable graph-revision artifact.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the schema version.</summary>
    /// <value>Always <see cref="CurrentSchemaVersion"/>.</value>
    public int SchemaVersion { get; }

    /// <summary>Gets the exact immutable lifecycle lineage and creation provenance.</summary>
    public GovernedLoopRevisionArtifact RevisionArtifact { get; }

    /// <summary>Gets the deeply snapshotted executable graph and display layout.</summary>
    public GovernedLoopGraphDefinition Graph { get; }

    /// <summary>Gets the lowercase SHA-256 digest of display and layout content.</summary>
    public string LayoutHash { get; }

    /// <summary>Gets the lowercase SHA-256 digest binding lineage, executable content, and layout.</summary>
    public string ArtifactHash { get; }
}
