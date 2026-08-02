using EmbodySense.Core.Common.ContextualRoles.Models;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbodySense.Core.Common.ContextualRoles;

/// <summary>Computes and verifies canonical semantic content hashes for immutable contextual-role revisions.</summary>
public static class ContextualRoleRevisionContentHash
{
    /// <summary>Computes the lowercase SHA-256 hash of the semantic revision content, excluding display and provenance metadata.</summary>
    /// <param name="revision">The revision to serialize canonically.</param>
    /// <returns>A 64-character lowercase hexadecimal SHA-256 digest.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="revision"/> is <see langword="null"/>.</exception>
    public static string Compute(ContextualRoleRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", revision.SchemaVersion);
            writer.WriteString("roleId", Normalize(revision.Identity?.RoleId));
            writer.WriteNumber("revision", revision.Identity?.Revision ?? 0);
            writer.WriteString("purpose", Normalize(revision.Purpose));
            writer.WriteString("status", revision.Status.ToString());
            WriteWorkspaceApplicability(writer, revision.WorkspaceApplicability);
            WriteInstructionSource(writer, revision.InstructionSource);
            WritePolicyMaxima(writer, revision.PolicyMaxima);
            writer.WriteEndObject();
            writer.Flush();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    /// <summary>Returns a copy with the canonical content hash applied.</summary>
    /// <param name="revision">The revision to hash.</param>
    /// <returns>A revision whose content hash matches its semantic content.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="revision"/> is <see langword="null"/>.</exception>
    public static ContextualRoleRevision Apply(ContextualRoleRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        return revision with { ContentHash = Compute(revision) };
    }

    /// <summary>Determines whether the recorded digest matches the canonical semantic content.</summary>
    /// <param name="revision">The revision to verify.</param>
    /// <returns><see langword="true"/> when the exact lowercase digest matches using fixed-time comparison.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="revision"/> is <see langword="null"/>.</exception>
    public static bool Matches(ContextualRoleRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        var expected = Encoding.ASCII.GetBytes(Compute(revision));
        var actual = Encoding.ASCII.GetBytes(revision.ContentHash ?? string.Empty);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static void WriteWorkspaceApplicability(Utf8JsonWriter writer, ContextualRoleWorkspaceApplicability? applicability)
    {
        writer.WritePropertyName("workspaceIds");
        writer.WriteStartArray();
        foreach (var workspaceId in (applicability?.WorkspaceIds ?? []).Order(StringComparer.Ordinal))
        {
            writer.WriteStringValue(Normalize(workspaceId));
        }

        writer.WriteEndArray();
    }

    private static void WriteInstructionSource(Utf8JsonWriter writer, ContextualRoleInstructionSourceReference? source)
    {
        writer.WritePropertyName("instructionSource");
        writer.WriteStartObject();
        writer.WriteString("kind", source?.Kind.ToString());
        writer.WriteString("referenceId", Normalize(source?.ReferenceId));
        writer.WriteString("classification", source?.Classification.ToString());
        writer.WriteEndObject();
    }

    private static void WritePolicyMaxima(Utf8JsonWriter writer, ContextualRolePolicyMaxima? maxima)
    {
        writer.WritePropertyName("capabilityMaximumIds");
        writer.WriteStartArray();
        foreach (var capabilityId in (maxima?.CapabilityIds ?? []).Order(StringComparer.Ordinal))
        {
            writer.WriteStringValue(Normalize(capabilityId));
        }

        writer.WriteEndArray();
    }

    private static string? Normalize(string? value) => value?.Normalize(NormalizationForm.FormC);
}
