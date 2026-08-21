using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Admission;

namespace EmbodySense.Core.Startup.Inference.Profiles;

/// <summary>Admits explicit empty routing only when a host has not supplied model-profile sources.</summary>
/// <remarks>
/// This fail-closed composition seam preserves canonical empty evidence for graphs without reachable Inference nodes.
/// It never invents a profile, trusts ambient host options, or permits an Inference node without the canonical catalog.
/// </remarks>
internal sealed class UnconfiguredModelRoutingAdmissionService : IGovernedModelRoutingAdmissionService
{
    /// <inheritdoc />
    public Task<GovernedModelRoutingAdmissionResult> AdmitAsync(GovernedModelRoutingAdmissionRequest? request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request?.Seed is null || request.Nodes is null)
        {
            return Task.FromResult(new GovernedModelRoutingAdmissionResult(GovernedModelRoutingAdmissionStatus.Invalid, null));
        }
        if (request.Nodes.Count != 0)
        {
            return Task.FromResult(new GovernedModelRoutingAdmissionResult(GovernedModelRoutingAdmissionStatus.Unavailable, null));
        }

        try
        {
            var seed = request.Seed;
            var snapshot = GovernedLoopAdmissionContractHash.CreateEmptyModelRoutingAdmission(
                seed.Intent,
                seed.Binding,
                seed.GrantProfile,
                seed.GrantBoundary,
                seed.GrantDependencyEvidenceHash,
                seed.EffectiveAuthority,
                seed.CapabilityAdmission,
                seed.EvaluatedAtUtc);
            return Task.FromResult(new GovernedModelRoutingAdmissionResult(GovernedModelRoutingAdmissionStatus.Admitted, snapshot));
        }
        catch (ArgumentException)
        {
            return Task.FromResult(new GovernedModelRoutingAdmissionResult(GovernedModelRoutingAdmissionStatus.Invalid, null));
        }
    }
}
