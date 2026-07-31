namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Projects the immutable admission-time context manifest and its execution-ready message groups.
/// </summary>
/// <param name="SchemaVersion">The schema version.</param>
/// <param name="CapturedAtUtc">The captured at utc.</param>
/// <param name="ManifestHash">The manifest hash.</param>
/// <param name="SourceManifest">The source manifest.</param>
/// <param name="WorkspaceContextMessages">The workspace context messages.</param>
/// <param name="InvokingConversationMessages">The invoking conversation messages.</param>
public sealed record LoopRunContextSnapshot(
    int SchemaVersion,
    DateTimeOffset CapturedAtUtc,
    string ManifestHash,
    IReadOnlyList<LoopRunContextManifestSourceSnapshot> SourceManifest,
    IReadOnlyList<LoopRunMessageSnapshot> WorkspaceContextMessages,
    IReadOnlyList<LoopRunMessageSnapshot> InvokingConversationMessages);
