namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>
/// Represents a custom loop prior conversation publication.
/// </summary>
/// <param name="OperationId">The operation ID.</param>
/// <param name="CanonicalOutput">The canonical output.</param>
/// <param name="CanonicalOutputHash">The canonical output hash.</param>
public sealed record CustomLoopPriorConversationPublication(
    string OperationId,
    string CanonicalOutput,
    string CanonicalOutputHash);
