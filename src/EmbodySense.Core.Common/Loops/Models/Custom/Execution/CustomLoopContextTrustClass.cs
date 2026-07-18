namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

public enum CustomLoopContextTrustClass
{
    Unknown = 0,
    NonOverridableGovernance = 1,
    TrustedInstruction = 2,
    TrustedMetadata = 3,
    UntrustedData = 4
}
