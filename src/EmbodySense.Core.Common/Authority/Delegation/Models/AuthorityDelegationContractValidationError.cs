namespace EmbodySense.Core.Common.Authority.Delegation.Models;

/// <summary>Reports one bounded, value-free delegated-authority validation failure.</summary>
/// <param name="Code">The closed failure classification.</param>
/// <param name="Path">The bounded structural path without rejected values.</param>
public sealed record AuthorityDelegationContractValidationError(AuthorityDelegationContractValidationErrorCode Code, string Path);
