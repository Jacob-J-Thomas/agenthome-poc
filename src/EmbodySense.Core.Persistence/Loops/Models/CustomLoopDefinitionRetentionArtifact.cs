using EmbodySense.Core.Application.Loops.Models;

namespace EmbodySense.Core.Persistence.Loops.Models;

internal sealed record CustomLoopDefinitionRetentionArtifact(
    string ArtifactId,
    string Path,
    byte[] Utf8Json,
    string Hash,
    CustomLoopDefinitionMutationOperationRecord? Operation,
    CustomLoopDefinitionTombstone? Tombstone);
