using System.Text.Json;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Startup.Capabilities.Models;

namespace EmbodySense.Core.Startup.Capabilities;

/// <summary>Exposes bounded redacted capability posture through one workspace-bound surface-neutral facade.</summary>
/// <remarks>
/// Construction and every query are read-only. Human and administrative methods expose safe declared posture without
/// conferring execution permission. Model context is a separate exact-admission query that never projects the ambient
/// catalog, unassigned capabilities, secret values, private configuration, or lower-layer diagnostics.
/// </remarks>
public sealed class CapabilityPostureFacade
{
    private readonly CapabilityPostureService _service;

    /// <summary>Creates a facade using the server account's default capability trust root.</summary>
    /// <param name="workingDirectory">The exact workspace root whose posture may be queried.</param>
    public CapabilityPostureFacade(string workingDirectory) : this(workingDirectory, FileCapabilityCatalogTrustProvider.CreateDefault())
    {
    }

    private CapabilityPostureFacade(string workingDirectory, FileCapabilityCatalogTrustProvider catalogTrustProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(catalogTrustProvider);
        var paths = new WorkspacePaths(workingDirectory);
        var catalog = new CapabilityCatalogStore(paths, catalogTrustProvider);
        var lifecycle = new CapabilityLifecycleMutationStore(paths, catalogTrustProvider);
        var artifacts = new CapabilityArtifactStore(paths, new FileCapabilityArtifactStateTrustProvider(catalogTrustProvider.RootPath), UnavailableCapabilityArtifactTrustVerifier.Instance, lifecycleStore: lifecycle);
        var dependents = new CapabilityDependentIndex(
        [
            new LoopCapabilityDependentIndexSource(new LoopDefinitionStore(paths), new CustomLoopDefinitionStore(paths)),
            new SkillCapabilityDependentIndexSource(new LocalSkillDependencyManifestDiscovery(paths)),
            new CapabilityPackageDependentIndexSource(artifacts)
        ]);
        var admission = CapabilityAdmissionFactory.Create(paths, catalogTrustProvider);
        _service = new CapabilityPostureService(catalog, lifecycle, dependents, admission, CapabilityHostRuntime.HostContractVersion, CapabilityHostRuntime.Platform);
    }

    /// <summary>Creates a facade over an explicit server-owned file trust root.</summary>
    /// <param name="workingDirectory">The exact workspace root whose posture may be queried.</param>
    /// <param name="trustRootPath">The server-owned trust root kept outside mutable workspace storage.</param>
    /// <returns>A read-only capability posture facade.</returns>
    public static CapabilityPostureFacade ForFileCapabilityTrustRoot(string workingDirectory, string trustRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustRootPath);
        return new CapabilityPostureFacade(workingDirectory, new FileCapabilityCatalogTrustProvider(trustRootPath));
    }

    /// <summary>Reads one bounded deterministic administrative catalog page.</summary>
    /// <param name="startAfterId">The optional exclusive canonical identifier cursor.</param>
    /// <param name="maximumCount">The requested page size from one through fifty.</param>
    /// <param name="cancellationToken">The token used to cancel posture reads.</param>
    /// <returns>A safe surface-neutral page that grants no authority.</returns>
    public async Task<CapabilityPostureCatalogResponse> ReadCatalogAsync(string? startAfterId, int maximumCount, CancellationToken cancellationToken = default)
    {
        var result = await _service.ReadCatalogAsync(startAfterId, maximumCount, cancellationToken);
        return new CapabilityPostureCatalogResponse(Token(result.Status), result.CatalogRevision, result.Entries.Select(Map).ToArray(), result.NextCursor, Map(result.Error));
    }

    /// <summary>Reads one exact safe administrative capability posture.</summary>
    /// <param name="capabilityId">The canonical capability identity.</param>
    /// <param name="cancellationToken">The token used to cancel posture reads.</param>
    /// <returns>The exact posture or a stable non-sensitive error.</returns>
    public async Task<CapabilityPostureResponse> ReadAsync(string capabilityId, CancellationToken cancellationToken = default)
    {
        if (!CapabilityId.TryParse(capabilityId, out var parsed, out _))
        {
            return new CapabilityPostureResponse("invalid", null, InvalidError());
        }
        var result = await _service.ReadAsync(parsed!, cancellationToken);
        return new CapabilityPostureResponse(Token(result.Status), result.Posture is null ? null : Map(result.Posture), Map(result.Error));
    }

    /// <summary>Computes one read-only lifecycle impact projection without persisting preview or mutation authority.</summary>
    /// <param name="capabilityId">The canonical capability identity.</param>
    /// <param name="operation">One of upgrade, rollback, disable, or remove.</param>
    /// <param name="targetVersion">The exact replacement version required only for upgrade.</param>
    /// <param name="cancellationToken">The token used to cancel posture reads.</param>
    /// <returns>The bounded impact posture or a stable non-sensitive error.</returns>
    public async Task<CapabilityPosturePreviewResponse> PreviewAsync(string capabilityId, string operation, string? targetVersion = null, CancellationToken cancellationToken = default)
    {
        if (!CapabilityId.TryParse(capabilityId, out var parsedId, out _) || !TryParseOperation(operation, out var parsedOperation) || !TryParseTargetVersion(parsedOperation, targetVersion, out var parsedVersion))
        {
            return new CapabilityPosturePreviewResponse("invalid", null, InvalidError());
        }
        var result = await _service.PreviewAsync(new CapabilityPosturePreviewQuery(parsedId!, parsedOperation, parsedVersion), cancellationToken);
        return new CapabilityPosturePreviewResponse(Token(result.Status), result.Preview is null ? null : Map(result.Preview), Map(result.Error));
    }

    /// <summary>Builds canonical model context from exact admitted pins filtered by assignment and current narrower authority.</summary>
    /// <param name="admission">The immutable admitted capability evidence.</param>
    /// <param name="assignedCapabilityIds">The exact admitted loop or node assignment visible for this inference.</param>
    /// <param name="currentAuthorityCapabilityIds">The current narrower authority ceiling.</param>
    /// <param name="cancellationToken">The token used to cancel revalidation.</param>
    /// <returns>Deterministic bounded model context or a stable non-leaking error.</returns>
    public async Task<CapabilityModelPostureResponse> ReadModelContextAsync(CapabilityAdmissionSnapshot admission, IReadOnlyCollection<string> assignedCapabilityIds, IReadOnlyCollection<string> currentAuthorityCapabilityIds, CancellationToken cancellationToken = default)
    {
        var result = await _service.ReadModelContextAsync(admission, assignedCapabilityIds, currentAuthorityCapabilityIds, cancellationToken);
        var capabilities = result.Capabilities.Select(item => new CapabilityModelPostureSnapshot(item.Id, item.Version, item.Kind, item.Description)).ToArray();
        return new CapabilityModelPostureResponse(Token(result.Status), capabilities, result.CanonicalJson, Map(result.Error));
    }

    private static CapabilityPostureSnapshot Map(CapabilityPostureProjection item)
    {
        return new CapabilityPostureSnapshot(
            item.Id,
            item.Version,
            item.DescriptorHash,
            item.Kind,
            item.Purpose,
            item.ProviderId,
            item.ImplementationId,
            item.ProvenanceKind,
            item.SourceUri,
            item.SourceRevision,
            item.Integrity,
            item.HostVersionRange,
            item.SupportedPlatforms,
            item.IsCurrentHostCompatible,
            item.SideEffectClass,
            item.DataClasses,
            item.EgressMode,
            item.EgressDestinations,
            item.SecretRequirements,
            Token(item.State),
            item.Declaration,
            item.Installation,
            item.Enablement,
            item.Health,
            item.Retirement,
            item.Trust,
            item.IsLifecycleEnabled,
            item.IsRemoved,
            item.EntryRevision,
            item.LifecycleRevision,
            item.IsRecovered,
            item.Dependents.Select(Map).ToArray(),
            item.AreDependentsAvailable,
            item.DependentsTruncated);
    }

    private static CapabilityPostureDependentSnapshot Map(CapabilityPostureDependentProjection item)
    {
        return new CapabilityPostureDependentSnapshot(Token(item.Kind), item.Identity, item.Revision, Token(item.RequirementKind), item.CompatibleVersionRange, Token(item.AuthorityPosture));
    }

    private static CapabilityPosturePreviewSnapshot Map(CapabilityPosturePreviewProjection item)
    {
        var impacts = item.Impacts.Select(impact => new CapabilityPosturePreviewImpactSnapshot(Map(impact.Dependent), impact.IsCompatible, Token(impact.Outcome))).ToArray();
        return new CapabilityPosturePreviewSnapshot(item.CapabilityId, Token(item.Operation), item.CurrentVersion, item.TargetVersion, item.DependentSetHash, item.IsBlocked, item.HasDegradation, impacts, item.ImpactsTruncated);
    }

    private static Models.CapabilityPostureError? Map(Application.Capabilities.Models.CapabilityPostureError? error)
    {
        return error is null ? null : new Models.CapabilityPostureError(error.Code, error.Message);
    }

    private static bool TryParseOperation(string? value, out CapabilityLifecycleOperationKind operation)
    {
        operation = value switch
        {
            "upgrade" => CapabilityLifecycleOperationKind.Upgrade,
            "rollback" => CapabilityLifecycleOperationKind.Rollback,
            "disable" => CapabilityLifecycleOperationKind.Disable,
            "remove" => CapabilityLifecycleOperationKind.Remove,
            _ => default
        };
        return operation != default;
    }

    private static bool TryParseTargetVersion(CapabilityLifecycleOperationKind operation, string? value, out CapabilityVersion? version)
    {
        if (operation == CapabilityLifecycleOperationKind.Upgrade)
        {
            return CapabilityVersion.TryParse(value, out version, out _);
        }
        version = null;
        return value is null;
    }

    private static Models.CapabilityPostureError InvalidError() => new("invalid_capability_posture_request", "The capability posture request is outside the bounded contract.");

    private static string Token<T>(T value) where T : struct, Enum => JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString());
}
