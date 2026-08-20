using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Computes the canonical digest that causally names one bounded retained node-evidence receipt.</summary>
public static class GovernedLoopSequentialNodeEvidenceHash
{
    /// <summary>Computes a lowercase SHA-256 digest over every receipt coordinate except the digest itself.</summary>
    public static string Compute(GovernedLoopSequentialNodeEvidenceReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(receipt.Revision);
        ArgumentNullException.ThrowIfNull(receipt.SelectedControlEdgeIds);
        ArgumentNullException.ThrowIfNull(receipt.SkippedControlEdgeIds);
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
        writer.WriteNumber("activationOrdinal", receipt.ActivationOrdinal);
        writer.WriteNumber("visitOrdinal", receipt.VisitOrdinal);
        writer.WriteString("nodeId", receipt.NodeId);
        writer.WriteNumber("attempt", receipt.Attempt);
        writer.WriteString("cycleId", receipt.CycleId);
        if (receipt.CycleIteration is { } cycleIteration)
        {
            writer.WriteNumber("cycleIteration", cycleIteration);
        }
        else
        {
            writer.WriteNull("cycleIteration");
        }

        if (receipt.ControlOutcome is { } controlOutcome)
        {
            writer.WriteString("controlOutcome", ToCanonical(controlOutcome));
        }
        else
        {
            writer.WriteNull("controlOutcome");
        }

        WriteIdentifiers(writer, "selectedControlEdgeIds", receipt.SelectedControlEdgeIds);
        WriteIdentifiers(writer, "skippedControlEdgeIds", receipt.SkippedControlEdgeIds);
        writer.WriteNull("governingActivationOrdinal");
        writer.WriteNull("governingControlEdgeId");
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

    private static string ToCanonical(GovernedLoopControlCondition condition)
        => condition switch
        {
            GovernedLoopControlCondition.Always => "always",
            GovernedLoopControlCondition.Success => "success",
            GovernedLoopControlCondition.Failure => "failure",
            GovernedLoopControlCondition.True => "true",
            GovernedLoopControlCondition.False => "false",
            GovernedLoopControlCondition.Timeout => "timeout",
            GovernedLoopControlCondition.Approved => "approved",
            GovernedLoopControlCondition.Rejected => "rejected",
            _ => throw new ArgumentOutOfRangeException(nameof(condition)),
        };

    private static void WriteIdentifiers(Utf8JsonWriter writer, string propertyName, IEnumerable<string> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
