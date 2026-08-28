namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

public enum ScriptedConversationPublicationAuthorityBehavior
{
    Direct,
    DenyRevoked,
    Pause,
    DenyUnrelatedCeiling,
    DenyExternalPublication,
    Invalid,
    Unavailable,
    Replay,
    EvidenceUnavailable,
    DirectAlreadyPresent,
    DoubleCallback,
    LateCallback,
    UnawaitedCallback,
    UnawaitedCallbackAfterAppendStarted,
    NoCallbackDirect,
    NullResult,
    MalformedResult,
    MismatchedDecision,
    MismatchedOperationDecision,
    ForgedAdmittedProofDecision,
    SwallowCallbackFailure,
}
