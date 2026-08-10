using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Common.Authority.Grants;

/// <summary>Computes and applies the canonical content hash of one immutable authority-grant revision.</summary>
public static class AuthorityGrantHash
{
    private const string Prefix = "sha256:";

    /// <summary>Computes the canonical hash after validating every field other than the supplied hash.</summary>
    public static string Compute(AuthorityGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var validation = AuthorityGrantContractValidator.ValidateForHash(grant);
        if (!validation.IsValid)
        {
            throw new ArgumentException($"Authority grant is invalid at {validation.Errors[0].Path}.", nameof(grant));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", grant.SchemaVersion);
            writer.WriteString("grantId", grant.GrantId.Value);
            writer.WriteNumber("revision", grant.Revision.Value);
            if (grant.PredecessorRevision is null)
            {
                writer.WriteNull("predecessorRevision");
            }
            else
            {
                writer.WriteNumber("predecessorRevision", grant.PredecessorRevision.Value);
            }

            writer.WriteString("predecessorContentHash", grant.PredecessorContentHash);
            writer.WriteString("status", grant.Status.ToString());
            WriteBinding(writer, grant.Binding);
            WriteCeiling(writer, grant.RequestedCeiling);
            writer.WriteStartObject("boundary");
            writer.WriteString("effectiveAtUtc", grant.Boundary.EffectiveAtUtc);
            if (grant.Boundary.ExpiresAtUtc is { } expiresAtUtc)
            {
                writer.WriteString("expiresAtUtc", expiresAtUtc);
            }
            else
            {
                writer.WriteNull("expiresAtUtc");
            }
            writer.WriteString("completionConstraint", grant.Boundary.CompletionConstraint.ToString());
            writer.WriteEndObject();
            writer.WriteString("changedByActorId", grant.ChangedByActorId.Value);
            writer.WriteString("reason", grant.Reason.Value);
            writer.WriteString("recordedAtUtc", grant.RecordedAtUtc);
            writer.WriteEndObject();
            writer.Flush();
        }

        return Prefix + Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    /// <summary>Returns an immutable copy carrying its canonical content hash.</summary>
    public static AuthorityGrant Apply(AuthorityGrant grant) => grant with { ContentHash = Compute(grant) };

    /// <summary>Gets whether the supplied hash exactly matches canonical immutable content.</summary>
    public static bool Matches(AuthorityGrant? grant)
    {
        if (grant is null || !IsCanonical(grant.ContentHash))
        {
            return false;
        }

        try
        {
            return string.Equals(grant.ContentHash, Compute(grant), StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static bool IsCanonical(string? value)
        => value?.Length == Prefix.Length + 64
            && value.StartsWith(Prefix, StringComparison.Ordinal)
            && value[Prefix.Length..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void WriteBinding(Utf8JsonWriter writer, AuthorityGrantBinding binding)
    {
        writer.WriteStartObject("binding");
        writer.WriteStartObject("profile");
        writer.WriteString("profileId", binding.Profile.Reference.ProfileId.Value);
        writer.WriteNumber("revision", binding.Profile.Reference.Revision.Value);
        writer.WriteString("contentHash", binding.Profile.ContentHash.Value);
        writer.WriteEndObject();
        writer.WriteStartObject("role");
        writer.WriteString("roleId", binding.Role.Identity.RoleId);
        writer.WriteNumber("revision", binding.Role.Identity.Revision);
        writer.WriteString("contentHash", binding.Role.ContentHash);
        writer.WriteEndObject();
        writer.WriteStartObject("loop");
        writer.WriteNumber("schemaVersion", binding.Loop.SchemaVersion);
        writer.WriteString("graphId", binding.Loop.Revision.GraphId);
        writer.WriteString("revisionId", binding.Loop.Revision.RevisionId);
        writer.WriteString("executableHash", binding.Loop.Revision.ExecutableHash);
        writer.WriteString("publicationOperationId", binding.Loop.PublicationOperationId);
        writer.WriteString("validationEvidenceHash", binding.Loop.ValidationEvidenceHash);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteCeiling(Utf8JsonWriter writer, AuthorityCeiling ceiling)
    {
        writer.WriteStartObject("requestedCeiling");
        writer.WriteStartArray("capabilities");
        foreach (var capability in ceiling.Capabilities.OrderBy(identity => identity.Id.Value, StringComparer.Ordinal).ThenBy(identity => identity.Version.Value, StringComparer.Ordinal).ThenBy(identity => identity.Hash.Value, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("id", capability.Id.Value);
            writer.WriteString("version", capability.Version.Value);
            writer.WriteString("hash", capability.Hash.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("dataClasses");
        foreach (var dataClass in ceiling.DataClasses.OrderBy(value => value.Value, StringComparer.Ordinal))
        {
            writer.WriteStringValue(dataClass.Value);
        }

        writer.WriteEndArray();
        writer.WriteNumber("maxTargetCount", ceiling.MaxTargetCount);
        writer.WriteString("maxSideEffectClass", ceiling.MaxSideEffectClass.ToString());
        writer.WriteBoolean("allowsRecurrence", ceiling.AllowsRecurrence);
        writer.WriteBoolean("allowsExternalPublication", ceiling.AllowsExternalPublication);
        writer.WriteBoolean("allowsIrreversibleAction", ceiling.AllowsIrreversibleAction);
        writer.WriteEndObject();
    }
}
