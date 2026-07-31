using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Persistence.ToolResults.Models;

/// <summary>
/// Represents a tool result artifact manifest.
/// </summary>
/// <param name="SchemaVersion">The schema version.</param>
/// <param name="RequestId">The request ID.</param>
/// <param name="ToolRequestCorrelationId">The tool request correlation ID.</param>
/// <param name="LoopId">The loop ID.</param>
/// <param name="RoleId">The role ID.</param>
/// <param name="RunId">The run ID.</param>
/// <param name="DefinitionVersion">The definition version.</param>
/// <param name="DefinitionHash">The definition hash.</param>
/// <param name="Iteration">The iteration.</param>
/// <param name="StepId">The step ID.</param>
/// <param name="Attempt">The attempt.</param>
/// <param name="AttemptCorrelationId">The attempt correlation ID.</param>
/// <param name="Command">The command.</param>
/// <param name="TargetPath">The target path.</param>
/// <param name="ResolvedPath">The resolved path.</param>
/// <param name="Outcome">The outcome.</param>
/// <param name="ContentSha256">The content SHA-256.</param>
/// <param name="CharacterCount">The character count.</param>
/// <param name="Utf8ByteCount">The UTF-8 byte count.</param>
/// <param name="RetainedAtUtc">The retained at UTC.</param>
/// <param name="RetentionPolicy">The retention policy.</param>
/// <param name="Chunks">The chunks.</param>
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
