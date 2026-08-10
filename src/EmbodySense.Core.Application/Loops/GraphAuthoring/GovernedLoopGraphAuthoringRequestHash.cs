using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;

namespace EmbodySense.Core.Application.Loops.GraphAuthoring;

/// <summary>Computes the canonical hash that binds a lifecycle intent to executable and display content.</summary>
public static class GovernedLoopGraphAuthoringRequestHash
{
    /// <summary>Computes the lowercase SHA-256 digest of one normalized full authoring intent.</summary>
    public static string Compute(
        GovernedLoopRevisionLifecycleRequest lifecycleRequest,
        GovernedLoopGraphDefinition? graph)
    {
        ArgumentNullException.ThrowIfNull(lifecycleRequest);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("contract", "governed-loop-graph-authoring-request-v1");
            writer.WriteString("lifecycleRequestHash", GovernedLoopRevisionLifecycleRequestHash.Compute(lifecycleRequest));
            if (graph is null)
            {
                writer.WriteNull("executableHash");
                writer.WriteNull("layoutHash");
            }
            else
            {
                writer.WriteString("executableHash", graph.ExecutableHash);
                writer.WriteString("layoutHash", GovernedLoopGraphRevisionContractHash.ComputeLayoutHash(graph));
            }

            writer.WriteEndObject();
            writer.Flush();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }
}
