using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.GraphAuthoring;

internal static class GovernedLoopGraphValidationBindingHash
{
    internal static string Compute(
        string authoringRequestHash,
        GovernedLoopGraphDefinition graph,
        string validationEvidenceHash)
    {
        return Compute(
            authoringRequestHash,
            graph.ExecutableHash,
            GovernedLoopGraphRevisionContractHash.ComputeLayoutHash(graph),
            validationEvidenceHash);
    }

    internal static string Compute(
        string authoringRequestHash,
        GovernedLoopGraphRevisionArtifact artifact,
        string validationEvidenceHash)
    {
        return Compute(
            authoringRequestHash,
            artifact.RevisionArtifact.Revision.ExecutableHash,
            artifact.LayoutHash,
            validationEvidenceHash,
            artifact.ArtifactHash);
    }

    private static string Compute(
        string authoringRequestHash,
        string executableHash,
        string layoutHash,
        string validationEvidenceHash,
        string? artifactHash = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("contract", "governed-loop-graph-validation-binding-v1");
            writer.WriteString("authoringRequestHash", authoringRequestHash);
            writer.WriteString("executableHash", executableHash);
            writer.WriteString("layoutHash", layoutHash);
            writer.WriteString("artifactHash", artifactHash);
            writer.WriteString("validationEvidenceHash", validationEvidenceHash);
            writer.WriteEndObject();
            writer.Flush();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }
}
