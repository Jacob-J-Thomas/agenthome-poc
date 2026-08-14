namespace EmbodySense.Core.Common.Loops.Admission.Models;

/// <summary>Identifies one closed reproducible capability-policy denial reason.</summary>
public enum GovernedLoopAdmissionCapabilityDenialReason
{
    /// <summary>No supported denial reason is present.</summary>
    Unknown = 0,

    /// <summary>No exact effective-authority identity satisfies the required root dependency.</summary>
    RequiredCapabilityOutsideEffectiveAuthority = 1
}
