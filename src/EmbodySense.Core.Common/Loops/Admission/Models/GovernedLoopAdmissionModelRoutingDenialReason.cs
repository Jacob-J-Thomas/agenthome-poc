namespace EmbodySense.Core.Common.Loops.Admission.Models;

/// <summary>Identifies one definitive, value-free reason an authored model-routing candidate is ineligible.</summary>
public enum GovernedLoopAdmissionModelRoutingDenialReason
{
    /// <summary>No configured default exists for a bounded inherit selector.</summary>
    DefaultNotConfigured = 1,
    /// <summary>The resolved candidate is absent from the exact admitted capability snapshot.</summary>
    CandidateNotAdmitted = 2,
    /// <summary>The candidate is not a model-profile capability.</summary>
    CandidateNotModelProfile = 3,
    /// <summary>The candidate's current capability lifecycle no longer matches its admission pin.</summary>
    CandidateLifecycleIneligible = 4,
    /// <summary>The exact server-owned model-profile metadata does not exist or no longer matches.</summary>
    CandidateMetadataIneligible = 5,
    /// <summary>The exact adapter is registered but not eligible for current dispatch.</summary>
    CandidateAdapterIneligible = 6,
    /// <summary>The candidate fails the common role, node, privacy, capability, or budget requirements.</summary>
    CandidateRequirementsUnsatisfied = 7
}
