namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Reports one value-free registered instruction-source posture.</summary>
/// <param name="Status">The closed posture without source content, paths, hashes, or native diagnostics.</param>
public sealed record ContextualRoleInstructionSourceProbeResult(ContextualRoleInstructionSourceProbeStatus Status);
