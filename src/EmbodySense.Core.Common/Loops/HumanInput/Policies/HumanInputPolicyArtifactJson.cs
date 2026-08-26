using System.Text.Json;
using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;

namespace EmbodySense.Core.Common.Loops.HumanInput.Policies;

/// <summary>Serializes and strictly parses one immutable Human Input policy artifact without compatibility readers.</summary>
public static class HumanInputPolicyArtifactJson
{
    private const int MaximumUtf8Bytes = 16 * 1024;
    private static readonly string[] _properties = ["schemaVersion", "policyId", "revisionId", "kind", "workspaceId", "graphId", "authorityActorId", "responseWindowMilliseconds", "terminalDisposition", "contentHash"];

    /// <summary>Writes one canonical UTF-8 JSON policy artifact.</summary>
    /// <param name="artifact">The complete hash-authenticated artifact.</param>
    /// <returns>Canonical compact UTF-8 JSON.</returns>
    /// <exception cref="ArgumentException">The artifact is incomplete or invalid.</exception>
    public static byte[] Serialize(HumanInputPolicyArtifact artifact)
    {
        if (!HumanInputPolicyArtifactValidator.Validate(artifact).IsValid) throw new ArgumentException("The Human Input policy artifact is invalid.", nameof(artifact));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", artifact.SchemaVersion);
            writer.WriteString("policyId", artifact.PolicyId);
            writer.WriteString("revisionId", artifact.RevisionId);
            writer.WriteNumber("kind", (int)artifact.Kind);
            writer.WriteString("workspaceId", artifact.WorkspaceId);
            writer.WriteString("graphId", artifact.GraphId);
            writer.WriteString("authorityActorId", artifact.AuthorityActorId);
            if (artifact.ResponseWindowMilliseconds is { } window) writer.WriteNumber("responseWindowMilliseconds", window); else writer.WriteNull("responseWindowMilliseconds");
            writer.WriteNumber("terminalDisposition", (int)artifact.TerminalDisposition);
            writer.WriteString("contentHash", artifact.ContentHash);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>Strictly parses one canonical JSON policy artifact.</summary>
    /// <param name="utf8Json">The untrusted JSON bytes.</param>
    /// <returns>The detached exact artifact.</returns>
    /// <exception cref="FormatException">The JSON is malformed, has an unknown shape, or fails artifact validation.</exception>
    public static HumanInputPolicyArtifact Deserialize(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length is < 2 or > MaximumUtf8Bytes) throw new FormatException("The Human Input policy JSON exceeds schema-1 bounds.");
        try
        {
            using var document = JsonDocument.Parse(utf8Json.ToArray(), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(_properties.Order(StringComparer.Ordinal)) == false) throw new FormatException("The Human Input policy JSON shape is unsupported.");
            var artifact = new HumanInputPolicyArtifact(
                root.GetProperty("schemaVersion").GetInt32(),
                root.GetProperty("policyId").GetString()!,
                root.GetProperty("revisionId").GetString()!,
                (HumanInputPolicyKind)root.GetProperty("kind").GetInt32(),
                root.GetProperty("workspaceId").GetString()!,
                root.GetProperty("graphId").GetString()!,
                root.GetProperty("authorityActorId").GetString()!,
                root.GetProperty("responseWindowMilliseconds").ValueKind == JsonValueKind.Null ? null : root.GetProperty("responseWindowMilliseconds").GetInt64(),
                (HumanInputTerminalDisposition)root.GetProperty("terminalDisposition").GetInt32(),
                root.GetProperty("contentHash").GetString()!);
            if (!HumanInputPolicyArtifactValidator.Validate(artifact).IsValid) throw new FormatException("The Human Input policy JSON artifact is invalid.");
            return artifact;
        }
        catch (JsonException exception)
        {
            throw new FormatException("The Human Input policy JSON is malformed.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new FormatException("The Human Input policy JSON shape is invalid.", exception);
        }
    }
}
