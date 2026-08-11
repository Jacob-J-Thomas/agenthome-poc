using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EmbodySense.Core.Common.Loops.Execution;

/// <summary>Computes, applies, and verifies the canonical hash of one exact bound execution frontier.</summary>
public static class GovernedLoopFrontierContractHash
{
    /// <summary>Computes the canonical hash over every bound frontier coordinate except the hash field itself.</summary>
    public static string Compute(GovernedLoopFrontierPosture frontier)
    {
        ArgumentNullException.ThrowIfNull(frontier);
        ArgumentNullException.ThrowIfNull(frontier.Binding);
        ArgumentNullException.ThrowIfNull(frontier.Payload);
        var canonical = new StringBuilder(2048);
        Append(canonical, "governed-loop-frontier-v1");
        Append(canonical, frontier.SchemaVersion);
        Append(canonical, frontier.WorkspaceId);
        Append(canonical, frontier.Binding.SchemaVersion);
        Append(canonical, frontier.Binding.RunId);
        Append(canonical, frontier.Binding.Revision.SchemaVersion);
        Append(canonical, frontier.Binding.Revision.GraphId);
        Append(canonical, frontier.Binding.Revision.RevisionId);
        Append(canonical, frontier.Binding.Revision.ExecutableHash);
        Append(canonical, frontier.Binding.ExecutionGeneration);
        Append(canonical, frontier.GraphArtifactHash);
        Append(canonical, frontier.GraphLayoutHash);
        Append(canonical, frontier.AdmissionReceiptHash);
        Append(canonical, frontier.Payload.SchemaVersion);
        Append(canonical, frontier.Payload.FrontierVersion);
        Append(canonical, frontier.Payload.ConcurrencyCeiling);
        Append(canonical, (int)frontier.Payload.Status);
        Append(canonical, frontier.Payload.Nodes.Count);
        foreach (var node in frontier.Payload.Nodes)
        {
            Append(canonical, node.SchemaVersion);
            Append(canonical, node.PlanOrdinal);
            Append(canonical, node.NodeId);
            Append(canonical, (int)node.Descriptor.Kind);
            Append(canonical, node.Descriptor.TypeId);
            Append(canonical, node.Descriptor.Version);
            AppendCollection(canonical, node.IncomingControlEdgeIds);
            AppendCollection(canonical, node.OutgoingControlEdgeIds);
            Append(canonical, (int)node.Status);
            AppendNullable(canonical, node.Attempt);
            Append(canonical, node.AttemptOperationId);
            Append(canonical, node.OutcomeEvidenceId);
            Append(canonical, node.OutcomeEvidenceHash);
        }

        Append(canonical, frontier.Payload.UpdatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    /// <summary>Returns a defensive frontier copy carrying its exact canonical content hash.</summary>
    public static GovernedLoopFrontierPosture Apply(GovernedLoopFrontierPosture frontier)
    {
        ArgumentNullException.ThrowIfNull(frontier);
        return frontier.WithPayload(frontier.Payload.WithContentHash(Compute(frontier)));
    }

    /// <summary>Gets whether a frontier retains its exact canonical content hash.</summary>
    public static bool Matches(GovernedLoopFrontierPosture? frontier)
    {
        if (frontier?.Payload.ContentHash is not { Length: GovernedLoopExecutionLimits.Sha256HexCharacters } actual)
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(Compute(frontier)));
        }
        catch (Exception exception) when (exception is ArgumentException or NullReferenceException)
        {
            return false;
        }
    }

    private static void AppendCollection(StringBuilder canonical, IReadOnlyList<string> values)
    {
        Append(canonical, values.Count);
        foreach (var value in values)
        {
            Append(canonical, value);
        }
    }

    private static void AppendNullable(StringBuilder canonical, int? value)
    {
        if (value is null)
        {
            canonical.Append("-1:");
        }
        else
        {
            Append(canonical, value.Value);
        }
    }

    private static void Append(StringBuilder canonical, int value) => Append(canonical, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder canonical, long value) => Append(canonical, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder canonical, string? value)
    {
        if (value is null)
        {
            canonical.Append("-1:");
            return;
        }

        var normalized = value.Normalize(NormalizationForm.FormC);
        canonical.Append(Encoding.UTF8.GetByteCount(normalized).ToString(CultureInfo.InvariantCulture));
        canonical.Append(':');
        canonical.Append(normalized);
    }
}
