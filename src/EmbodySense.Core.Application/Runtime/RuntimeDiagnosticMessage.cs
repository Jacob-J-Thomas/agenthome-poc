namespace EmbodySense.Core.Application.Runtime;

public sealed record RuntimeDiagnosticMessage
{
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

    public RuntimeDiagnosticKind Kind { get; }

    public string Content { get; }

    public string? Title { get; }
}
