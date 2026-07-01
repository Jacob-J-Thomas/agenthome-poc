namespace EmbodySense.Core.Common.Runtime.Models;

public sealed record RuntimeSurfaceId
{
    private RuntimeSurfaceId(string id)
    {
        Id = id;
    }

    public string Id { get; }

    public static RuntimeSurfaceId Web { get; } = Create("web");

    public static RuntimeSurfaceId Cli { get; } = Create("cli");

    public static RuntimeSurfaceId Runtime { get; } = Create("runtime");

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

    public override string ToString()
    {
        return Id;
    }
}
