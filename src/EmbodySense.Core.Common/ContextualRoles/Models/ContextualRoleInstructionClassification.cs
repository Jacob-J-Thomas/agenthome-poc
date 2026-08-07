namespace EmbodySense.Core.Common.ContextualRoles.Models;

/// <summary>Classifies the authority and identity meaning of a referenced instruction source.</summary>
public enum ContextualRoleInstructionClassification
{
    /// <summary>An undefined classification that is never valid.</summary>
    Unknown = 0,
    /// <summary>Instructions explicitly classified for the contextual role.</summary>
    RoleInstruction = 1,
    /// <summary>Durable identity or personality material, which cannot become role instructions through this contract.</summary>
    DurableIdentity = 2,
    /// <summary>Untrusted contextual material, which cannot become role instructions through this contract.</summary>
    UntrustedContext = 3
}
