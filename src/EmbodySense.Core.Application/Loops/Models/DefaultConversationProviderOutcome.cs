namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Classifies what durable evidence proves about one provider attempt.
/// </summary>
public enum DefaultConversationProviderOutcome
{
    /// <summary>No provider outcome classification exists.</summary>
    Unknown = 0,
    /// <summary>The provider <c>turn/start</c> transport-write boundary has definitely not been reached.</summary>
    DefinitelyNotStarted,
    /// <summary>The provider turn/start transport-write boundary was reached but no terminal outcome was durably observed.</summary>
    OutcomeUnknown,
    /// <summary>A successful terminal provider outcome and exact output were observed.</summary>
    Observed,
    /// <summary>A successful terminal provider outcome was observed, but required completion bookkeeping failed before publication.</summary>
    ObservedWithAuditFailure,
    /// <summary>A conclusive terminal provider failure was observed, so the attempt must not be quarantined or redispatched.</summary>
    ObservedFailure
}
