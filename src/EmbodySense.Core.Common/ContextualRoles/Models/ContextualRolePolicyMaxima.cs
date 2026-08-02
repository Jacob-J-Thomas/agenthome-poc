using System.Collections.Immutable;

namespace EmbodySense.Core.Common.ContextualRoles.Models;

/// <summary>Declares capability ceilings that may constrain later effective authority but never grant it.</summary>
/// <param name="CapabilityIds">The immutable capability identifiers that form the role's upper bound.</param>
public sealed record ContextualRolePolicyMaxima(ImmutableArray<string> CapabilityIds)
{
    /// <summary>Gets a value confirming that this declarative ceiling has no grant, approval, consent, credential, or user-authority effect.</summary>
    public bool IsNonGranting => true;
}
