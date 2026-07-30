namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>
/// Represents a custom loop conversation publication request.
/// </summary>
/// <param name="OperationId">The operation ID.</param>
/// <param name="RunId">The unique run identifier.</param>
/// <param name="LoopId">The owning loop identifier.</param>
/// <param name="Iteration">The iteration.</param>
/// <param name="StepId">The step ID.</param>
/// <param name="ConversationId">The conversation ID.</param>
/// <param name="ExpectedConversationVersion">The expected conversation version.</param>
/// <param name="CanonicalOutput">The canonical output.</param>
/// <param name="CanonicalOutputHash">The canonical output hash.</param>
/// <param name="PriorPublications">The prior publications.</param>
/// <param name="AppendStarted">The append started.</param>
public sealed record CustomLoopConversationPublicationRequest(
    string OperationId,
    string RunId,
    string LoopId,
    int Iteration,
    string StepId,
    string ConversationId,
    string ExpectedConversationVersion,
    string CanonicalOutput,
    string CanonicalOutputHash,
    IReadOnlyList<CustomLoopPriorConversationPublication>? PriorPublications = null,
    Action? AppendStarted = null);
