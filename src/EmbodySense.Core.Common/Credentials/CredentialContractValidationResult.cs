namespace EmbodySense.Core.Common.Credentials;

/// <summary>Reports deterministic credential contract validation failures.</summary>
public sealed class CredentialContractValidationResult
{
    private CredentialContractValidationResult(IReadOnlyList<CredentialContractError> errors)
    {
        Errors = Array.AsReadOnly((errors ?? throw new ArgumentNullException(nameof(errors))).ToArray());
    }

    /// <summary>Gets the shared valid result.</summary>
    public static CredentialContractValidationResult Valid { get; } = new([]);

    /// <summary>Gets the ordered structured failures.</summary>
    public IReadOnlyList<CredentialContractError> Errors { get; }

    /// <summary>Gets whether no validation failure was found.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>Creates one bounded rejection from a closed code and sanitized path.</summary>
    public static CredentialContractValidationResult Rejected(Models.CredentialContractErrorCode code) => RejectedAt(code, "$");

    internal static CredentialContractValidationResult RejectedAt(Models.CredentialContractErrorCode code, string? path) => new([CredentialContractError.Create(code, path)]);

    internal static CredentialContractValidationResult FromErrors(IReadOnlyList<CredentialContractError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return errors.Count <= CredentialContractLimits.MaxValidationErrors ? new CredentialContractValidationResult(errors) : Rejected(Models.CredentialContractErrorCode.ValidationLimitExceeded);
    }

    /// <inheritdoc />
    public override string ToString() => IsValid ? "valid" : string.Join(',', Errors.Select(error => error.ToString()));
}
