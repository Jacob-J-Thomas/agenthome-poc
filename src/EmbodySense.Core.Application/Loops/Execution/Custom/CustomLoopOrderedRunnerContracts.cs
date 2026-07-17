using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public sealed record CustomLoopOrderedRunRequest(
    string RunId,
    string Actor);

public enum CustomLoopOrderedRunStatus
{
    Unknown = 0,
    Completed = 1,
    Failed = 2,
    NeedsReview = 3,
    Conflict = 4,
    InvalidState = 5,
    NotFound = 6,
    Cancelled = 7,
    Paused = 8
}

public sealed record CustomLoopOrderedRunResult(
    CustomLoopOrderedRunStatus Status,
    CustomLoopRunRecord? Run,
    string Detail);

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
    IReadOnlyList<CustomLoopPriorConversationPublication>? PriorPublications = null);

public sealed record CustomLoopPriorConversationPublication(
    string OperationId,
    string CanonicalOutput,
    string CanonicalOutputHash);

public enum CustomLoopConversationPublicationOutcome
{
    Unknown = 0,
    Published = 1,
    AlreadyPublished = 2,
    DefinitelyFailed = 3,
    Uncertain = 4
}

public sealed record CustomLoopConversationPublicationResult(
    CustomLoopConversationPublicationOutcome Outcome,
    string? PublicationId,
    string Detail);

public interface ICustomLoopConversationPublisher
{
    Task<CustomLoopConversationPublicationResult> PublishAsync(CustomLoopConversationPublicationRequest request, CancellationToken cancellationToken = default);
}
