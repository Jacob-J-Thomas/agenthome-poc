namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

public enum ScriptedEffectAuthorityBehavior
{
    Direct,
    Deny,
    Pause,
    Invalid,
    Unavailable,
    ReplayAmbiguous,
    DoubleCallback,
    LateCallback,
    NullResult,
    MalformedResult,
    MismatchedOperation,
    ForgedAdmittedProof,
}
