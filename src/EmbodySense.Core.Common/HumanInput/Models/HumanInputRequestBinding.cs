namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Exact identity binding that prevents a human-input exchange from being reused across workspaces, loop revisions, nodes, runs, or checkpoints.
/// </summary>
/// <param name="WorkspaceId">The stable workspace ID.</param>
/// <param name="LoopRevisionId">The exact immutable loop-revision ID.</param>
/// <param name="NodeId">The exact node ID.</param>
/// <param name="RunId">The exact run ID.</param>
/// <param name="CheckpointId">The exact checkpoint ID.</param>
public sealed record HumanInputRequestBinding(string WorkspaceId, string LoopRevisionId, string NodeId, string RunId, string CheckpointId);
