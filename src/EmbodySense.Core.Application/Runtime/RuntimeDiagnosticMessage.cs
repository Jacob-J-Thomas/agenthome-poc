using EmbodySense.Core.Application.Runtime.Models;
namespace EmbodySense.Core.Application.Runtime;

/// <summary>
/// Represents a runtime diagnostic message.
/// </summary>
public sealed record RuntimeDiagnosticMessage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RuntimeDiagnosticMessage"/> type.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <param name="content">The content.</param>
    /// <param name="title">The title.</param>
    public RuntimeDiagnosticMessage(RuntimeDiagnosticKind kind, string content, string? title = null)
    {
        if (!Enum.IsDefined(kind) || kind == RuntimeDiagnosticKind.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Choose a concrete diagnostic kind.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        Kind = kind;
        Content = content;
        Title = title;
    }

    /// <summary>
    /// Gets the runtime diagnostic kind.
    /// </summary>
    /// <value>The runtime diagnostic kind.</value>
    public RuntimeDiagnosticKind Kind { get; }

    /// <summary>
    /// Gets the content.
    /// </summary>
    /// <value>The content.</value>
    public string Content { get; }

    /// <summary>
    /// Gets the title.
    /// </summary>
    /// <value>The title.</value>
    public string? Title { get; }
}
