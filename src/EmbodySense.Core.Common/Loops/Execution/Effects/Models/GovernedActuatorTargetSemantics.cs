namespace EmbodySense.Core.Common.Loops.Execution.Effects.Models;

/// <summary>Identifies how an actuator operation binds one exact effect target.</summary>
public enum GovernedActuatorTargetSemantics
{
    /// <summary>No supported target semantics were selected.</summary>
    Unknown = 0,

    /// <summary>The server resolves one stable opaque target and retains only its canonical fingerprint.</summary>
    ExactOpaqueFingerprint = 1,

    /// <summary>The server resolves one exact workspace-contained target and retains only its canonical fingerprint.</summary>
    ExactWorkspaceTarget = 2,
}
