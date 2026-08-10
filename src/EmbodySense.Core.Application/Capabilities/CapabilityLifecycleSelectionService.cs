using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;

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
            var resolutionRequest = new CapabilityLifecycleTargetResolutionRequest(request.Kind, request.CapabilityId, request.TargetVersion);
            var resolution = request.Kind == CapabilityLifecycleOperationKind.Enable
                ? await _lifecycle.ResolveCurrentEnableTargetAsync(resolutionRequest, cancellationToken)
                : await _targetResolver.ResolveAsync(resolutionRequest, cancellationToken);
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

    /// <summary>Retires the exact persisted preview only when every caller-observed identity still matches.</summary>
    public async Task<CapabilityLifecycleMutationResult> DiscardAsync(CapabilityLifecycleDispositionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var selection = request.Selection;
        if (selection is null
            || request.BaselineCatalogRevision < 0
            || request.BaselineActivationRevision < 0
            || request.LifecycleRevision < 1
            || request.DependentSetRevision < 0
            || !CapabilityIntegrityDigest.TryParse(request.DependentSetHash, out _, out _)
            || !CapabilityIntegrityDigest.TryParse(request.PreviewHash, out _, out _))
        {
            return new CapabilityLifecycleMutationResult(CapabilityLifecycleMutationStatus.Invalid, null, null, false, "The lifecycle discard identity is outside the bounded contract.");
        }

        var replay = await _lifecycle.TryReplaySelectionAsync(selection, cancellationToken);
        if (replay.Status == CapabilityLifecyclePreviewStatus.NotFound)
        {
            return new CapabilityLifecycleMutationResult(CapabilityLifecycleMutationStatus.Discarded, null, null, false, "No durable preview remains for this exact lifecycle selection.");
        }
        if (replay.Status != CapabilityLifecyclePreviewStatus.Replayed)
        {
            var status = replay.Status switch
            {
                CapabilityLifecyclePreviewStatus.Invalid => CapabilityLifecycleMutationStatus.Invalid,
                CapabilityLifecyclePreviewStatus.Conflict => CapabilityLifecycleMutationStatus.Conflict,
                CapabilityLifecyclePreviewStatus.NotFound => CapabilityLifecycleMutationStatus.NotFound,
                _ => CapabilityLifecycleMutationStatus.Unavailable
            };
            return new CapabilityLifecycleMutationResult(status, null, replay.LifecycleRevision > 0 ? replay.LifecycleRevision : null, false, replay.Detail);
        }
        if (replay.BaselineCatalogRevision != request.BaselineCatalogRevision
            || replay.BaselineActivationRevision != request.BaselineActivationRevision
            || replay.LifecycleRevision != request.LifecycleRevision
            || replay.DependentSetRevision != request.DependentSetRevision
            || !string.Equals(replay.DependentSetHash, request.DependentSetHash, StringComparison.Ordinal)
            || !string.Equals(replay.PreviewHash, request.PreviewHash, StringComparison.Ordinal))
        {
            return new CapabilityLifecycleMutationResult(CapabilityLifecycleMutationStatus.Conflict, null, replay.LifecycleRevision, false, "The lifecycle discard does not match the exact durable preview identity.");
        }

        return await _lifecycle.DiscardAsync(replay, cancellationToken);
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
