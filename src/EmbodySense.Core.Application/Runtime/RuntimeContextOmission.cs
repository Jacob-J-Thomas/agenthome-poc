using EmbodySense.Core.Application.Runtime.Models;
namespace EmbodySense.Core.Application.Runtime;

/// <summary>
/// Represents a runtime context omission.
/// </summary>
public sealed record RuntimeContextOmission
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RuntimeContextOmission"/> type.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <param name="stage">The stage.</param>
    /// <param name="reason">The reason.</param>
    public RuntimeContextOmission(string source, string stage, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Source = source;
        Stage = stage;
        Reason = reason;
    }

    /// <summary>
    /// Gets the source.
    /// </summary>
    /// <value>The source.</value>
    public string Source { get; }

    /// <summary>
    /// Gets the stage.
    /// </summary>
    /// <value>The stage.</value>
    public string Stage { get; }

    /// <summary>
    /// Gets the reason.
    /// </summary>
    /// <value>The reason.</value>
    public string Reason { get; }
}
