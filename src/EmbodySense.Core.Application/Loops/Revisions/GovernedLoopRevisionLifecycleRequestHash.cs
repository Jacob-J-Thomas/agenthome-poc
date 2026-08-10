using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbodySense.Core.Application.Loops.Revisions;

/// <summary>Computes the canonical server-owned hash that binds an operation identifier to one exact lifecycle request.</summary>
public static class GovernedLoopRevisionLifecycleRequestHash
{
    /// <summary>Computes a lowercase SHA-256 digest over every lifecycle request field.</summary>
    /// <param name="request">The exact request to hash.</param>
    /// <returns>The canonical 64-character lowercase digest.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when request text contains malformed UTF-16.</exception>
    public static string Compute(GovernedLoopRevisionLifecycleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", request.SchemaVersion);
            writer.WriteString("operationId", Normalize(request.OperationId));
            writer.WriteString("kind", request.Kind.ToString());
            writer.WriteString("graphId", Normalize(request.GraphId));
            writer.WriteString("actorId", Normalize(request.ActorId?.Value));
            writer.WriteString("expectedLifecycleStatus", request.ExpectedLifecycleStatus.ToString());
            writer.WriteNumber("expectedLifecycleVersion", request.ExpectedLifecycleVersion);
            WriteRevision(writer, "expectedDraftRevision", request.ExpectedDraftRevision);
            WritePublication(writer, "expectedPublishedRevision", request.ExpectedPublishedRevision);
            WriteRevision(writer, "candidateRevision", request.CandidateRevision);
            WriteRevision(writer, "targetRevision", request.TargetRevision);
            WritePublication(writer, "rollbackSourcePublication", request.RollbackSourcePublication);
            writer.WriteEndObject();
            writer.Flush();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    private static void WritePublication(Utf8JsonWriter writer, string propertyName, GovernedLoopRevisionPublicationPin? publication)
    {
        writer.WritePropertyName(propertyName);
        if (publication is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", publication.SchemaVersion);
        WriteRevision(writer, "revision", publication.Revision);
        writer.WriteString("publicationOperationId", Normalize(publication.PublicationOperationId));
        writer.WriteString("validationEvidenceHash", Normalize(publication.ValidationEvidenceHash));
        writer.WriteEndObject();
    }

    private static void WriteRevision(Utf8JsonWriter writer, string propertyName, GovernedLoopRevisionReference? revision)
    {
        writer.WritePropertyName(propertyName);
        if (revision is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", revision.SchemaVersion);
        writer.WriteString("graphId", Normalize(revision.GraphId));
        writer.WriteString("revisionId", Normalize(revision.RevisionId));
        writer.WriteString("executableHash", Normalize(revision.ExecutableHash));
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
                    throw new ArgumentException("Lifecycle request text must contain well-formed UTF-16.", nameof(value));
                }

                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                throw new ArgumentException("Lifecycle request text must contain well-formed UTF-16.", nameof(value));
            }
        }

        return value.Normalize(NormalizationForm.FormC);
    }
}
