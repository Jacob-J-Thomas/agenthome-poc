using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Common.HumanInput.Responses;

/// <summary>Computes and verifies the canonical digest binding response eligibility evidence to exact authenticated context.</summary>
public static class HumanInputResponseEligibilityEvidenceHash
{
    /// <summary>Computes the canonical eligibility digest for one exact authenticated response-operation context.</summary>
    /// <param name="workspaceId">The exact trusted workspace identifier.</param>
    /// <param name="operationId">The exact idempotent response-operation identifier.</param>
    /// <param name="commandHash">The canonical exact-intent command digest.</param>
    /// <param name="request">The exact immutable request reference evaluated by policy.</param>
    /// <param name="actorId">The authenticated actor evaluated by policy.</param>
    /// <param name="actorRoleId">The trusted eligible role, or null when eligibility could not establish one.</param>
    /// <param name="authenticationEvidenceHash">The server-owned authentication evidence digest.</param>
    /// <param name="evaluatedAtUtc">The trusted UTC eligibility-evaluation time.</param>
    /// <returns>The canonical 64-character lowercase SHA-256 digest.</returns>
    /// <exception cref="ArgumentException">Thrown before serialization when an input is malformed or exceeds schema-1 bounds.</exception>
    public static string Compute(
        string workspaceId,
        string operationId,
        string commandHash,
        HumanInputRequestReference request,
        AuthorityActorId actorId,
        string? actorRoleId,
        string authenticationEvidenceHash,
        DateTimeOffset evaluatedAtUtc)
    {
        if (!ContextualRoleWorkspaceId.IsValid(workspaceId))
        {
            throw new ArgumentException("A canonical bounded workspace identifier is required.", nameof(workspaceId));
        }
        if (!HumanInputIdentifier.IsValid(operationId))
        {
            throw new ArgumentException("A canonical bounded operation identifier is required.", nameof(operationId));
        }
        if (!HumanInputResponseHashRules.IsSha256(commandHash))
        {
            throw new ArgumentException("A canonical lowercase SHA-256 command digest is required.", nameof(commandHash));
        }
        if (request is null || !HumanInputRequestLifecycleValidator.ValidateReference(request).IsValid)
        {
            throw new ArgumentException("A canonical exact immutable request reference is required.", nameof(request));
        }
        if (actorId is null || !AuthorityActorId.TryParse(actorId.Value, out _, out _))
        {
            throw new ArgumentException("A canonical authenticated actor identifier is required.", nameof(actorId));
        }
        if (actorRoleId is not null && !HumanInputIdentifier.IsValid(actorRoleId))
        {
            throw new ArgumentException("The actor role identifier must be null or canonical and bounded.", nameof(actorRoleId));
        }
        if (!HumanInputResponseHashRules.IsSha256(authenticationEvidenceHash))
        {
            throw new ArgumentException("A canonical lowercase SHA-256 authentication evidence digest is required.", nameof(authenticationEvidenceHash));
        }
        if (evaluatedAtUtc == default || evaluatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A non-default UTC eligibility-evaluation time is required.", nameof(evaluatedAtUtc));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", HumanInputResponseContractLimits.CurrentSchemaVersion);
            HumanInputResponseCanonicalWriter.WriteString(writer, "workspaceId", workspaceId);
            HumanInputResponseCanonicalWriter.WriteString(writer, "operationId", operationId);
            HumanInputResponseCanonicalWriter.WriteString(writer, "commandHash", commandHash);
            writer.WritePropertyName("request");
            HumanInputResponseCanonicalWriter.WriteRequestReference(writer, request);
            HumanInputResponseCanonicalWriter.WriteString(writer, "actorId", actorId.Value);
            HumanInputResponseCanonicalWriter.WriteString(writer, "actorRoleId", actorRoleId);
            HumanInputResponseCanonicalWriter.WriteString(writer, "authenticationEvidenceHash", authenticationEvidenceHash);
            HumanInputResponseCanonicalWriter.WriteUtc(writer, "evaluatedAtUtc", evaluatedAtUtc);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    /// <summary>Verifies the exact canonical eligibility digest over every retained authority-bearing input.</summary>
    /// <param name="evidence">The retained response-operation evidence.</param>
    /// <returns><see langword="true"/> only when the authority-bearing inputs are structurally canonical and their digest matches in fixed time.</returns>
    public static bool Matches(HumanInputResponseOperationEvidence? evidence)
    {
        if (!HasCanonicalInputs(evidence))
        {
            return false;
        }

        try
        {
            var expected = Compute(
                evidence!.ExpectedBinding.WorkspaceId,
                evidence.OperationId,
                evidence.CommandHash,
                evidence.Request,
                evidence.ActorId,
                evidence.ActorRoleId,
                evidence.AuthenticationEvidenceHash,
                evidence.RecordedAtUtc);
            return HumanInputResponseHashRules.FixedEquals(expected, evidence.EligibilityEvidenceHash);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IndexOutOfRangeException or NullReferenceException)
        {
            return false;
        }
    }

    private static bool HasCanonicalInputs(HumanInputResponseOperationEvidence? evidence)
        => evidence is not null
            && evidence.SchemaVersion == HumanInputResponseOperationEvidence.CurrentSchemaVersion
            && HumanInputIdentifier.IsValid(evidence.OperationId)
            && HumanInputResponseHashRules.IsSha256(evidence.CommandHash)
            && evidence.Request is not null
            && HumanInputRequestLifecycleValidator.ValidateReference(evidence.Request).IsValid
            && evidence.ExpectedBinding is not null
            && ContextualRoleWorkspaceId.IsValid(evidence.ExpectedBinding.WorkspaceId)
            && evidence.ActorId is not null
            && AuthorityActorId.TryParse(evidence.ActorId.Value, out _, out _)
            && (evidence.ActorRoleId is null || HumanInputIdentifier.IsValid(evidence.ActorRoleId))
            && HumanInputResponseHashRules.IsSha256(evidence.AuthenticationEvidenceHash)
            && HumanInputResponseHashRules.IsSha256(evidence.EligibilityEvidenceHash)
            && evidence.RecordedAtUtc != default
            && evidence.RecordedAtUtc.Offset == TimeSpan.Zero;
}
