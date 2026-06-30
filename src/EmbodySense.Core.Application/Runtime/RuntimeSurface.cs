namespace EmbodySense.Core.Application.Runtime;

public sealed record RuntimeSurface
{
    private RuntimeSurface(string id)
    {
        Id = id;
    }

    public string Id { get; }

    public static RuntimeSurface Web { get; } = Create("web");

    public static RuntimeSurface Cli { get; } = Create("cli");

    public static RuntimeSurface Create(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var normalized = id.Trim().ToLowerInvariant();
        if (normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("Runtime surface ids must contain only ASCII letters, digits, or hyphens.", nameof(id));
        }

        return new RuntimeSurface(normalized);
    }

    public override string ToString()
    {
        return Id;
    }
}
