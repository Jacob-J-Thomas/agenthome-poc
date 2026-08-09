using System.Text.Json;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Startup.Capabilities.Models;
using StartupCapabilityPostureError = EmbodySense.Core.Startup.Capabilities.Models.CapabilityPostureError;

namespace EmbodySense.Core.Startup.Capabilities;

/// <summary>Exposes safe capability catalog inspection and exact confirmed lifecycle mutation through one facade.</summary>
/// <remarks>
/// Browser and other interface callers select only public capability identities, operations, and optional versions.
/// Trusted descriptors, artifact digests, dependency evidence, workspace identity, and mutation authority remain
/// server-owned. Confirmation replays the durable preview by operation identity and compares every observed revision
/// and hash before the underlying service recaptures current dependencies and applies the mutation.
/// </remarks>
public sealed class CapabilityCatalogFacade : ICapabilityCatalogFacade
{
    private readonly CapabilityPostureFacade _posture;
    private readonly CapabilityLifecycleSelectionService _lifecycle;

    /// <summary>Creates a workspace-bound facade using the server account's default capability trust root.</summary>
    /// <param name="workingDirectory">The exact initialized workspace root.</param>
    public CapabilityCatalogFacade(string workingDirectory) : this(CreateDefaultComposition(workingDirectory))
    {
    }

    /// <summary>Creates a workspace-bound facade over an explicit server-owned file trust root.</summary>
    /// <param name="workingDirectory">The exact initialized workspace root.</param>
    /// <param name="trustRootPath">The server-owned trust root kept outside mutable workspace storage.</param>
    /// <returns>The safe surface-neutral catalog and lifecycle facade.</returns>
    public static CapabilityCatalogFacade ForFileCapabilityTrustRoot(string workingDirectory, string trustRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustRootPath);
        return new CapabilityCatalogFacade(CreateComposition(workingDirectory, new FileCapabilityCatalogTrustProvider(trustRootPath)));
    }

    private CapabilityCatalogFacade((CapabilityPostureFacade Posture, CapabilityLifecycleSelectionService Lifecycle) composition) : this(composition.Posture, composition.Lifecycle)
    {
    }

    private CapabilityCatalogFacade(CapabilityPostureFacade posture, CapabilityLifecycleSelectionService lifecycle)
    {
        ArgumentNullException.ThrowIfNull(posture);
        ArgumentNullException.ThrowIfNull(lifecycle);
        _posture = posture;
        _lifecycle = lifecycle;
    }

    /// <inheritdoc />
    public Task<CapabilityPostureCatalogResponse> ReadCatalogAsync(string? startAfterId, int maximumCount, CancellationToken cancellationToken = default) => _posture.ReadCatalogAsync(startAfterId, maximumCount, cancellationToken);

    /// <inheritdoc />
    public Task<CapabilityPostureResponse> ReadAsync(string capabilityId, CancellationToken cancellationToken = default) => _posture.ReadAsync(capabilityId, cancellationToken);

    /// <inheritdoc />
    public async Task<CapabilityLifecyclePreviewResponse> PreviewAsync(CapabilityLifecycleSelectionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!TryMap(input, out var selection))
        {
            return InvalidPreview();
        }

        var result = await _lifecycle.PreviewAsync(selection!, cancellationToken);
        var isReady = result.Status == CapabilityLifecycleSelectionStatus.Ready && result.Preview is not null;
        return new CapabilityLifecyclePreviewResponse(
            isReady ? "ready" : Token(result.Status),
            isReady ? Map(result.Preview!) : null,
            isReady ? null : new StartupCapabilityPostureError(ErrorCode(result.Status), result.Detail));
    }

    /// <inheritdoc />
    public async Task<CapabilityLifecycleMutationResponse> ConfirmAsync(CapabilityLifecycleConfirmationInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var selectionInput = new CapabilityLifecycleSelectionInput(input.OperationId, input.Operation, input.CapabilityId, input.TargetVersion);
        if (!input.Confirmed
            || !TryMap(selectionInput, out var selection)
            || input.BaselineCatalogRevision < 0
            || input.BaselineActivationRevision < 0
            || input.LifecycleRevision < 0
            || input.DependentSetRevision < 0
            || !CapabilityIntegrityDigest.TryParse(input.DependentSetHash, out _, out _)
            || !CapabilityIntegrityDigest.TryParse(input.PreviewHash, out _, out _))
        {
            return InvalidMutation("The lifecycle confirmation is outside the bounded contract.");
        }

        var replay = await _lifecycle.PreviewAsync(selection!, cancellationToken);
        if (replay.Status != CapabilityLifecycleSelectionStatus.Ready || replay.Preview is null)
        {
            return new CapabilityLifecycleMutationResponse(Token(replay.Status), false, null, null, null, false, replay.Detail);
        }

        var preview = replay.Preview;
        if (preview.BaselineCatalogRevision != input.BaselineCatalogRevision
            || preview.BaselineActivationRevision != input.BaselineActivationRevision
            || preview.LifecycleRevision != input.LifecycleRevision
            || preview.DependentSetRevision != input.DependentSetRevision
            || !string.Equals(preview.DependentSetHash, input.DependentSetHash, StringComparison.Ordinal)
            || !string.Equals(preview.PreviewHash, input.PreviewHash, StringComparison.Ordinal))
        {
            return new CapabilityLifecycleMutationResponse("conflict", false, null, null, preview.LifecycleRevision, false, "The confirmed lifecycle preview does not match the current durable preview identity.");
        }

        var result = await _lifecycle.MutateAsync(preview, cancellationToken);
        var effectiveOutcome = result.ReplayedOutcome ?? (result.Status == CapabilityLifecycleMutationStatus.Replayed ? CapabilityLifecycleMutationStatus.Applied : result.Status);
        return new CapabilityLifecycleMutationResponse(
            Token(result.Status),
            effectiveOutcome == CapabilityLifecycleMutationStatus.Applied,
            result.ReplayedOutcome is null ? null : Token(result.ReplayedOutcome.Value),
            result.State is null ? null : new CapabilityLifecycleMutationStateSnapshot(result.State.Descriptor.Id.Value, result.State.Descriptor.Version.Value, result.State.IsEnabled, result.State.IsRemoved, result.State.Revision, result.State.UpdatedAtUtc),
            result.LifecycleRevision,
            result.OutcomeAuditPending,
            result.Detail);
    }

    private static (CapabilityPostureFacade Posture, CapabilityLifecycleSelectionService Lifecycle) CreateDefaultComposition(string workingDirectory)
    {
        var catalogTrust = FileCapabilityCatalogTrustProvider.CreateDefault();
        return CreateComposition(workingDirectory, catalogTrust);
    }

    private static (CapabilityPostureFacade Posture, CapabilityLifecycleSelectionService Lifecycle) CreateComposition(string workingDirectory, FileCapabilityCatalogTrustProvider catalogTrust)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(catalogTrust);
        var paths = new WorkspacePaths(workingDirectory);
        catalogTrust.RequireDisjointWorkspace(paths.RootPath);
        var posture = CapabilityPostureFacade.ForFileCapabilityTrustRoot(workingDirectory, catalogTrust.RootPath);
        var artifactTrust = new FileCapabilityArtifactStateTrustProvider(catalogTrust.RootPath);
        var lifecycle = CapabilityLifecycleFactory.CreateSelection(paths, catalogTrust, artifactTrust, UnavailableCapabilityArtifactTrustVerifier.Instance, new AuditLog(paths));
        return (posture, lifecycle);
    }

    private static bool TryMap(CapabilityLifecycleSelectionInput input, out CapabilityLifecycleSelectionRequest? request)
    {
        request = null;
        if (!CapabilityId.TryParse(input.CapabilityId, out var capabilityId, out _) || !TryParseOperation(input.Operation, out var operation) || !TryParseTargetVersion(operation, input.TargetVersion, out var targetVersion))
        {
            return false;
        }

        request = new CapabilityLifecycleSelectionRequest(input.OperationId, operation, capabilityId!, targetVersion);
        return true;
    }

    private static bool TryParseOperation(string? value, out CapabilityLifecycleOperationKind operation)
    {
        operation = value switch
        {
            "enable" => CapabilityLifecycleOperationKind.Enable,
            "disable" => CapabilityLifecycleOperationKind.Disable,
            "upgrade" => CapabilityLifecycleOperationKind.Upgrade,
            "rollback" => CapabilityLifecycleOperationKind.Rollback,
            "remove" => CapabilityLifecycleOperationKind.Remove,
            _ => default
        };
        return operation != default;
    }

    private static bool TryParseTargetVersion(CapabilityLifecycleOperationKind operation, string? value, out CapabilityVersion? version)
    {
        if (operation is CapabilityLifecycleOperationKind.Enable or CapabilityLifecycleOperationKind.Upgrade)
        {
            if (value is null)
            {
                version = null;
                return operation == CapabilityLifecycleOperationKind.Enable;
            }

            return CapabilityVersion.TryParse(value, out version, out _);
        }

        version = null;
        return value is null;
    }

    private static CapabilityLifecyclePreviewSnapshot Map(CapabilityLifecyclePreview preview)
    {
        var impacts = preview.Impacts.Select(impact => new CapabilityLifecycleImpactSnapshot(Token(impact.DependentKind), impact.DependentIdentity, impact.DependentRevision, Token(impact.RequirementKind), impact.CompatibleVersionRange, impact.IsCompatible, Token(impact.AuthorityPosture), Token(impact.Outcome))).ToArray();
        return new CapabilityLifecyclePreviewSnapshot(
            preview.OperationId,
            Token(preview.Kind),
            preview.CapabilityId.Value,
            preview.TargetDescriptor?.Version.Value,
            preview.BaselineCatalogRevision,
            preview.BaselineActivationRevision,
            preview.LifecycleRevision,
            preview.DependentSetRevision,
            preview.DependentSetHash,
            preview.PreviewHash,
            preview.Impacts.Any(impact => impact.Outcome == CapabilityLifecycleImpactOutcome.Blocked),
            preview.Impacts.Any(impact => impact.Outcome == CapabilityLifecycleImpactOutcome.Degraded),
            impacts,
            preview.Detail);
    }

    private static CapabilityLifecyclePreviewResponse InvalidPreview() => new("invalid", null, new StartupCapabilityPostureError("invalid_capability_lifecycle_selection", "The lifecycle selection is outside the bounded contract."));

    private static CapabilityLifecycleMutationResponse InvalidMutation(string detail) => new("invalid", false, null, null, null, false, detail);

    private static string ErrorCode(CapabilityLifecycleSelectionStatus status) => status switch
    {
        CapabilityLifecycleSelectionStatus.NotFound => "capability_lifecycle_target_not_found",
        CapabilityLifecycleSelectionStatus.Ambiguous => "capability_lifecycle_target_ambiguous",
        CapabilityLifecycleSelectionStatus.Conflict => "capability_lifecycle_conflict",
        CapabilityLifecycleSelectionStatus.Invalid => "invalid_capability_lifecycle_selection",
        _ => "capability_lifecycle_unavailable"
    };

    private static string Token<T>(T value) where T : struct, Enum => JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString());
}
