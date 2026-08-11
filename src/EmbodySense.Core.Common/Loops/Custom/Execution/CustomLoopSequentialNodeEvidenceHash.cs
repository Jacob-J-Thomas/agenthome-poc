using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Common.Loops.Custom.Execution;

/// <summary>Computes the canonical digest for durable sequential-node evidence.</summary>
public static class CustomLoopSequentialNodeEvidenceHash
{
    /// <summary>Computes a lowercase SHA-256 digest over every evidence coordinate except the digest itself.</summary>
    public static string Compute(CustomLoopSequentialNodeEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(evidence.Revision);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", evidence.SchemaVersion);
        writer.WriteString("kind", ToCanonical(evidence.Kind));
        writer.WriteString("workspaceId", evidence.WorkspaceId);
        writer.WriteString("runId", evidence.RunId);
        writer.WritePropertyName("revision");
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", evidence.Revision.SchemaVersion);
        writer.WriteString("graphId", evidence.Revision.GraphId);
        writer.WriteString("revisionId", evidence.Revision.RevisionId);
        writer.WriteString("executableHash", evidence.Revision.ExecutableHash);
        writer.WriteEndObject();
        writer.WriteNumber("executionGeneration", evidence.ExecutionGeneration);
        writer.WriteString("nodeId", evidence.NodeId);
        writer.WriteNumber("attempt", evidence.Attempt);
        writer.WriteString("disposition", ToCanonical(evidence.Disposition));
        writer.WriteString("outcomeArtifactHash", evidence.OutcomeArtifactHash);
        writer.WriteEndObject();
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    /// <summary>Returns a copy carrying its canonical digest.</summary>
    public static CustomLoopSequentialNodeEvidence Apply(CustomLoopSequentialNodeEvidence evidence)
        => evidence with { EvidenceHash = Compute(evidence) };

    /// <summary>Returns whether the declared digest matches every exact evidence coordinate.</summary>
    public static bool Matches(CustomLoopSequentialNodeEvidence? evidence)
    {
        if (evidence is null || !IsHash(evidence.EvidenceHash) || !IsHash(evidence.OutcomeArtifactHash))
        {
            return false;
        }

        try
        {
            var expected = Compute(evidence);
            return CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(expected),
                System.Text.Encoding.ASCII.GetBytes(evidence.EvidenceHash));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsHash(string? value)
        => value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string ToCanonical(CustomLoopSequentialNodeEvidenceKind kind)
        => kind switch
        {
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted => "dispatch-started",
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome => "completed-outcome",
            CustomLoopSequentialNodeEvidenceKind.DefinitiveRejection => "definitive-rejection",
            CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention => "ambiguity-attention",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static string ToCanonical(CustomLoopSequentialNodeDisposition disposition)
        => disposition switch
        {
            CustomLoopSequentialNodeDisposition.Unknown => "unknown",
            CustomLoopSequentialNodeDisposition.Completed => "completed",
            CustomLoopSequentialNodeDisposition.Rejected => "rejected",
            CustomLoopSequentialNodeDisposition.NeedsReview => "needs-review",
            _ => throw new ArgumentOutOfRangeException(nameof(disposition)),
        };
}
