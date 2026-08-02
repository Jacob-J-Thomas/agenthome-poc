using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Turns browser-safe lifecycle selections into exact server-owned durable previews.</summary>
public sealed class CapabilityLifecycleSelectionService
{
    private readonly ICapabilityLifecycleTargetResolver _targetResolver;
    private readonly CapabilityLifecycleService _lifecycle;

    /// <summary>Creates the selection orchestrator over target resolution and the existing audited lifecycle service.</summary>
    public CapabilityLifecycleSelectionService(ICapabilityLifecycleTargetResolver targetResolver, CapabilityLifecycleService lifecycle)
    {
        ArgumentNullException.ThrowIfNull(targetResolver);
        ArgumentNullException.ThrowIfNull(lifecycle);
        _targetResolver = targetResolver;
        _lifecycle = lifecycle;
    }

    /// <summary>Resolves any artifact-bearing target server-side and persists the existing full lifecycle preview.</summary>
    public async Task<CapabilityLifecycleSelectionResult> PreviewAsync(CapabilityLifecycleSelectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CapabilityId is null || !Enum.IsDefined(request.Kind) || !CapabilityArtifactManifestValidator.IsOperationId(request.OperationId) || request.TargetVersion is not null && request.Kind is not CapabilityLifecycleOperationKind.Enable and not CapabilityLifecycleOperationKind.Upgrade)
        {
            return new CapabilityLifecycleSelectionResult(CapabilityLifecycleSelectionStatus.Invalid, null, "The lifecycle selection identity, kind, capability, or target-version combination is invalid.");
        }

        var replay = await _lifecycle.TryReplaySelectionAsync(request, cancellationToken);
        if (replay.Status != CapabilityLifecyclePreviewStatus.NotFound)
        {
            return Map(replay);
        }

        CapabilityLifecyclePreviewRequest previewRequest;
        if (request.Kind is CapabilityLifecycleOperationKind.Enable or CapabilityLifecycleOperationKind.Upgrade)
        {
            var resolution = await _targetResolver.ResolveAsync(new CapabilityLifecycleTargetResolutionRequest(request.Kind, request.CapabilityId, request.TargetVersion), cancellationToken);
            if (resolution.Status != CapabilityLifecycleTargetResolutionStatus.Available || resolution.Descriptor is null || resolution.ArtifactDigest is null)
            {
                var status = resolution.Status switch
                {
                    CapabilityLifecycleTargetResolutionStatus.NotFound => CapabilityLifecycleSelectionStatus.NotFound,
                    CapabilityLifecycleTargetResolutionStatus.Ambiguous => CapabilityLifecycleSelectionStatus.Ambiguous,
                    _ => CapabilityLifecycleSelectionStatus.Unavailable
                };
                return new CapabilityLifecycleSelectionResult(status, null, resolution.Detail);
            }
            previewRequest = new CapabilityLifecyclePreviewRequest(request.OperationId, request.Kind, request.CapabilityId, resolution.Descriptor, resolution.ArtifactDigest, request);
        }
        else
        {
            previewRequest = new CapabilityLifecyclePreviewRequest(request.OperationId, request.Kind, request.CapabilityId, Selection: request);
        }

        var preview = await _lifecycle.PreviewAsync(previewRequest, cancellationToken);
        return Map(preview);
    }

    private static CapabilityLifecycleSelectionResult Map(CapabilityLifecyclePreview preview)
    {
        var selectionStatus = preview.Status switch
        {
            CapabilityLifecyclePreviewStatus.Ready or CapabilityLifecyclePreviewStatus.Replayed => CapabilityLifecycleSelectionStatus.Ready,
            CapabilityLifecyclePreviewStatus.NotFound => CapabilityLifecycleSelectionStatus.NotFound,
            CapabilityLifecyclePreviewStatus.Invalid => CapabilityLifecycleSelectionStatus.Invalid,
            CapabilityLifecyclePreviewStatus.Conflict => CapabilityLifecycleSelectionStatus.Conflict,
            _ => CapabilityLifecycleSelectionStatus.Unavailable
        };
        return new CapabilityLifecycleSelectionResult(selectionStatus, preview, preview.Detail);
    }

    /// <summary>Confirms one opaque server-retained preview through the existing durable lifecycle service.</summary>
    /// <remarks>Surface adapters must not reconstruct or accept this trusted preview from browser fields.</remarks>
    public Task<CapabilityLifecycleMutationResult> MutateAsync(CapabilityLifecyclePreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return _lifecycle.MutateAsync(preview, cancellationToken);
    }
}
