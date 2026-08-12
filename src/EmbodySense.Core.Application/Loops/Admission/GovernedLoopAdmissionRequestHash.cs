using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Admission;

/// <summary>Computes the canonical hash of every caller-stable governed-loop admission coordinate.</summary>
public static class GovernedLoopAdmissionRequestHash
{
    /// <summary>Computes a lowercase SHA-256 digest without trusting the request's supplied digest field.</summary>
    /// <param name="request">The exact server-prepared request.</param>
    /// <returns>The canonical 64-character lowercase digest.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when request text contains malformed UTF-16.</exception>
    public static string Compute(GovernedLoopAdmissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("contract", "governed-loop-admission-request-v1");
            writer.WriteNumber("schemaVersion", request.SchemaVersion);
            writer.WriteString("operationId", Normalize(request.OperationId));
            writer.WriteString("invocationPayloadHash", Normalize(request.InvocationPayloadHash));
            writer.WritePropertyName("publication");
            WritePublication(writer, request.Publication);
            writer.WritePropertyName("authorityGrant");
            WriteGrant(writer, request.AuthorityGrant);
            writer.WriteString("actorId", Normalize(request.ActorId?.Value));
            writer.WriteString("surface", Normalize(request.Surface));
            writer.WriteEndObject();
            writer.Flush();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    /// <summary>Returns a request copy carrying its canonical server-computed request hash.</summary>
    public static GovernedLoopAdmissionRequest Apply(GovernedLoopAdmissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request with { RequestHash = Compute(request) };
    }

    /// <summary>Gets whether the request retains its exact canonical server-computed request hash.</summary>
    public static bool Matches(GovernedLoopAdmissionRequest? request)
    {
        if (request?.RequestHash is not { Length: 64 } actual)
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Compute(request)),
                Encoding.ASCII.GetBytes(actual));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void WritePublication(Utf8JsonWriter writer, GovernedLoopRevisionPublicationPin? publication)
    {
        if (publication is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", publication.SchemaVersion);
        writer.WritePropertyName("revision");
        if (publication.Revision is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", publication.Revision.SchemaVersion);
            writer.WriteString("graphId", Normalize(publication.Revision.GraphId));
            writer.WriteString("revisionId", Normalize(publication.Revision.RevisionId));
            writer.WriteString("executableHash", Normalize(publication.Revision.ExecutableHash));
            writer.WriteEndObject();
        }

        writer.WriteString("publicationOperationId", Normalize(publication.PublicationOperationId));
        writer.WriteString("validationEvidenceHash", Normalize(publication.ValidationEvidenceHash));
        writer.WriteEndObject();
    }

    private static void WriteGrant(Utf8JsonWriter writer, AuthorityGrantReference? grant)
    {
        if (grant is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("grantId", Normalize(grant.GrantId?.Value));
        writer.WriteNumber("revision", grant.Revision?.Value ?? 0);
        writer.WriteString("contentHash", Normalize(grant.ContentHash));
        writer.WriteEndObject();
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
                    throw new ArgumentException("Admission request text must contain well-formed UTF-16.", nameof(value));
                }

                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                throw new ArgumentException("Admission request text must contain well-formed UTF-16.", nameof(value));
            }
        }

        return value.Normalize(NormalizationForm.FormC);
    }
}
