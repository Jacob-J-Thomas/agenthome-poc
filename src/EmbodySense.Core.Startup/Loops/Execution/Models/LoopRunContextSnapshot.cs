namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed record LoopRunContextSnapshot(
    int SchemaVersion,
    DateTimeOffset CapturedAtUtc,
    string ManifestHash,
    IReadOnlyList<LoopRunContextManifestSourceSnapshot> SourceManifest,
    IReadOnlyList<LoopRunMessageSnapshot> WorkspaceContextMessages,
    IReadOnlyList<LoopRunMessageSnapshot> InvokingConversationMessages);
