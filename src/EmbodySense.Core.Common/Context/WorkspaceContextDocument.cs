namespace EmbodySense.Core.Common.Context;

public enum WorkspaceContextDocumentKind
{
    Unknown = 0,
    RoleInstruction = 1,
    ContextualState = 2
}

public sealed record WorkspaceContextDocument(
    string SourceId,
    string DisplayPath,
    string ExactPath,
    WorkspaceContextDocumentKind Kind,
    string Content,
    int OriginalCharacterCount,
    string? OmissionReason)
{
    public WorkspaceContextDocument(string displayPath, string content)
        : this(displayPath, displayPath, displayPath, WorkspaceContextDocumentKind.Unknown, content, content?.Length ?? 0, null)
    {
    }
}
