namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>
/// Projects one actionable default-conversation review through the interface-safe Startup boundary.
/// </summary>
public sealed record DefaultConversationReviewSnapshot(
    string TurnId,
    string RequestId,
    string RunId,
    int LifecycleVersion,
    string ProviderAttemptId,
    string ProviderCorrelationId,
    string Detail);
