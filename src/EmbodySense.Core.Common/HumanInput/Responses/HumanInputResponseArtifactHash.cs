using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Common.HumanInput.Responses;

/// <summary>Computes, applies, and verifies the canonical digest for immutable authenticated response artifacts.</summary>
public static class HumanInputResponseArtifactHash
{
    /// <summary>Computes a lowercase SHA-256 digest over every behavior-affecting response artifact field.</summary>
    /// <param name="artifact">The response artifact.</param>
    /// <returns>The canonical 64-character lowercase digest.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="artifact"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown before serialization when an artifact value is malformed or exceeds schema-1 bounds.</exception>
    public static string Compute(HumanInputResponseArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!IsBounded(artifact))
        {
            throw new ArgumentException("Human Input response artifact is malformed or exceeds canonical schema-1 bounds.", nameof(artifact));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", artifact.SchemaVersion);
            HumanInputResponseCanonicalWriter.WriteString(writer, "responseId", artifact.ResponseId);
            writer.WritePropertyName("request");
            HumanInputResponseCanonicalWriter.WriteRequestReference(writer, artifact.Request);
            writer.WritePropertyName("binding");
            HumanInputResponseCanonicalWriter.WriteBinding(writer, artifact.Binding);
            HumanInputResponseCanonicalWriter.WriteString(writer, "actorId", artifact.ActorId?.Value);
            HumanInputResponseCanonicalWriter.WriteString(writer, "respondentRoleId", artifact.RespondentRoleId);
            HumanInputResponseCanonicalWriter.WriteUtc(writer, "submittedAtUtc", artifact.SubmittedAtUtc);
            writer.WriteNumber("privacyClass", (int)artifact.PrivacyClass);
            HumanInputResponseCanonicalWriter.WriteString(writer, "valueHash", artifact.ValueHash);
            HumanInputResponseCanonicalWriter.WriteString(writer, "explanation", artifact.Explanation);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    /// <summary>Returns an artifact copy with its canonical value and full-artifact hashes applied.</summary>
    /// <param name="artifact">The response artifact candidate.</param>
    /// <returns>The artifact with both canonical hashes.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="artifact"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown before serialization when an artifact value is malformed or exceeds schema-1 bounds.</exception>
    public static HumanInputResponseArtifact Apply(HumanInputResponseArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var withValueHash = artifact with { ValueHash = HumanInputResponseValueHash.Compute(artifact.Value), ResponseHash = string.Empty };
        return withValueHash with { ResponseHash = Compute(withValueHash) };
    }

    /// <summary>Determines whether both stored response digests match the exact artifact.</summary>
    /// <param name="artifact">The artifact to verify.</param>
    /// <returns><see langword="true"/> when both hashes match in fixed time; otherwise, <see langword="false"/>.</returns>
    public static bool Matches(HumanInputResponseArtifact? artifact)
    {
        if (artifact is null || !IsBounded(artifact) || !HumanInputResponseHashRules.IsSha256(artifact.ResponseHash))
        {
            return false;
        }

        try
        {
            return HumanInputResponseValueHash.Matches(artifact.Value, artifact.ValueHash)
                && HumanInputResponseHashRules.FixedEquals(Compute(artifact), artifact.ResponseHash);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IndexOutOfRangeException or NullReferenceException)
        {
            return false;
        }
    }

    internal static bool IsBounded(HumanInputResponseArtifact artifact)
    {
        if (!HumanInputIdentifier.IsValid(artifact.ResponseId)
            || artifact.Request is null
            || !HumanInputIdentifier.IsValid(artifact.Request.RequestId)
            || !HumanInputIdentifier.IsValid(artifact.Request.RequestVersionId)
            || !HumanInputResponseHashRules.IsSha256(artifact.Request.RequestHash)
            || artifact.Binding is null
            || !HumanInputIdentifier.IsValid(artifact.Binding.WorkspaceId)
            || !HumanInputIdentifier.IsValid(artifact.Binding.LoopGraphId)
            || !HumanInputIdentifier.IsValid(artifact.Binding.LoopRevisionId)
            || !HumanInputIdentifier.IsValid(artifact.Binding.NodeId)
            || !HumanInputIdentifier.IsValid(artifact.Binding.RunId)
            || !HumanInputIdentifier.IsValid(artifact.Binding.CheckpointId)
            || artifact.ActorId is null
            || !AuthorityActorId.TryParse(artifact.ActorId.Value, out _, out _)
            || !HumanInputIdentifier.IsValid(artifact.RespondentRoleId)
            || artifact.Explanation is { } explanation && !HumanInputText.IsValid(explanation, HumanInputLimits.MaxExplanationCharacters, required: false)
            || !HumanInputResponseHashRules.IsSha256(artifact.ValueHash)
            || artifact.Value is null)
        {
            return false;
        }
        return HumanInputResponseValueHash.IsBounded(artifact.Value);
    }
}
