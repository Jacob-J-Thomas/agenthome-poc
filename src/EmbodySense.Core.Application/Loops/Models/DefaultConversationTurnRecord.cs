using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Inference;

namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Is the canonical version-1 intent, outbox, checkpoint, and recovery record for one ordinary chat turn.
/// </summary>
/// <remarks>
/// The base transcript and turn-owned messages are exact harness-owned evidence. Provider-private history is never authoritative.
/// Transitions are append-only and persistence adapters must reject updates that alter an existing transition or immutable identity.
/// </remarks>
public sealed record DefaultConversationTurnRecord(
    int SchemaVersion,
    int LifecycleVersion,
    string TurnId,
    string RequestId,
    LoopRunRecord Run,
    string ConversationId,
    string ConversationVersion,
    IReadOnlyList<LlmMessage> BaseTranscript,
    DefaultConversationTurnMessage UserMessage,
    DefaultConversationTurnMessage? AssistantMessage,
    string ProviderAttemptId,
    string ProviderCorrelationId,
    string UserPublicationId,
    string AssistantPublicationId,
    DefaultConversationProviderOutcome ProviderOutcome,
    string? ProviderResponseId,
    DefaultConversationTurnCheckpoint Checkpoint,
    bool RunProjectionSynchronized,
    string? ReviewDetail,
    DefaultConversationTurnReviewResolution? ReviewResolution,
    IReadOnlyList<DefaultConversationTurnTransition> Transitions)
{
    /// <summary>The only supported persisted protocol schema.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the immutable exact capability resolution admitted before any turn effect.</summary>
    public CapabilityAdmissionSnapshot CapabilityAdmission { get; init; } = null!;
}
