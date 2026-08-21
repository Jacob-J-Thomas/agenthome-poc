using EmbodySense.Core.Common.Authority.Delegation.Models;

namespace EmbodySense.Core.Common.Authority.Delegation;

/// <summary>Returns the complete bounded validation posture of one delegated-authority contract.</summary>
public sealed record AuthorityDelegationContractValidationResult
{
    /// <summary>Creates a defensively copied validation result.</summary>
    /// <param name="errors">The bounded value-free failures.</param>
    /// <param name="isValid">Whether no validation failure was observed.</param>
    public AuthorityDelegationContractValidationResult(IReadOnlyList<AuthorityDelegationContractValidationError> errors, bool isValid)
    {
        var snapshot = errors is null
            ? Array.Empty<AuthorityDelegationContractValidationError>()
            : AuthorityDelegationContractCopy.Snapshot(errors, AuthorityDelegationContractLimits.MaxValidationErrors);
        Errors = snapshot ?? Array.AsReadOnly([
            new AuthorityDelegationContractValidationError(
                AuthorityDelegationContractValidationErrorCode.InvalidCollection,
                "$.errors"),
        ]);
        IsValid = isValid && Errors.Count == 0;
    }

    /// <summary>Gets the bounded value-free failures.</summary>
    public IReadOnlyList<AuthorityDelegationContractValidationError> Errors { get; }

    /// <summary>Gets whether the complete contract is valid.</summary>
    public bool IsValid { get; }
}
