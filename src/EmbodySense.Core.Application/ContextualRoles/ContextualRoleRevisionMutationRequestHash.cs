using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbodySense.Core.Application.ContextualRoles;

/// <summary>Computes canonical hashes that bind a stable operation id to one exact contextual-role mutation intent.</summary>
public static class ContextualRoleRevisionMutationRequestHash
{
    /// <summary>Computes a lowercase SHA-256 digest over every mutation field except the digest itself.</summary>
    /// <param name="request">The mutation request to hash.</param>
    /// <returns>The canonical 64-character lowercase digest.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when request text contains malformed UTF-16 or an identifier array is uninitialized.</exception>
    public static string Compute(ContextualRoleRevisionMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("operationId", Normalize(request.OperationId));
            writer.WriteString("kind", request.Kind.ToString());
            writer.WriteString("roleId", Normalize(request.RoleId));
            writer.WriteString("actorId", Normalize(request.ActorId));
            WriteIdentity(writer, "expectedPreviousIdentity", request.ExpectedPreviousIdentity);
            writer.WriteString("requestedAtUtc", request.RequestedAtUtc);
            WriteRevision(writer, request.Revision);
            writer.WriteEndObject();
            writer.Flush();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    /// <summary>Returns a copy whose request hash matches its exact canonical mutation intent.</summary>
    /// <param name="request">The mutation request to hash.</param>
    /// <returns>A request with its canonical hash applied.</returns>
    public static ContextualRoleRevisionMutationRequest Apply(ContextualRoleRevisionMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request with { RequestHash = Compute(request) };
    }

    /// <summary>Determines whether a request records its exact canonical mutation digest.</summary>
    /// <param name="request">The request to verify.</param>
    /// <returns><see langword="true"/> only when the digest matches in fixed time.</returns>
    public static bool Matches(ContextualRoleRevisionMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var expected = Encoding.ASCII.GetBytes(Compute(request));
        var actual = Encoding.ASCII.GetBytes(request.RequestHash ?? string.Empty);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static void WriteRevision(Utf8JsonWriter writer, ContextualRoleRevision? revision)
    {
        writer.WritePropertyName("revision");
        if (revision is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", revision.SchemaVersion);
        WriteIdentity(writer, "identity", revision.Identity);
        writer.WriteString("contentHash", Normalize(revision.ContentHash));
        writer.WriteString("displayName", Normalize(revision.DisplayName));
        writer.WriteString("purpose", Normalize(revision.Purpose));
        writer.WriteString("status", revision.Status.ToString());
        writer.WritePropertyName("provenance");
        writer.WriteStartObject();
        writer.WriteString("authorId", Normalize(revision.Provenance?.AuthorId));
        writer.WriteString("createdAtUtc", revision.Provenance?.CreatedAtUtc ?? default);
        writer.WriteString("recordedAtUtc", revision.Provenance?.RecordedAtUtc ?? default);
        writer.WriteEndObject();
        WriteIdentifiers(writer, "workspaceIds", revision.WorkspaceApplicability?.WorkspaceIds ?? []);
        writer.WritePropertyName("instructionSource");
        writer.WriteStartObject();
        writer.WriteString("kind", revision.InstructionSource?.Kind.ToString());
        writer.WriteString("referenceId", Normalize(revision.InstructionSource?.ReferenceId));
        writer.WriteString("classification", revision.InstructionSource?.Classification.ToString());
        writer.WriteEndObject();
        WriteIdentifiers(writer, "capabilityIds", revision.PolicyMaxima?.CapabilityIds ?? []);
        writer.WriteEndObject();
    }

    private static void WriteIdentity(Utf8JsonWriter writer, string propertyName, ContextualRoleRevisionIdentity? identity)
    {
        writer.WritePropertyName(propertyName);
        if (identity is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("roleId", Normalize(identity.RoleId));
        writer.WriteNumber("revision", identity.Revision);
        writer.WriteEndObject();
    }

    private static void WriteIdentifiers(Utf8JsonWriter writer, string propertyName, System.Collections.Immutable.ImmutableArray<string> values)
    {
        if (values.IsDefault)
        {
            throw new ArgumentException("Contextual-role mutation identifier collections must be initialized.", propertyName);
        }

        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values.Order(StringComparer.Ordinal))
        {
            writer.WriteStringValue(Normalize(value));
        }

        writer.WriteEndArray();
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
                    throw new ArgumentException("Contextual-role mutation text must contain well-formed UTF-16.", nameof(value));
                }

                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                throw new ArgumentException("Contextual-role mutation text must contain well-formed UTF-16.", nameof(value));
            }
        }

        return value.Normalize(NormalizationForm.FormC);
    }
}
