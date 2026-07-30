namespace EmbodySense.Core.Common.Runtime;

/// <summary>
/// Identifies the interface or runtime surface that owns an operation.
/// </summary>
/// <remarks>Identifiers are trimmed, normalized to lowercase, and limited to ASCII letters, digits, and hyphens.</remarks>
public sealed record RuntimeSurfaceId
{
    private RuntimeSurfaceId(string id)
    {
        Id = id;
    }

    /// <summary>
    /// Gets the normalized surface identifier.
    /// </summary>
    /// <value>The lowercase ASCII surface identifier.</value>
    public string Id { get; }

    /// <summary>
    /// Gets the Web interface surface.
    /// </summary>
    /// <value>The web runtime surface ID.</value>
    public static RuntimeSurfaceId Web { get; } = Create("web");

    /// <summary>
    /// Gets the CLI interface surface.
    /// </summary>
    /// <value>The CLI runtime surface ID.</value>
    public static RuntimeSurfaceId Cli { get; } = Create("cli");

    /// <summary>
    /// Gets the shared runtime surface.
    /// </summary>
    /// <value>The runtime surface ID.</value>
    public static RuntimeSurfaceId Runtime { get; } = Create("runtime");

    /// <summary>
    /// Creates a normalized runtime surface identifier.
    /// </summary>
    /// <param name="id">The surface identifier to normalize and validate.</param>
    /// <returns>A normalized runtime surface identifier.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="id"/> is empty or contains a character other than an ASCII letter, digit, or hyphen.</exception>
    public static RuntimeSurfaceId Create(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var normalized = id.Trim().ToLowerInvariant();
        if (normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("Runtime surface ids must contain only ASCII letters, digits, or hyphens.", nameof(id));
        }

        return new RuntimeSurfaceId(normalized);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Id;
    }
}
