using EmbodySense.Core.Common.Context.Models;
namespace EmbodySense.Core.Common.Context;

/// <summary>
/// Represents a workspace context document.
/// </summary>
/// <param name="SourceId">The stable source identifier.</param>
/// <param name="DisplayPath">The display path.</param>
/// <param name="ExactPath">The exact path.</param>
/// <param name="Kind">The kind.</param>
/// <param name="Content">The exact content.</param>
/// <param name="OriginalCharacterCount">The character count before truncation or omission.</param>
/// <param name="OmissionReason">The omission reason, or <see langword="null"/> when the source was included.</param>
public sealed record WorkspaceContextDocument(
    string SourceId,
    string DisplayPath,
    string ExactPath,
    WorkspaceContextDocumentKind Kind,
    string Content,
    int OriginalCharacterCount,
    string? OmissionReason)
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkspaceContextDocument"/> type.
    /// </summary>
    /// <param name="displayPath">The display path.</param>
    /// <param name="content">The content.</param>
    public WorkspaceContextDocument(string displayPath, string content)
        : this(displayPath, displayPath, displayPath, WorkspaceContextDocumentKind.Unknown, content, content?.Length ?? 0, null)
    {
    }
}
