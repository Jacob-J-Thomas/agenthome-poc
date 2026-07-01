namespace EmbodySense.Core.Application.Runtime.Models;

public sealed record LoopRunIdentity
{
    public LoopRunIdentity(string loopId, string runId, string? roleId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loopId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (roleId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(roleId);
        }

        LoopId = loopId;
        RunId = runId;
        RoleId = roleId;
    }

    public string LoopId { get; }

    public string RunId { get; }

    public string? RoleId { get; }
}
