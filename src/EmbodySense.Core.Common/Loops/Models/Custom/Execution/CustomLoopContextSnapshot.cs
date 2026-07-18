using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

public sealed record CustomLoopContextSnapshot(
    int SchemaVersion,
    DateTimeOffset CapturedAtUtc,
    CustomLoopContextManifestSource[] SourceManifest,
    string ManifestHash)
{
    public const int CurrentSchemaVersion = 1;

    [JsonIgnore]
    public CustomLoopMessageSnapshot[] DirectoryRoleMessages => (SourceManifest ?? [])
            .Where(source => source.Included && source.SourceType is CustomLoopContextSource.RoleInstruction or CustomLoopContextSource.ContextualState)
            .Select(source => new CustomLoopMessageSnapshot(source.Role, source.Content))
            .ToArray();

    [JsonIgnore]
    public CustomLoopMessageSnapshot[] InvokingConversationMessages => (SourceManifest ?? [])
            .Where(source => source.Included && source.SourceType == CustomLoopContextSource.InvokingConversation)
            .Select(source => new CustomLoopMessageSnapshot(source.Role, source.Content))
            .ToArray();

    public static CustomLoopContextSnapshot CreateEmpty(DateTimeOffset capturedAtUtc)
    {
        var snapshot = new CustomLoopContextSnapshot(
            CurrentSchemaVersion,
            capturedAtUtc,
            [
                OmittedWorkspaceSource(1, CustomLoopContextSource.RoleInstruction, "nearest-agents", CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System, capturedAtUtc),
                OmittedWorkspaceSource(2, CustomLoopContextSource.RoleInstruction, "agent", CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System, capturedAtUtc),
                OmittedWorkspaceSource(3, CustomLoopContextSource.RoleInstruction, "soul", CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System, capturedAtUtc),
                OmittedWorkspaceSource(4, CustomLoopContextSource.RoleInstruction, "personality", CustomLoopContextProvenance.WorkspaceRoleFile, CustomLoopContextTrustClass.TrustedInstruction, LlmMessageRole.System, capturedAtUtc),
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
            "agent" => "unavailable/.agent/AGENT.md",
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
