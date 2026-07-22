using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

public static class CustomLoopContextSnapshotHash
{
    public static string Compute(CustomLoopContextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", snapshot.SchemaVersion);
            writer.WriteString("capturedAtUtc", ToCanonicalTimestamp(snapshot.CapturedAtUtc));
            writer.WritePropertyName("sourceManifest");
            if (snapshot.SourceManifest is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartArray();
                foreach (var source in snapshot.SourceManifest)
                {
                    WriteSource(writer, source);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    public static CustomLoopContextSnapshot Apply(CustomLoopContextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot with { ManifestHash = Compute(snapshot) };
    }

    public static bool Matches(CustomLoopContextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var expected = Encoding.ASCII.GetBytes(Compute(snapshot));
        var actual = Encoding.ASCII.GetBytes(snapshot.ManifestHash ?? string.Empty);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static void WriteSource(Utf8JsonWriter writer, CustomLoopContextManifestSource? source)
    {
        if (source is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("order", source.Order);
        writer.WriteString("sourceType", ToCanonical(source.SourceType));
        writer.WriteString("sourceId", source.SourceId);
        writer.WriteString("sourcePath", source.SourcePath);
        writer.WriteString("provenance", ToCanonical(source.Provenance));
        writer.WriteString("trustClass", ToCanonical(source.TrustClass));
        writer.WriteString("role", ToCanonical(source.Role));
        writer.WriteString("content", source.Content);
        writer.WriteString("contentHash", source.ContentHash);
        writer.WriteNumber("originalCharacterCount", source.OriginalCharacterCount);
        writer.WriteNumber("usedCharacterCount", source.UsedCharacterCount);
        writer.WriteBoolean("truncated", source.Truncated);
        WriteNullableString(writer, "truncationReason", source.TruncationReason);
        WriteNullableString(writer, "omissionReason", source.OmissionReason);
        writer.WriteString("capturedAtUtc", ToCanonicalTimestamp(source.CapturedAtUtc));
        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static string ToCanonicalTimestamp(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static string ToCanonical(CustomLoopContextSource source)
    {
        return source switch
        {
            CustomLoopContextSource.HarnessGovernance => "harness-governance",
            CustomLoopContextSource.RoleInstruction => "role-instruction",
            CustomLoopContextSource.ContextualState => "contextual-state",
            CustomLoopContextSource.RunMetadata => "run-metadata",
            CustomLoopContextSource.NodeInstruction => "node-instruction",
            CustomLoopContextSource.TriggerPrompt => "trigger-prompt",
            CustomLoopContextSource.InvokingConversation => "invoking-conversation",
            CustomLoopContextSource.EarlierRetainedOutput => "earlier-retained-output",
            CustomLoopContextSource.PreviousIterationResult => "previous-iteration-result",
            _ => $"unknown:{(int)source}"
        };
    }

    private static string ToCanonical(CustomLoopContextProvenance provenance)
    {
        return provenance switch
        {
            CustomLoopContextProvenance.HarnessRuntime => "harness-runtime",
            CustomLoopContextProvenance.WorkspaceRoleFile => "workspace-role-file",
            CustomLoopContextProvenance.WorkspaceContextFile => "workspace-context-file",
            CustomLoopContextProvenance.ServerRunState => "server-run-state",
            CustomLoopContextProvenance.AuthoredDefinition => "authored-definition",
            CustomLoopContextProvenance.ManualInvocation => "manual-invocation",
            CustomLoopContextProvenance.LogicalConversation => "logical-conversation",
            CustomLoopContextProvenance.ModelOutput => "model-output",
            CustomLoopContextProvenance.AgentIdentityFile => "agent-identity-file",
            _ => $"unknown:{(int)provenance}"
        };
    }

    private static string ToCanonical(CustomLoopContextTrustClass trustClass)
    {
        return trustClass switch
        {
            CustomLoopContextTrustClass.NonOverridableGovernance => "non-overridable-governance",
            CustomLoopContextTrustClass.TrustedInstruction => "trusted-instruction",
            CustomLoopContextTrustClass.TrustedMetadata => "trusted-metadata",
            CustomLoopContextTrustClass.UntrustedData => "untrusted-data",
            _ => $"unknown:{(int)trustClass}"
        };
    }

    private static string ToCanonical(LlmMessageRole role)
    {
        return role switch
        {
            LlmMessageRole.System => "system",
            LlmMessageRole.User => "user",
            LlmMessageRole.Assistant => "assistant",
            LlmMessageRole.Tool => "tool",
            _ => $"unknown:{(int)role}"
        };
    }
}
