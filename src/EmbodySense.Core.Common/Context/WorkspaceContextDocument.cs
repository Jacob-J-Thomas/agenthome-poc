namespace EmbodySense.Core.Common.Context;

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
