namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>
/// Correlates an interface turn or event with its durable loop execution.
/// </summary>
/// <param name="LoopId">The executing loop definition identity.</param>
/// <param name="RunId">The durable run identity.</param>
/// <param name="RoleId">The contextual role captured by the run, when available.</param>
public sealed record AgentRuntimeRunIdentity(string LoopId, string RunId, string? RoleId);
