using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Inference.Profiles;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Supplies the complete immutable pre-receipt evidence needed to resolve model routing without a hash cycle.</summary>
public sealed record GovernedModelRoutingAdmissionSeed(
    GovernedLoopAdmissionIntent Intent,
    GovernedLoopExecutionBinding Binding,
    AuthorityGrantProfilePin GrantProfile,
    AuthorityGrantBoundary GrantBoundary,
    string GrantDependencyEvidenceHash,
    AuthorityCeiling EffectiveAuthority,
    CapabilityAdmissionSnapshot CapabilityAdmission,
    DateTimeOffset EvaluatedAtUtc)
{
    /// <summary>Gets a retained exact admission intent.</summary>
    public GovernedLoopAdmissionIntent Intent { get; } = Intent is null ? null! : Intent with { };
    /// <summary>Gets a reconstructed immutable execution binding.</summary>
    public GovernedLoopExecutionBinding Binding { get; } = Binding is null ? null! : GovernedLoopExecutionBinding.Create(Binding.SchemaVersion, Binding.RunId, Binding.Revision, Binding.ExecutionGeneration);
    /// <summary>Gets a retained exact authority-profile pin.</summary>
    public AuthorityGrantProfilePin GrantProfile { get; } = GrantProfile is null ? null! : new(GrantProfile.Reference, GrantProfile.ContentHash);
    /// <summary>Gets a retained grant boundary.</summary>
    public AuthorityGrantBoundary GrantBoundary { get; } = GrantBoundary is null ? null! : new(GrantBoundary.EffectiveAtUtc, GrantBoundary.ExpiresAtUtc, GrantBoundary.CompletionConstraint);
    /// <summary>Gets a defensively copied effective authority ceiling.</summary>
    public AuthorityCeiling EffectiveAuthority { get; } = EffectiveAuthority is null ? null! : new(
        Array.AsReadOnly(EffectiveAuthority.Capabilities.Take(AuthorityContractLimits.MaxCapabilitiesPerCeiling + 1).ToArray()),
        Array.AsReadOnly(EffectiveAuthority.DataClasses.Take(AuthorityContractLimits.MaxDataClassesPerCeiling + 1).ToArray()),
        EffectiveAuthority.MaxTargetCount,
        EffectiveAuthority.MaxSideEffectClass,
        EffectiveAuthority.AllowsRecurrence,
        EffectiveAuthority.AllowsExternalPublication,
        EffectiveAuthority.AllowsIrreversibleAction);
    /// <summary>Gets a defensively copied capability-admission snapshot.</summary>
    public CapabilityAdmissionSnapshot CapabilityAdmission { get; } = CapabilityAdmission is null ? null! : new(
        CapabilityAdmission.SchemaVersion,
        CapabilityAdmission.WorkspaceScopeId,
        CapabilityAdmission.Requirements,
        CapabilityAdmission.RequirementsHash,
        Array.AsReadOnly(CapabilityAdmission.Pins.Take(CapabilityContractLimits.MaxCapabilityAdmissionPins + 1).ToArray()),
        Array.AsReadOnly(CapabilityAdmission.Evidence.Take(CapabilityContractLimits.MaxCapabilityAdmissionEvidenceEntries + 1).ToArray()),
        CapabilityAdmission.AdmittedAtUtc);
}
