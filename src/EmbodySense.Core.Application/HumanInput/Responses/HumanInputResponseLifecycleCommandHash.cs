using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses;

/// <summary>Computes the canonical hash binding a workspace-global operation identity to exact response intent.</summary>
public static class HumanInputResponseLifecycleCommandHash
{
    /// <summary>Computes a lowercase SHA-256 digest over every behavior-affecting command field except <see cref="HumanInputResponseLifecycleCommand.CommandHash"/>.</summary>
    /// <param name="command">The exact bounded command.</param>
    /// <returns>The canonical 64-character lowercase digest.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="command"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown before serialization when command data exceeds schema-1 bounds or contains malformed UTF-16.</exception>
    public static string Compute(HumanInputResponseLifecycleCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireBounded(command);
        var buffer = new ArrayBufferWriter<byte>();
        try
        {
            using var writer = new Utf8JsonWriter(buffer);
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", command.SchemaVersion);
            writer.WriteString("operationId", command.OperationId);
            writer.WriteNumber("kind", (int)command.Kind);
            writer.WriteString("requestId", command.RequestId);
            writer.WriteNumber("expectedLifecycleVersion", command.ExpectedLifecycleVersion);
            writer.WriteNumber("expectedLifecycleStatus", (int)command.ExpectedLifecycleStatus);
            writer.WritePropertyName("expectedRequest");
            WriteRequestReference(writer, command.ExpectedRequest);
            writer.WritePropertyName("expectedBinding");
            WriteBinding(writer, command.ExpectedBinding);
            WriteString(writer, "responseId", command.ResponseId);
            WriteString(writer, "valueHash", command.Value is null ? null : HumanInputResponseValueHash.Compute(command.Value));
            WriteString(writer, "explanation", command.Explanation);
            writer.WriteStartArray("targetResponses");
            if (!command.TargetResponses.IsDefault)
            {
                foreach (var target in command.TargetResponses)
                {
                    WriteResponseReference(writer, target);
                }
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("Human Input response command text must contain well-formed UTF-16.", nameof(command), exception);
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    /// <summary>Returns an immutable command copy carrying its canonical exact-intent hash.</summary>
    /// <param name="command">The exact bounded command.</param>
    /// <returns>A command copy with its canonical hash.</returns>
    public static HumanInputResponseLifecycleCommand Apply(HumanInputResponseLifecycleCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command with { CommandHash = Compute(command) };
    }

    /// <summary>Determines whether a command retains its exact canonical hash.</summary>
    /// <param name="command">The command to inspect.</param>
    /// <returns><see langword="true"/> only for a canonical fixed-time exact match.</returns>
    public static bool Matches(HumanInputResponseLifecycleCommand? command)
    {
        if (command is null || !IsSha256(command.CommandHash))
        {
            return false;
        }

        try
        {
            var expected = Encoding.ASCII.GetBytes(Compute(command));
            var actual = Encoding.ASCII.GetBytes(command.CommandHash);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void RequireBounded(HumanInputResponseLifecycleCommand command)
    {
        if (!HumanInputIdentifier.IsValid(command.OperationId)
            || !HumanInputIdentifier.IsValid(command.RequestId)
            || command.ResponseId is not null && !HumanInputIdentifier.IsValid(command.ResponseId)
            || command.Explanation is not null && !HumanInputText.IsValid(command.Explanation, HumanInputLimits.MaxExplanationCharacters, required: false)
            || !RequestReferenceIsWithin(command.ExpectedRequest)
            || !BindingIsWithin(command.ExpectedBinding)
            || command.TargetResponses.IsDefault
            || command.TargetResponses.Length > HumanInputResponseContractLimits.MaxSelectedResponses)
        {
            throw new ArgumentException("Human Input response command data is incomplete or exceeds schema-1 bounds.", nameof(command));
        }

        _ = command.Value is null ? null : HumanInputResponseValueHash.Compute(command.Value);
        foreach (var target in command.TargetResponses)
        {
            if (!ResponseReferenceIsWithin(target))
            {
                throw new ArgumentException("A target response reference exceeds schema-1 bounds.", nameof(command));
            }
        }
    }

    private static bool RequestReferenceIsWithin(HumanInputRequestReference? reference)
        => reference is not null
            && reference.SchemaVersion == HumanInputRequestReference.CurrentSchemaVersion
            && HumanInputIdentifier.IsValid(reference.RequestId)
            && HumanInputIdentifier.IsValid(reference.RequestVersionId)
            && IsSha256(reference.RequestHash);

    private static bool ResponseReferenceIsWithin(HumanInputResponseReference? reference)
        => reference is not null
            && reference.SchemaVersion == HumanInputResponseReference.CurrentSchemaVersion
            && HumanInputIdentifier.IsValid(reference.ResponseId)
            && RequestReferenceIsWithin(reference.Request)
            && IsSha256(reference.ValueHash)
            && IsSha256(reference.ResponseHash);

    private static bool BindingIsWithin(HumanInputRequestBinding? binding)
        => binding is not null
            && ContextualRoleWorkspaceId.IsValid(binding.WorkspaceId)
            && HumanInputIdentifier.IsValid(binding.LoopGraphId)
            && HumanInputIdentifier.IsValid(binding.LoopRevisionId)
            && HumanInputIdentifier.IsValid(binding.NodeId)
            && HumanInputIdentifier.IsValid(binding.RunId)
            && HumanInputIdentifier.IsValid(binding.CheckpointId);

    private static void WriteRequestReference(Utf8JsonWriter writer, HumanInputRequestReference reference)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", reference.SchemaVersion);
        writer.WriteString("requestId", reference.RequestId);
        writer.WriteString("requestVersionId", reference.RequestVersionId);
        writer.WriteString("requestHash", reference.RequestHash);
        writer.WriteEndObject();
    }

    private static void WriteBinding(Utf8JsonWriter writer, HumanInputRequestBinding binding)
    {
        writer.WriteStartObject();
        writer.WriteString("workspaceId", binding.WorkspaceId);
        writer.WriteString("loopGraphId", binding.LoopGraphId);
        writer.WriteString("loopRevisionId", binding.LoopRevisionId);
        writer.WriteString("nodeId", binding.NodeId);
        writer.WriteString("runId", binding.RunId);
        writer.WriteString("checkpointId", binding.CheckpointId);
        writer.WriteEndObject();
    }

    private static void WriteResponseReference(Utf8JsonWriter writer, HumanInputResponseReference reference)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", reference.SchemaVersion);
        writer.WriteString("responseId", reference.ResponseId);
        writer.WritePropertyName("request");
        WriteRequestReference(writer, reference.Request);
        writer.WriteString("valueHash", reference.ValueHash);
        writer.WriteString("responseHash", reference.ResponseHash);
        writer.WriteEndObject();
    }

    private static void WriteString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static bool IsSha256(string? value)
        => value is { Length: HumanInputLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
