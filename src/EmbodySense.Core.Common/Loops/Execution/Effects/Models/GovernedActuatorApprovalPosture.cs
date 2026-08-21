namespace EmbodySense.Core.Common.Loops.Execution.Effects.Models;

/// <summary>Declares whether an operation requires separate governed approval before dispatch.</summary>
public enum GovernedActuatorApprovalPosture
{
    /// <summary>No supported posture was selected.</summary>
    Unknown = 0,

    /// <summary>The admitted effect may proceed without a separate human approval checkpoint.</summary>
    AuthorityOnly = 1,

    /// <summary>A separate governed approval proof is required before dispatch.</summary>
    GovernedApprovalRequired = 2,
}
