using EmbodySense.Core.Application.Runtime.Models;
namespace EmbodySense.Core.Application.Runtime;

/// <summary>
/// Represents a loop run identity.
/// </summary>
public sealed record LoopRunIdentity
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LoopRunIdentity"/> type.
    /// </summary>
    /// <param name="loopId">The loop ID.</param>
    /// <param name="runId">The run ID.</param>
    /// <param name="roleId">The role ID.</param>
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

    /// <summary>
    /// Gets the loop ID.
    /// </summary>
    /// <value>The loop ID.</value>
    public string LoopId { get; }

    /// <summary>
    /// Gets the run ID.
    /// </summary>
    /// <value>The run ID.</value>
    public string RunId { get; }

    /// <summary>
    /// Gets the role ID.
    /// </summary>
    /// <value>The role ID.</value>
    public string? RoleId { get; }
}
