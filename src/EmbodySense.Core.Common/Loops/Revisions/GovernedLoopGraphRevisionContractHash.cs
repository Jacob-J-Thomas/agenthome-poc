using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Loops.Revisions;

/// <summary>Computes domain-separated canonical hashes for immutable graph-revision artifacts.</summary>
public static class GovernedLoopGraphRevisionContractHash
{
    /// <summary>Computes the canonical lowercase SHA-256 digest of display and layout content.</summary>
    /// <param name="graph">The canonical graph whose display metadata is hashed.</param>
    /// <returns>A digest that deliberately excludes executable content, graph identity, and revision identity.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="graph"/> is <see langword="null"/>.</exception>
    public static string ComputeLayoutHash(GovernedLoopGraphDefinition graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var canonical = Begin("governed-loop-graph-layout-v1");
        Append(canonical, graph.SchemaVersion);
        Append(canonical, graph.DisplayMetadata.DisplayName);
        Append(canonical, graph.DisplayMetadata.Description);
        Append(canonical, graph.DisplayMetadata.Nodes.Count);
        foreach (var node in graph.DisplayMetadata.Nodes.OrderBy(item => item.NodeId, StringComparer.Ordinal))
        {
            Append(canonical, node.NodeId);
            Append(canonical, node.DisplayName);
            Append(canonical, node.Description);
            Append(canonical, node.CanvasX);
            Append(canonical, node.CanvasY);
        }

        return Digest(canonical);
    }

    /// <summary>Recomputes the canonical full-artifact digest after validating all stored derived identities.</summary>
    /// <param name="artifact">The immutable graph-revision artifact.</param>
    /// <returns>The recomputed digest binding lineage, executable content, and layout.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="artifact"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the artifact composition or a stored derived hash is invalid.</exception>
    public static string ComputeArtifactHash(GovernedLoopGraphRevisionArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        GovernedLoopGraphRevisionArtifactFactory.RequireValidComposition(artifact, nameof(artifact));
        var layoutHash = ComputeLayoutHash(artifact.Graph);
        return ComputeArtifactHashCore(artifact.SchemaVersion, artifact.RevisionArtifact, artifact.Graph, layoutHash);
    }

    internal static string ComputeArtifactHashCore(
        int schemaVersion,
        GovernedLoopRevisionArtifact revisionArtifact,
        GovernedLoopGraphDefinition graph,
        string layoutHash)
    {
        var canonical = Begin("governed-loop-graph-revision-artifact-v1");
        Append(canonical, schemaVersion);
        Append(canonical, GovernedLoopRevisionContractHash.ComputeArtifactHash(revisionArtifact));
        Append(canonical, graph.ExecutableHash);
        Append(canonical, layoutHash);
        return Digest(canonical);
    }

    private static StringBuilder Begin(string domain)
    {
        var canonical = new StringBuilder(1024);
        Append(canonical, domain);
        return canonical;
    }

    private static void Append(StringBuilder canonical, int? value)
        => Append(canonical, value?.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder canonical, int value)
        => Append(canonical, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder canonical, string? value)
    {
        if (value is null)
        {
            canonical.Append("-1:");
            return;
        }

        canonical.Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture));
        canonical.Append(':');
        canonical.Append(value);
    }

    private static string Digest(StringBuilder canonical)
    {
        var bytes = Encoding.UTF8.GetBytes(canonical.ToString());
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
