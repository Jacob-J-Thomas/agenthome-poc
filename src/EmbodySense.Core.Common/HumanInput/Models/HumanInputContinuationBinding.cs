namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Restricts future response-data visibility to the exact request node and checkpoint; it is not a continuation executor or authority grant.
/// </summary>
/// <param name="Kind">The only supported non-ambient visibility policy.</param>
/// <param name="NodeId">The node that may receive data in a future lifecycle implementation.</param>
/// <param name="CheckpointId">The checkpoint that may receive data in a future lifecycle implementation.</param>
public sealed record HumanInputContinuationBinding(HumanInputContinuationPolicyKind Kind, string NodeId, string CheckpointId);
