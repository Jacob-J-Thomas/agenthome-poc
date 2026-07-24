using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Persistence.ToolResults.Models;

internal sealed record ToolResultArtifactManifest(
    int SchemaVersion,
    string RequestId,
    string? ToolRequestCorrelationId,
    string LoopId,
    string RoleId,
    string? RunId,
    int? DefinitionVersion,
    string? DefinitionHash,
    int? Iteration,
    string? StepId,
    int? Attempt,
    string? AttemptCorrelationId,
    ToolCommand Command,
    string TargetPath,
    string ResolvedPath,
    ToolExecutionOutcome Outcome,
    string ContentSha256,
    int CharacterCount,
    long Utf8ByteCount,
    DateTimeOffset RetainedAtUtc,
    string RetentionPolicy,
    ToolResultArtifactChunk[] Chunks);
