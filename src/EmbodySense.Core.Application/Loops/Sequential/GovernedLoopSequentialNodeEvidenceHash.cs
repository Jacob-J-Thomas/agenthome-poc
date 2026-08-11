using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using EmbodySense.Core.Application.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Computes the canonical digest that causally names one bounded retained node-evidence receipt.</summary>
public static class GovernedLoopSequentialNodeEvidenceHash
{
    /// <summary>Computes a lowercase SHA-256 digest over every receipt coordinate except the digest itself.</summary>
    public static string Compute(GovernedLoopSequentialNodeEvidenceReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(receipt.Revision);
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", receipt.SchemaVersion);
        writer.WriteString("kind", ToCanonical(receipt.Kind));
        writer.WriteString("workspaceId", receipt.WorkspaceId);
        writer.WriteString("runId", receipt.RunId);
        writer.WritePropertyName("revision");
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", receipt.Revision.SchemaVersion);
        writer.WriteString("graphId", receipt.Revision.GraphId);
        writer.WriteString("revisionId", receipt.Revision.RevisionId);
        writer.WriteString("executableHash", receipt.Revision.ExecutableHash);
        writer.WriteEndObject();
        writer.WriteNumber("executionGeneration", receipt.ExecutionGeneration);
        writer.WriteString("nodeId", receipt.NodeId);
        writer.WriteNumber("attempt", receipt.Attempt);
        writer.WriteString("disposition", ToCanonical(receipt.Disposition));
        writer.WriteString("outcomeArtifactHash", receipt.OutcomeArtifactHash);
        writer.WriteEndObject();
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    /// <summary>Returns a copy carrying its canonical digest.</summary>
    public static GovernedLoopSequentialNodeEvidenceReceipt Apply(GovernedLoopSequentialNodeEvidenceReceipt receipt)
        => receipt with { EvidenceHash = Compute(receipt) };

    /// <summary>Returns whether the declared digest matches every exact receipt coordinate.</summary>
    public static bool Matches(GovernedLoopSequentialNodeEvidenceReceipt? receipt)
    {
        if (receipt is null || !IsHash(receipt.EvidenceHash) || !IsHash(receipt.OutcomeArtifactHash))
        {
            return false;
        }

        try
        {
            return string.Equals(receipt.EvidenceHash, Compute(receipt), StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsHash(string? value)
        => value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string ToCanonical(GovernedLoopSequentialNodeEvidenceKind kind)
        => kind switch
        {
            GovernedLoopSequentialNodeEvidenceKind.CompletedOutcome => "completed-outcome",
            GovernedLoopSequentialNodeEvidenceKind.DefinitiveRejection => "definitive-rejection",
            GovernedLoopSequentialNodeEvidenceKind.AmbiguityAttention => "ambiguity-attention",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static string ToCanonical(GovernedLoopSequentialNodeHandlerResultStatus disposition)
        => disposition switch
        {
            GovernedLoopSequentialNodeHandlerResultStatus.Completed => "completed",
            GovernedLoopSequentialNodeHandlerResultStatus.Rejected => "rejected",
            GovernedLoopSequentialNodeHandlerResultStatus.NeedsReview => "needs-review",
            _ => throw new ArgumentOutOfRangeException(nameof(disposition)),
        };
}
