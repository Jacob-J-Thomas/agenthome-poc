using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Governance.Audit;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Orchestrates verified source intake, immutable staging, and atomic activation without granting runtime authority.</summary>
public sealed class CapabilityArtifactIntakeService
{
    private readonly ILocalCapabilityArtifactSource _localSource;
    private readonly IRemoteCapabilityArtifactSource _remoteSource;
    private readonly ICapabilityArtifactTrustVerifier _trustVerifier;
    private readonly ICapabilityArtifactStore _store;
    private readonly ICapabilityExecutableHost _host;
    private readonly CapabilityPlatform _currentPlatform;
    private readonly CapabilityVersion _hostVersion;
    private readonly IAuditLog _auditLog;

    /// <summary>Creates the intake orchestrator over explicit source, trust, persistence, and executable-host ports.</summary>
    public CapabilityArtifactIntakeService(ILocalCapabilityArtifactSource localSource, IRemoteCapabilityArtifactSource remoteSource, ICapabilityArtifactTrustVerifier trustVerifier, ICapabilityArtifactStore store, ICapabilityExecutableHost host, CapabilityPlatform currentPlatform, CapabilityVersion hostVersion, IAuditLog auditLog)
    {
        ArgumentNullException.ThrowIfNull(localSource);
        ArgumentNullException.ThrowIfNull(remoteSource);
        ArgumentNullException.ThrowIfNull(trustVerifier);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(currentPlatform);
        ArgumentNullException.ThrowIfNull(hostVersion);
        ArgumentNullException.ThrowIfNull(auditLog);
        _localSource = localSource;
        _remoteSource = remoteSource;
        _trustVerifier = trustVerifier;
        _store = store;
        _host = host;
        _currentPlatform = currentPlatform;
        _hostVersion = hostVersion;
        _auditLog = auditLog;
    }

    /// <summary>Verifies, stages, and atomically activates one exact artifact.</summary>
    public async Task<CapabilityArtifactIntakeResult> IntakeAsync(CapabilityArtifactIntakeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var result = await IntakeCoreAsync(request, cancellationToken);
            if (result.Trust is not null)
            {
                await AppendVerificationAuditAsync(request, result, CancellationToken.None);
            }
            if (result.Activation is not null)
            {
                await AppendActivationAuditAsync(request, result, CancellationToken.None);
            }
            await AppendAuditAsync(request, result, CancellationToken.None);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = Result(CapabilityArtifactIntakeStatus.Unavailable, request.OperationId ?? string.Empty, null, null, "Artifact intake was cancelled before a new activation was proved.");
            await AppendAuditAsync(request, cancelled, CancellationToken.None);
            throw;
        }
    }

    /// <summary>Rolls back one proved activation through the audited lifecycle boundary.</summary>
    public async Task<CapabilityArtifactStoreResult> RollbackAsync(CapabilityId capabilityId, long expectedRevision, string operationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capabilityId);
        var result = await _store.RollbackAsync(capabilityId, expectedRevision, operationId, cancellationToken);
        var outcome = result.Status is CapabilityArtifactStoreStatus.Applied or CapabilityArtifactStoreStatus.Replayed ? AuditSchema.Outcomes.Succeeded : result.Status == CapabilityArtifactStoreStatus.Conflict ? AuditSchema.Outcomes.Conflict : AuditSchema.Outcomes.Failed;
        var metadata = new Dictionary<string, object?> { ["operationId"] = operationId, ["activationRevision"] = result.Activation?.Revision, ["artifactDigest"] = result.Activation?.ArtifactDigest.Value, ["transition"] = "rollback" };
        await _auditLog.AppendAsync(AuditEvent.Create(AuditSchema.Actors.CapabilityHost, AuditSchema.Actions.CapabilityArtifactActivation, capabilityId.Value, outcome, result.Detail.Length <= 512 ? result.Detail : result.Detail[..512], metadata), CancellationToken.None);
        return result;
    }

    private async Task<CapabilityArtifactIntakeResult> IntakeCoreAsync(CapabilityArtifactIntakeRequest request, CancellationToken cancellationToken)
    {
        var operationId = request.OperationId ?? string.Empty;
        var validation = CapabilityArtifactManifestValidator.Validate(request.Manifest);
        if (!validation.IsValid || !CapabilityArtifactManifestValidator.IsOperationId(operationId) || request.ExpectedActivationRevision < 0)
        {
            return Result(CapabilityArtifactIntakeStatus.Invalid, operationId, null, null, "The artifact intake request is invalid.");
        }

        var manifest = request.Manifest;
        if (!manifest.Platform.Equals(_currentPlatform) || !manifest.Descriptor.Compatibility.HostVersionRange.Contains(_hostVersion))
        {
            return Result(CapabilityArtifactIntakeStatus.Incompatible, operationId, null, null, "The artifact is incompatible with the current host platform or contract version.");
        }

        var availability = _host.CheckAvailability(manifest);
        if (availability.Status != CapabilityExecutableAvailabilityStatus.Available)
        {
            var status = availability.Status == CapabilityExecutableAvailabilityStatus.Incompatible ? CapabilityArtifactIntakeStatus.Incompatible : CapabilityArtifactIntakeStatus.RequirementsUnavailable;
            return Result(status, operationId, null, null, availability.Detail);
        }

        try
        {
            var trust = await _trustVerifier.VerifyAsync(manifest, manifest.Checksum, cancellationToken);
            if (trust.Status != CapabilityArtifactTrustStatus.Verified)
            {
                var status = trust.Status == CapabilityArtifactTrustStatus.Rejected ? CapabilityArtifactIntakeStatus.TrustRejected : CapabilityArtifactIntakeStatus.Unavailable;
                return Result(status, operationId, null, trust, trust.Detail);
            }

            var content = manifest.Source.Kind == CapabilityArtifactSourceKind.Local
                ? await _localSource.ReadAsync(manifest.Source, cancellationToken)
                : await _remoteSource.ReadAsync(manifest.Source, cancellationToken);
            if (content.Length is < 1 or > CapabilityArtifactManifestValidator.MaximumArtifactBytes)
            {
                return Result(CapabilityArtifactIntakeStatus.Invalid, operationId, null, trust, "The artifact payload is empty or exceeds the intake bound.");
            }

            var digest = CapabilityIntegrityDigest.Compute(content.ToArray());
            if (!manifest.Checksum.FixedTimeEquals(digest))
            {
                return Result(CapabilityArtifactIntakeStatus.IntegrityRejected, operationId, null, trust, "The artifact payload does not match its declared checksum.");
            }

            var staged = await _store.StageAsync(new CapabilityArtifactStageRequest(manifest, content, trust), cancellationToken);
            if (staged.Status is not CapabilityArtifactStoreStatus.Applied and not CapabilityArtifactStoreStatus.NoChange)
            {
                return Result(Map(staged.Status), operationId, staged.Activation, trust, staged.Detail);
            }

            var activated = await _store.ActivateAsync(new CapabilityArtifactActivationRequest(manifest, request.ExpectedActivationRevision, operationId), cancellationToken);
            return Result(Map(activated.Status), operationId, activated.Activation, trust, activated.Detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or UnauthorizedAccessException or FormatException)
        {
            return Result(CapabilityArtifactIntakeStatus.Unavailable, operationId, null, null, "Artifact intake infrastructure is unavailable; prior activation was preserved.");
        }
    }

    private static CapabilityArtifactIntakeStatus Map(CapabilityArtifactStoreStatus status) => status switch
    {
        CapabilityArtifactStoreStatus.Applied => CapabilityArtifactIntakeStatus.Activated,
        CapabilityArtifactStoreStatus.Replayed => CapabilityArtifactIntakeStatus.Replayed,
        CapabilityArtifactStoreStatus.Conflict => CapabilityArtifactIntakeStatus.Conflict,
        CapabilityArtifactStoreStatus.Invalid => CapabilityArtifactIntakeStatus.Invalid,
        _ => CapabilityArtifactIntakeStatus.Unavailable
    };

    private static CapabilityArtifactIntakeResult Result(CapabilityArtifactIntakeStatus status, string operationId, CapabilityArtifactActivation? activation, CapabilityArtifactTrustDecision? trust, string detail) => new(status, operationId, activation, trust, detail.Length <= 512 ? detail : detail[..512]);

    private Task AppendAuditAsync(CapabilityArtifactIntakeRequest request, CapabilityArtifactIntakeResult result, CancellationToken cancellationToken)
    {
        var manifest = request.Manifest;
        var target = manifest?.Descriptor?.Id?.Value ?? "capability-artifact";
        var metadata = new Dictionary<string, object?>
        {
            ["operationId"] = result.OperationId,
            ["status"] = result.Status.ToString(),
            ["artifactDigest"] = manifest?.Checksum?.Value,
            ["artifactVersion"] = manifest?.Descriptor?.Version?.Value,
            ["implementationId"] = manifest?.Descriptor?.Implementation?.ImplementationId,
            ["sourceKind"] = manifest?.Source?.Kind.ToString(),
            ["verifier"] = result.Trust?.Verifier,
            ["activationRevision"] = result.Activation?.Revision
        };
        var outcome = result.Status is CapabilityArtifactIntakeStatus.Activated or CapabilityArtifactIntakeStatus.Replayed ? AuditSchema.Outcomes.Succeeded : result.Status == CapabilityArtifactIntakeStatus.Conflict ? AuditSchema.Outcomes.Conflict : AuditSchema.Outcomes.Failed;
        return _auditLog.AppendAsync(AuditEvent.Create(AuditSchema.Actors.CapabilityHost, AuditSchema.Actions.CapabilityArtifactIntake, target, outcome, result.Detail, metadata), cancellationToken);
    }

    private Task AppendVerificationAuditAsync(CapabilityArtifactIntakeRequest request, CapabilityArtifactIntakeResult result, CancellationToken cancellationToken)
    {
        var trust = result.Trust!;
        var outcome = trust.Status == CapabilityArtifactTrustStatus.Verified ? AuditSchema.Outcomes.Succeeded : AuditSchema.Outcomes.Failed;
        var metadata = new Dictionary<string, object?> { ["operationId"] = result.OperationId, ["artifactDigest"] = request.Manifest?.Checksum?.Value, ["verifier"] = trust.Verifier, ["status"] = trust.Status.ToString() };
        return _auditLog.AppendAsync(AuditEvent.Create(AuditSchema.Actors.CapabilityHost, AuditSchema.Actions.CapabilityArtifactVerification, request.Manifest?.Descriptor?.Id?.Value ?? "capability-artifact", outcome, result.Detail.Length <= 512 ? result.Detail : result.Detail[..512], metadata), cancellationToken);
    }

    private Task AppendActivationAuditAsync(CapabilityArtifactIntakeRequest request, CapabilityArtifactIntakeResult result, CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, object?> { ["operationId"] = result.OperationId, ["artifactDigest"] = result.Activation!.ArtifactDigest.Value, ["activationRevision"] = result.Activation.Revision, ["transition"] = "activate" };
        var outcome = result.Status is CapabilityArtifactIntakeStatus.Activated or CapabilityArtifactIntakeStatus.Replayed ? AuditSchema.Outcomes.Succeeded : AuditSchema.Outcomes.Failed;
        return _auditLog.AppendAsync(AuditEvent.Create(AuditSchema.Actors.CapabilityHost, AuditSchema.Actions.CapabilityArtifactActivation, request.Manifest?.Descriptor?.Id?.Value ?? "capability-artifact", outcome, result.Detail.Length <= 512 ? result.Detail : result.Detail[..512], metadata), cancellationToken);
    }
}
