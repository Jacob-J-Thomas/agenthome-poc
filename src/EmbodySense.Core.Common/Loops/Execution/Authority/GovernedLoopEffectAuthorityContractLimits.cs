using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Common.Loops.Execution.Authority;

/// <summary>Defines finite schema-version-1 bounds for effect-authority decision evidence.</summary>
public static class GovernedLoopEffectAuthorityContractLimits
{
    /// <summary>Gets the only supported experimental schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the maximum canonical operation or correlation identifier length.</summary>
    public const int MaxIdentifierCharacters = GovernedLoopExecutionLimits.MaxIdentifierCharacters;

    /// <summary>Gets the maximum exact capability pins retained in one proof.</summary>
    public const int MaxCapabilityPins = CapabilityContractLimits.MaxCapabilityAdmissionPins;

    /// <summary>Gets the maximum exact capability pins required by one effect boundary.</summary>
    public const int MaxRequiredCapabilityPins = AuthorityContractLimits.MaxCapabilitiesPerCeiling;

    /// <summary>Gets the maximum positive execution generation.</summary>
    public const long MaxExecutionGeneration = GovernedLoopExecutionLimits.MaxExecutionGeneration;

    /// <summary>Gets the maximum positive node-attempt number.</summary>
    public const int MaxNodeAttempt = GovernedLoopExecutionLimits.MaxNodeAttempt;

    /// <summary>Gets the lowercase hexadecimal character count of one SHA-256 digest.</summary>
    public const int Sha256HexCharacters = GovernedLoopExecutionLimits.Sha256HexCharacters;

    /// <summary>Gets the maximum number of value-free validation errors returned by one call.</summary>
    public const int MaxValidationErrors = 64;

    /// <summary>Gets the maximum safe schema-relative validation path length.</summary>
    public const int MaxValidationPathCharacters = GovernedLoopExecutionLimits.MaxErrorPathCharacters;
}
