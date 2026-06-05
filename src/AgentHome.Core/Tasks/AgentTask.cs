namespace AgentHome.Core.Tasks;

public sealed record AgentTask
{
    public required string Id { get; init; }
    public required string Goal { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public List<string> Constraints { get; init; } = new();
    public List<string> Decisions { get; init; } = new();
    public List<string> Artifacts { get; init; } = new();
}
