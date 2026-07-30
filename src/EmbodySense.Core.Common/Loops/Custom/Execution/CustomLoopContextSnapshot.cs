using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Common.Loops.Custom.Execution;

/// <summary>
/// Captures the ordered, provenance-tagged context admitted to one custom-loop execution.
/// </summary>
/// <param name="SchemaVersion">The persisted schema version.</param>
/// <param name="CapturedAtUtc">The UTC capture time.</param>
/// <param name="SourceManifest">The source manifest.</param>
/// <param name="ManifestHash">The manifest hash.</param>
public sealed record CustomLoopContextSnapshot(
    int SchemaVersion,
    DateTimeOffset CapturedAtUtc,
    CustomLoopContextManifestSource[] SourceManifest,
    string ManifestHash)
{
    /// <summary>
    /// Schema version required by the current context-snapshot contract.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Projects included role, identity, and contextual-state sources into model messages.
    /// </summary>
    /// <value>Included non-conversation sources in manifest order, projected to their captured role and content.</value>
    [JsonIgnore]
    public CustomLoopMessageSnapshot[] WorkspaceContextMessages => (SourceManifest ?? [])
            .Where(source => source is not null && source.Included && (source.SourceType is CustomLoopContextSource.RoleInstruction or CustomLoopContextSource.AgentIdentity or CustomLoopContextSource.ContextualState))
            .Select(source => new CustomLoopMessageSnapshot(source.Role, source.Content))
            .ToArray();

    /// <summary>
    /// Projects included invoking-conversation sources into model messages.
    /// </summary>
    /// <value>Included invoking-conversation sources in manifest order, projected to their captured role and content.</value>
    [JsonIgnore]
    public CustomLoopMessageSnapshot[] InvokingConversationMessages => (SourceManifest ?? [])
            .Where(source => source is not null && source.Included && source.SourceType == CustomLoopContextSource.InvokingConversation)
            .Select(source => new CustomLoopMessageSnapshot(source.Role, source.Content))
            .ToArray();

    /// <summary>
    /// Creates a hash-bound snapshot whose standard workspace context sources are all explicitly omitted.
    /// </summary>
    /// <param name="capturedAtUtc">The UTC capture time recorded on the snapshot and every omission entry.</param>
    /// <returns>A version-1 snapshot with deterministic source ordering, omission evidence, and a matching manifest hash.</returns>
    public static CustomLoopContextSnapshot CreateEmpty(DateTimeOffset capturedAtUtc)
    {
        var snapshot = new CustomLoopContextSnapshot(
            CurrentSchemaVersion,
            capturedAtUtc,
            [
                OmittedWorkspaceSource(1, CustomLoopContextSource.RoleInstruction, "nearest-agents", CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System, capturedAtUtc),
                OmittedWorkspaceSource(2, CustomLoopContextSource.RoleInstruction, "role", CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System, capturedAtUtc),
                OmittedWorkspaceSource(3, CustomLoopContextSource.AgentIdentity, "soul", CustomLoopContextProvenance.WorkspaceAgentIdentityFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System, capturedAtUtc),
                OmittedWorkspaceSource(4, CustomLoopContextSource.AgentIdentity, "personality", CustomLoopContextProvenance.WorkspaceAgentIdentityFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System, capturedAtUtc),
                OmittedWorkspaceSource(5, CustomLoopContextSource.ContextualState, "context", CustomLoopContextProvenance.WorkspaceContextFile, CustomLoopContextTrustClass.UntrustedData, LlmMessageRole.User, capturedAtUtc),
                OmittedWorkspaceSource(6, CustomLoopContextSource.ContextualState, "memory", CustomLoopContextProvenance.WorkspaceContextFile, CustomLoopContextTrustClass.UntrustedData, LlmMessageRole.User, capturedAtUtc),
                OmittedWorkspaceSource(7, CustomLoopContextSource.ContextualState, "models", CustomLoopContextProvenance.WorkspaceContextFile, CustomLoopContextTrustClass.UntrustedData, LlmMessageRole.User, capturedAtUtc)
            ],
            string.Empty);
        return CustomLoopContextSnapshotHash.Apply(snapshot);
    }

    private static CustomLoopContextManifestSource OmittedWorkspaceSource(
        int order,
        CustomLoopContextSource sourceType,
        string sourceId,
        CustomLoopContextProvenance provenance,
        CustomLoopContextTrustClass trustClass,
        LlmMessageRole role,
        DateTimeOffset capturedAtUtc)
    {
        var sourcePath = sourceId switch
        {
            "nearest-agents" => "unavailable/AGENTS.md",
            "role" => "unavailable/.agent/ROLE.md",
            "soul" => "unavailable/.agent/SOUL.md",
            "personality" => "unavailable/.agent/PERSONALITY.md",
            "context" => "unavailable/.agent/CONTEXT.md",
            "memory" => "unavailable/.agent/MEMORY.md",
            "models" => "unavailable/.agent/models.json",
            _ => $"unavailable/{sourceId}"
        };
        return new CustomLoopContextManifestSource(order, sourceType, sourceId, sourcePath, provenance, trustClass, role, string.Empty, CustomLoopTraceContentHash.Compute(string.Empty), 0, 0, false, null, "Source was not present in this captured context.", capturedAtUtc);
    }
}
