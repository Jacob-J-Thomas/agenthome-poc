namespace EmbodySense.Core.Common.Loops.Execution.Authority.Models;

/// <summary>Identifies the exact irreversible-commit boundary protected by one effect-authority decision.</summary>
public enum GovernedLoopEffectBoundaryKind
{
    /// <summary>The provider transport write that starts one inference request.</summary>
    ProviderTransport = 1,

    /// <summary>The governed workspace-tool intake boundary before approval or actuation.</summary>
    WorkspaceToolIntake = 2,

    /// <summary>The final governed workspace actuator boundary after any approval.</summary>
    WorkspaceActuation = 3,

    /// <summary>The identity-bearing append that publishes one conversation message.</summary>
    ConversationPublication = 4,

    /// <summary>A server-registered structured actuator operation's irreversible dispatch boundary.</summary>
    ActuatorDispatch = 5
}
