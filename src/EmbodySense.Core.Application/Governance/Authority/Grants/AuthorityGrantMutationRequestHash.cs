using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Loops.Revisions;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbodySense.Core.Application.Governance.Authority.Grants;

/// <summary>Computes the canonical hash binding a workspace-global operation identity to exact grant intent.</summary>
public static class AuthorityGrantMutationRequestHash
{
    /// <summary>Computes a lowercase SHA-256 digest over every mutation-request field except the supplied hash.</summary>
    /// <param name="request">The exact request to hash.</param>
    /// <returns>The canonical 64-character lowercase digest.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when the request shape is incomplete, out of bounds, or contains malformed UTF-16.</exception>
    public static string Compute(AuthorityGrantMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireBoundedShape(request);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", request.SchemaVersion);
            writer.WriteString("operationId", Normalize(request.OperationId));
            writer.WriteString("kind", request.Kind.ToString());
            writer.WriteString("grantId", Normalize(request.GrantId?.Value));
            writer.WriteNumber("expectedRevision", request.ExpectedRevision);
            writer.WriteString("expectedStatus", request.ExpectedStatus.ToString());
            WriteBinding(writer, request.CandidateBinding);
            WriteCeiling(writer, request.CandidateCeiling);
            WriteBoundary(writer, request.CandidateBoundary);
            writer.WriteString("actorId", Normalize(request.ActorId?.Value));
            writer.WriteString("reason", Normalize(request.Reason?.Value));
            writer.WriteEndObject();
            writer.Flush();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    /// <summary>Returns a copy carrying its canonical exact-intent hash.</summary>
    public static AuthorityGrantMutationRequest Apply(AuthorityGrantMutationRequest request) => request with { RequestHash = Compute(request) };

    /// <summary>Gets whether the supplied request hash matches all exact intent fields.</summary>
    public static bool Matches(AuthorityGrantMutationRequest? request)
    {
        if (request is null || !IsSha256(request.RequestHash))
        {
            return false;
        }

        try
        {
            return string.Equals(request.RequestHash, Compute(request), StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void WriteBinding(Utf8JsonWriter writer, AuthorityGrantBinding? binding)
    {
        writer.WritePropertyName("candidateBinding");
        if (binding is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("profileId", Normalize(binding.Profile?.Reference?.ProfileId?.Value));
        writer.WriteNumber("profileRevision", binding.Profile?.Reference?.Revision?.Value ?? 0);
        writer.WriteString("profileHash", Normalize(binding.Profile?.ContentHash?.Value));
        writer.WriteString("roleId", Normalize(binding.Role?.Identity?.RoleId));
        writer.WriteNumber("roleRevision", binding.Role?.Identity?.Revision ?? 0);
        writer.WriteString("roleHash", Normalize(binding.Role?.ContentHash));
        writer.WriteNumber("loopSchemaVersion", binding.Loop?.SchemaVersion ?? 0);
        writer.WriteString("loopGraphId", Normalize(binding.Loop?.Revision?.GraphId));
        writer.WriteString("loopRevisionId", Normalize(binding.Loop?.Revision?.RevisionId));
        writer.WriteString("loopExecutableHash", Normalize(binding.Loop?.Revision?.ExecutableHash));
        writer.WriteString("loopPublicationOperationId", Normalize(binding.Loop?.PublicationOperationId));
        writer.WriteString("loopValidationEvidenceHash", Normalize(binding.Loop?.ValidationEvidenceHash));
        writer.WriteEndObject();
    }

    private static void WriteCeiling(Utf8JsonWriter writer, AuthorityCeiling? ceiling)
    {
        writer.WritePropertyName("candidateCeiling");
        if (ceiling is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (ceiling.Capabilities is null || ceiling.DataClasses is null)
        {
            throw new ArgumentException("Grant mutation ceiling collections must be initialized.", nameof(ceiling));
        }

        foreach (var capability in ceiling.Capabilities)
        {
            if (capability?.Id is null || capability.Version is null || capability.Hash is null)
            {
                throw new ArgumentException("Grant mutation capability identities must be complete.", nameof(ceiling));
            }
        }

        if (ceiling.DataClasses.Any(dataClass => dataClass is null))
        {
            throw new ArgumentException("Grant mutation data classes must be complete.", nameof(ceiling));
        }

        writer.WriteStartObject();
        writer.WriteStartArray("capabilities");
        foreach (var capability in ceiling.Capabilities.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ThenBy(value => value.Version.Value, StringComparer.Ordinal).ThenBy(value => value.Hash.Value, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("id", Normalize(capability.Id.Value));
            writer.WriteString("version", Normalize(capability.Version.Value));
            writer.WriteString("hash", Normalize(capability.Hash.Value));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("dataClasses");
        foreach (var dataClass in ceiling.DataClasses.OrderBy(value => value.Value, StringComparer.Ordinal))
        {
            writer.WriteStringValue(Normalize(dataClass.Value));
        }

        writer.WriteEndArray();
        writer.WriteNumber("maxTargetCount", ceiling.MaxTargetCount);
        writer.WriteString("maxSideEffectClass", ceiling.MaxSideEffectClass.ToString());
        writer.WriteBoolean("allowsRecurrence", ceiling.AllowsRecurrence);
        writer.WriteBoolean("allowsExternalPublication", ceiling.AllowsExternalPublication);
        writer.WriteBoolean("allowsIrreversibleAction", ceiling.AllowsIrreversibleAction);
        writer.WriteEndObject();
    }

    private static void WriteBoundary(Utf8JsonWriter writer, AuthorityGrantBoundary? boundary)
    {
        writer.WritePropertyName("candidateBoundary");
        if (boundary is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("effectiveAtUtc", boundary.EffectiveAtUtc);
        if (boundary.ExpiresAtUtc is { } expiry)
        {
            writer.WriteString("expiresAtUtc", expiry);
        }
        else
        {
            writer.WriteNull("expiresAtUtc");
        }

        writer.WriteString("completionConstraint", boundary.CompletionConstraint.ToString());
        writer.WriteEndObject();
    }

    private static void RequireBoundedShape(AuthorityGrantMutationRequest request)
    {
        if (!IsBounded(request.OperationId, AuthorityGrantContractLimits.MaxOperationIdCharacters)
            || !IsBounded(request.GrantId?.Value, AuthorityGrantContractLimits.MaxGrantIdCharacters)
            || !IsBounded(request.ActorId?.Value, AuthorityContractLimits.MaxActorIdCharacters)
            || !IsBounded(request.Reason?.Value, AuthorityContractLimits.MaxPurposeCharacters))
        {
            throw new ArgumentException("Grant mutation text exceeds its schema bound or is incomplete.", nameof(request));
        }

        if (request.CandidateBinding is { } binding
            && (!IsBounded(binding.Profile?.Reference?.ProfileId?.Value, AuthorityContractLimits.MaxProfileIdCharacters)
                || binding.Profile?.Reference?.Revision is null
                || !IsPrefixedSha256(binding.Profile?.ContentHash?.Value)
                || !IsBounded(binding.Role?.Identity?.RoleId, ContextualRoleLimits.MaxIdentifierCharacters)
                || binding.Role!.Identity!.Revision < 1
                || !IsSha256(binding.Role.ContentHash)
                || binding.Loop?.Revision is null
                || !IsBounded(binding.Loop.Revision.GraphId, GovernedLoopRevisionContractLimits.MaxIdentifierCharacters)
                || !IsBounded(binding.Loop.Revision.RevisionId, GovernedLoopRevisionContractLimits.MaxIdentifierCharacters)
                || !IsSha256(binding.Loop.Revision.ExecutableHash)
                || !IsBounded(binding.Loop.PublicationOperationId, GovernedLoopRevisionContractLimits.MaxIdentifierCharacters)
                || !IsSha256(binding.Loop.ValidationEvidenceHash)))
        {
            throw new ArgumentException("Grant mutation binding exceeds its schema bound or is incomplete.", nameof(request));
        }

        if (request.CandidateCeiling is not { } ceiling)
        {
            return;
        }

        if (ceiling.Capabilities is null
            || ceiling.DataClasses is null
            || ceiling.Capabilities.Count > AuthorityContractLimits.MaxCapabilitiesPerCeiling
            || ceiling.DataClasses.Count > AuthorityContractLimits.MaxDataClassesPerCeiling)
        {
            throw new ArgumentException("Grant mutation ceiling exceeds its schema bound or is incomplete.", nameof(request));
        }

        foreach (var capability in ceiling.Capabilities)
        {
            if (capability?.Id is null || capability.Version is null || capability.Hash is null)
            {
                throw new ArgumentException("Grant mutation capability identities must be complete.", nameof(request));
            }
        }

        if (ceiling.DataClasses.Any(value => value is null))
        {
            throw new ArgumentException("Grant mutation data classes must be complete.", nameof(request));
        }
    }

    private static string? Normalize(string? value)
    {
        if (value is null)
        {
            return null;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    throw new ArgumentException("Grant mutation text must contain well-formed UTF-16.", nameof(value));
                }

                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                throw new ArgumentException("Grant mutation text must contain well-formed UTF-16.", nameof(value));
            }
        }

        return value.Normalize(NormalizationForm.FormC);
    }

    private static bool IsBounded(string? value, int maximumLength) => value is { Length: > 0 } && value.Length <= maximumLength;

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsPrefixedSha256(string? value)
        => value is { Length: 71 }
            && value.StartsWith("sha256:", StringComparison.Ordinal)
            && IsSha256(value[7..]);
}
