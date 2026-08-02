using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.Core.Startup.Capabilities;

/// <summary>Composes the authenticated capability lifecycle boundary over real loop, skill, package, catalog, artifact, and audit adapters.</summary>
public static class CapabilityLifecycleFactory
{
    /// <summary>Creates the lifecycle service without reading or mutating workspace state.</summary>
    /// <param name="paths">The initialized workspace paths.</param>
    /// <param name="catalogTrustProvider">The server-owned catalog and lifecycle trust provider.</param>
    /// <param name="artifactStateTrustProvider">The server-owned immutable artifact trust provider.</param>
    /// <param name="artifactTrustVerifier">The server-owned artifact policy verifier.</param>
    /// <param name="auditLog">The append-only workspace audit log.</param>
    /// <param name="roleSource">The optional explicitly registered future role-domain adapter.</param>
    /// <param name="scheduleSource">The optional explicitly registered future schedule-domain adapter.</param>
    /// <returns>The fully composed capability lifecycle service.</returns>
    public static CapabilityLifecycleService Create(
        WorkspacePaths paths,
        ICapabilityCatalogTrustProvider catalogTrustProvider,
        ICapabilityArtifactStateTrustProvider artifactStateTrustProvider,
        ICapabilityArtifactTrustVerifier artifactTrustVerifier,
        IAuditLog auditLog,
        IRoleCapabilityDependentIndexSource? roleSource = null,
        IScheduleCapabilityDependentIndexSource? scheduleSource = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(catalogTrustProvider);
        ArgumentNullException.ThrowIfNull(artifactStateTrustProvider);
        ArgumentNullException.ThrowIfNull(artifactTrustVerifier);
        ArgumentNullException.ThrowIfNull(auditLog);
        var authorityTransaction = new CapabilityAuthorityTransaction(paths);
        var catalog = new CapabilityCatalogStore(paths, catalogTrustProvider, authorityTransaction: authorityTransaction);
        var baselineArtifacts = new CapabilityArtifactStore(paths, artifactStateTrustProvider, artifactTrustVerifier, authorityTransaction: authorityTransaction);
        var baseline = new CapabilityLifecycleBaselineSource(catalog, baselineArtifacts, authorityTransaction);
        var lifecycle = new CapabilityLifecycleMutationStore(paths, catalogTrustProvider, baseline, baselineArtifacts, authorityTransaction: authorityTransaction);
        var artifacts = new CapabilityArtifactStore(paths, artifactStateTrustProvider, artifactTrustVerifier, lifecycleStore: lifecycle, authorityTransaction: authorityTransaction);
        var loopSource = new LoopCapabilityDependentIndexSource(new LoopDefinitionStore(paths, authorityTransaction), new CustomLoopDefinitionStore(paths, authorityTransaction));
        var skillSource = new SkillCapabilityDependentIndexSource(new LocalSkillDependencyManifestDiscovery(paths));
        var packageSource = new CapabilityPackageDependentIndexSource(artifacts);
        var index = new CapabilityDependentIndex([loopSource, skillSource, packageSource], roleSource, scheduleSource);
        return new CapabilityLifecycleService(index, baseline, artifacts, lifecycle, auditLog, authorityTransaction);
    }
}
