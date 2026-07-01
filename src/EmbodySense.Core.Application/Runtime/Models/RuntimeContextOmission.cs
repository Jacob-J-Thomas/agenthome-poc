namespace EmbodySense.Core.Application.Runtime.Models;

public sealed record RuntimeContextOmission
{
    public RuntimeContextOmission(string source, string stage, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Source = source;
        Stage = stage;
        Reason = reason;
    }

    public string Source { get; }

    public string Stage { get; }

    public string Reason { get; }
}
