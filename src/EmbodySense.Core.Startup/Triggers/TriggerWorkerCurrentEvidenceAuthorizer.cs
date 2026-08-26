using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Triggers.Models;

namespace EmbodySense.Core.Startup.Triggers;

/// <summary>Re-reads one governed trigger target's exact current authority and adapter posture before dispatch.</summary>
/// <remarks>
/// All mutable observations occur under the composition-owned reentrant capability-authority fence. The authorizer
/// never grants authority from delivery evidence, follows a replacement revision, or mutates a queue, catalog, grant,
/// publication, or profile. Its successful proof intentionally excludes evaluation time so an unchanged retained
/// schedule-overlap retry receives the same authority identity.
/// </remarks>
public sealed class TriggerWorkerCurrentEvidenceAuthorizer : ITriggerWorkerCurrentEvidenceAuthorizer
{
    private const int CatalogPageSize = 100;
    private const int MaximumCatalogEntries = 512;
    private const string EvidenceDomain = "embodysense-trigger-worker-current-evidence-v1";
    private readonly string _workspaceId;
    private readonly string _triggerWorkspaceId;
    private readonly IGovernedLoopGrantBindingSource _targetSource;
    private readonly IAuthorityGrantResolver _grantResolver;
    private readonly IAuthorityGrantProfileSource _profileSource;
    private readonly ICapabilityCatalogStore _capabilityCatalog;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates an authorizer over composition-owned canonical authority sources for one workspace.</summary>
    /// <param name="workspaceId">The canonical <c>workspace-sha256:</c> workspace identity.</param>
    /// <param name="targetSource">The exact governed publication and role-binding source.</param>
    /// <param name="grantResolver">The exact immutable authority-grant resolver.</param>
    /// <param name="profileSource">The exact authority-profile revision source.</param>
    /// <param name="capabilityCatalog">The lifecycle-projected capability catalog.</param>
    /// <param name="authorityTransaction">The shared reentrant fence for every mutable observation.</param>
    /// <param name="timeProvider">The trusted current UTC clock.</param>
    /// <exception cref="ArgumentException"><paramref name="workspaceId"/> is not canonical.</exception>
    /// <exception cref="ArgumentNullException">A required source is <see langword="null"/>.</exception>
    public TriggerWorkerCurrentEvidenceAuthorizer(
        string workspaceId,
        IGovernedLoopGrantBindingSource targetSource,
        IAuthorityGrantResolver grantResolver,
        IAuthorityGrantProfileSource profileSource,
        ICapabilityCatalogStore capabilityCatalog,
        ICapabilityAuthorityTransaction authorityTransaction,
        TimeProvider? timeProvider = null)
    {
        if (!ContextualRoleWorkspaceId.IsValid(workspaceId))
        {
            throw new ArgumentException("The trigger authorizer requires one canonical workspace identity.", nameof(workspaceId));
        }

        _workspaceId = workspaceId;
        _triggerWorkspaceId = workspaceId["workspace-sha256:".Length..];
        _targetSource = targetSource ?? throw new ArgumentNullException(nameof(targetSource));
        _grantResolver = grantResolver ?? throw new ArgumentNullException(nameof(grantResolver));
        _profileSource = profileSource ?? throw new ArgumentNullException(nameof(profileSource));
        _capabilityCatalog = capabilityCatalog ?? throw new ArgumentNullException(nameof(capabilityCatalog));
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<TriggerWorkerAuthorizationResponse> AuthorizeAsync(TriggerWorkerCurrentEvidenceInput input, DateTimeOffset evaluatedAtUtc, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryValidateInput(input, evaluatedAtUtc, out var adapter, out var profile, out var malformed))
        {
            return Unavailable(malformed);
        }

        if (input.Loop.Kind != TriggerLoopTargetKind.GovernedPublication
            || input.Loop.GovernedPublication is null
            || input.Loop.AuthorityGrant is null)
        {
            return Rejected("The selected trigger does not reference one governed publication and authority grant.");
        }

        if (!string.Equals(input.WorkspaceId, _triggerWorkspaceId, StringComparison.Ordinal))
        {
            return Rejected("The selected trigger workspace is not the composition's derived workspace identity.");
        }

        try
        {
            return await _authorityTransaction.ExecuteAsync(
                token => AuthorizeUnderFenceAsync(input, adapter!, profile!, evaluatedAtUtc, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Unavailable("Current trigger authority evidence could not be read under the shared authority fence.");
        }
    }

    private async Task<TriggerWorkerAuthorizationResponse> AuthorizeUnderFenceAsync(
        TriggerWorkerCurrentEvidenceInput input,
        TriggerAdapterReference adapter,
        AuthorityProfileReference profile,
        DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken)
    {
        var publication = input.Loop.GovernedPublication!;
        var requestedGrant = input.Loop.AuthorityGrant!;
        var target = await _targetSource.ResolveAsync(publication, cancellationToken).ConfigureAwait(false);
        var targetDisposition = TargetDisposition(target?.Status ?? AuthorityGrantDependencyStatus.Unknown);
        if (targetDisposition is not null)
        {
            return targetDisposition.Value ? Rejected("The selected governed publication is no longer active.") : Unavailable("The current governed publication posture is unavailable or malformed.");
        }

        if (!IsExactTarget(target, publication, input.RoleId))
        {
            return Unavailable("The governed publication binding is incomplete, substituted, or corrupt.");
        }

        var exactTarget = target!;
        var grant = await _grantResolver.ResolveAsync(requestedGrant, cancellationToken).ConfigureAwait(false);
        var grantDisposition = GrantDisposition(grant?.Status ?? AuthorityGrantResolutionStatus.Unknown);
        if (grantDisposition is not null)
        {
            return grantDisposition.Value ? Rejected("The selected authority grant no longer permits trigger dispatch.") : Unavailable("The current authority grant posture is unavailable or malformed.");
        }

        if (!IsExactGrant(grant, requestedGrant, publication, exactTarget, profile, input.RoleId))
        {
            return Unavailable("The current authority grant is incomplete, substituted, or corrupt.");
        }

        var exactGrant = grant!;
        var resolvedProfile = await _profileSource.ResolveAsync(exactGrant.Grant!.Binding.Profile, exactGrant.EvaluatedAtUtc, cancellationToken).ConfigureAwait(false);
        var profileDisposition = TargetDisposition(resolvedProfile?.Status ?? AuthorityGrantDependencyStatus.Unknown);
        if (profileDisposition is not null)
        {
            return profileDisposition.Value ? Rejected("The selected authority profile is no longer active.") : Unavailable("The current authority profile posture is unavailable or malformed.");
        }

        if (!IsExactProfile(resolvedProfile, exactGrant, profile))
        {
            return Unavailable("The current authority profile is incomplete, substituted, or corrupt.");
        }

        var intersection = AuthorityCeilingIntersection.Evaluate([resolvedProfile!.Profile!], exactGrant.EvaluatedAtUtc);
        if (!intersection.Validation.IsValid
            || !AuthorityBoundaryReceiptFactory.Validate(intersection.Receipt).IsValid
            || !intersection.Receipt.Profiles.SequenceEqual([profile]))
        {
            return Unavailable("The direct authority-boundary receipt could not be proved.");
        }

        if (intersection.Receipt.Decision != AuthorityBoundaryDecision.Direct
            || !exactGrant.EffectiveCeiling.AllowsRecurrence
            || !intersection.EffectiveCeiling.AllowsRecurrence
            || !exactGrant.EffectiveCeiling.Capabilities.Contains(adapter.Capability)
            || !intersection.EffectiveCeiling.Capabilities.Contains(adapter.Capability)
            || !exactTarget.CapabilityIds.Contains(adapter.Capability.Id.Value, StringComparer.Ordinal))
        {
            return Rejected("The current authority boundary does not permit the selected recurring trigger adapter.");
        }

        if (!AuthorityActorId.TryParse(input.ActorId, out var actorId, out _)
            || !TriggerDeliveryFactory.TryCreateActorContext(actorId, input.SurfaceId, input.WorkspaceId, input.RoleId, out _, out _))
        {
            return Unavailable("The selected trigger actor evidence is malformed.");
        }

        var adapterEvidence = await ResolveExactAdapterAsync(adapter, exactGrant.EvaluatedAtUtc, cancellationToken).ConfigureAwait(false);
        if (adapterEvidence is null)
        {
            return Unavailable("The current trigger adapter catalog posture is unavailable or corrupt.");
        }

        if (!adapterEvidence.Value.IsAuthorized)
        {
            return Rejected("The selected trigger adapter is no longer declared, installed, enabled, healthy, verified, and host-compatible.");
        }

        var exactAdapter = adapterEvidence.Value.Entry;
        if (exactAdapter is null)
        {
            return Unavailable("The current trigger adapter catalog posture is incomplete or corrupt.");
        }

        var now = UtcNow();
        if (!IsSupportedUtc(now) || now < evaluatedAtUtc || now < exactGrant.EvaluatedAtUtc || now < intersection.Receipt.EvaluatedAtUtc)
        {
            return Unavailable("The trusted trigger authority clock is unavailable or inconsistent.");
        }

        return Authorized(HashEvidence(
            _workspaceId,
            input,
            publication,
            requestedGrant,
            exactTarget.EvidenceHash,
            exactGrant.DependencyEvidenceHash,
            resolvedProfile.EvidenceHash,
            intersection.Receipt,
            adapter,
            adapterEvidence.Value.CatalogRevision,
            exactAdapter));
    }

    private async Task<(bool IsAuthorized, long CatalogRevision, CapabilityCatalogEntry? Entry)?> ResolveExactAdapterAsync(TriggerAdapterReference adapter, DateTimeOffset evidenceAtUtc, CancellationToken cancellationToken)
    {
        string? cursor = null;
        string? previousId = null;
        long? revision = null;
        var count = 0;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        CapabilityCatalogEntry? exact = null;
        do
        {
            CapabilityCatalogReadResult? read;
            try
            {
                read = await _capabilityCatalog.ReadAsync(cursor, CatalogPageSize, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }

            if (read is null || !Enum.IsDefined(read.Status) || read.Status != CapabilityCatalogReadStatus.Available || read.Page is null)
            {
                return null;
            }

            var page = read.Page;
            if (page.CatalogRevision < 0 || page.Entries is null || revision is not null && revision != page.CatalogRevision || page.Entries.Count > MaximumCatalogEntries - count)
            {
                return null;
            }

            revision ??= page.CatalogRevision;
            foreach (var entry in page.Entries)
            {
                if (!IsStructurallyValidCatalogEntry(entry, evidenceAtUtc))
                {
                    return null;
                }

                var id = entry.Descriptor.Id.Value;
                if (previousId is not null && string.CompareOrdinal(previousId, id) >= 0 || !ids.Add(id))
                {
                    return null;
                }

                previousId = id;
                if (entry.Descriptor.Id.Equals(adapter.Capability.Id))
                {
                    if (exact is not null)
                    {
                        return null;
                    }

                    exact = entry;
                }
            }

            count += page.Entries.Count;
            var next = page.NextCursor;
            if (next is not null && (page.Entries.Count == 0 || !CapabilityId.TryParse(next, out _, out _) || !string.Equals(next, previousId, StringComparison.Ordinal) || !cursors.Add(next)))
            {
                return null;
            }

            cursor = next;
        }
        while (cursor is not null);

        if (exact is null)
        {
            return new(false, revision!.Value, null);
        }

        return HasIndeterminateLifecycle(exact.Lifecycle)
            ? null
            : new(IsExactAvailableAdapter(exact, adapter, evidenceAtUtc), revision!.Value, exact);
    }

    private static bool TryValidateInput(TriggerWorkerCurrentEvidenceInput? input, DateTimeOffset evaluatedAtUtc, out TriggerAdapterReference? adapter, out AuthorityProfileReference? profile, out string detail)
    {
        adapter = null;
        profile = null;
        detail = "The selected trigger current-evidence input is malformed.";
        if (input is null
            || !IsSupportedUtc(evaluatedAtUtc)
            || !TriggerDeliveryId.TryParse(input.DeliveryId, out _)
            || !TriggerDeliveryValidator.ValidateLoopReference(input.Loop).IsValid
            || !AuthorityActorId.TryParse(input.ActorId, out _, out _)
            || !ContextualRoleId.IsValid(input.RoleId))
        {
            return false;
        }

        if (!CapabilityId.TryParse(input.AdapterCapabilityId, out var capabilityId, out _)
            || !CapabilityVersion.TryParse(input.AdapterCapabilityVersion, out var capabilityVersion, out _)
            || !CapabilityDescriptorHash.TryParse(input.AdapterDescriptorHash, out var descriptorHash, out _)
            || !CapabilityProviderId.TryParse(input.AdapterProviderId, out var providerId, out _)
            || !AuthorityProfileId.TryParse(input.AuthorityProfileId, out var profileId, out _)
            || !AuthorityProfileRevision.TryParse(input.AuthorityProfileRevision, out var profileRevision, out _))
        {
            return false;
        }

        var candidateAdapter = new TriggerAdapterReference(new CapabilityDescriptorIdentity(capabilityId!, capabilityVersion!, descriptorHash!), new CapabilityImplementationIdentity(providerId!, input.AdapterImplementationId));
        if (!TriggerDeliveryValidator.ValidateAdapterReference(candidateAdapter).IsValid)
        {
            return false;
        }

        adapter = candidateAdapter;
        profile = new AuthorityProfileReference(profileId!, profileRevision!);
        return true;
    }

    private static bool IsExactTarget(GovernedLoopGrantBindingResolution? resolution, GovernedLoopRevisionPublicationPin publication, string roleId)
    {
        var artifact = resolution?.Artifact;
        var revisionArtifact = artifact?.RevisionArtifact;
        var graph = artifact?.Graph;
        var owner = resolution?.OwningRole;
        return resolution?.Status == AuthorityGrantDependencyStatus.Active
            && Equals(resolution.PublicationPin, publication)
            && GovernedLoopRevisionContractValidator.Validate(publication).IsValid
            && artifact?.SchemaVersion == GovernedLoopGraphRevisionArtifact.CurrentSchemaVersion
            && revisionArtifact is not null
            && GovernedLoopRevisionContractValidator.Validate(revisionArtifact).IsValid
            && graph?.AuthorityCeiling is not null
            && owner?.Identity is not null
            && ContextualRoleId.IsValid(owner.Identity.RoleId)
            && string.Equals(owner.Identity.RoleId, roleId, StringComparison.Ordinal)
            && Equals(revisionArtifact.Revision, publication.Revision)
            && Equals(graph.OwningRole, owner)
            && IsCanonicalCapabilityIds(resolution.CapabilityIds)
            && resolution.CapabilityIds.SequenceEqual(graph.AuthorityCeiling.CapabilityIds, StringComparer.Ordinal)
            && IsSha256(resolution.EvidenceHash)
            && HasExactArtifactHash(artifact);
    }

    private static bool IsExactGrant(AuthorityGrantResolution? resolution, AuthorityGrantReference requestedGrant, GovernedLoopRevisionPublicationPin publication, GovernedLoopGrantBindingResolution target, AuthorityProfileReference profile, string roleId)
    {
        var grant = resolution?.Grant;
        var binding = grant?.Binding;
        var role = binding?.Role;
        return resolution?.Status == AuthorityGrantResolutionStatus.Active
            && Equals(resolution.RequestedReference, requestedGrant)
            && grant is not null
            && AuthorityGrantContractValidator.Validate(grant).IsValid
            && Equals(binding?.Loop, publication)
            && Equals(role, target.OwningRole)
            && role?.Identity is not null
            && string.Equals(role.Identity.RoleId, roleId, StringComparison.Ordinal)
            && Equals(binding?.Profile?.Reference, profile)
            && AuthorityCeilingSubset.IsEqual(resolution.EffectiveCeiling, grant.RequestedCeiling)
            && IsSha256(resolution.DependencyEvidenceHash)
            && IsSupportedUtc(resolution.EvaluatedAtUtc);
    }

    private static bool IsExactProfile(AuthorityGrantProfileResolution? resolution, AuthorityGrantResolution grant, AuthorityProfileReference profile)
    {
        var expected = grant.Grant?.Binding?.Profile;
        var value = resolution?.Profile;
        return resolution?.Status == AuthorityGrantDependencyStatus.Active
            && expected is not null
            && Equals(resolution.RequestedPin, expected)
            && value is not null
            && Equals(new AuthorityProfileReference(value.ProfileId, value.Revision), profile)
            && AuthorityProfileHash.TryCompute(value, out var hash, out var validation)
            && validation.IsValid
            && Equals(hash, expected.ContentHash)
            && IsSha256(resolution.EvidenceHash);
    }

    private static bool IsExactAvailableAdapter(CapabilityCatalogEntry entry, TriggerAdapterReference expected, DateTimeOffset evidenceAtUtc)
    {
        var descriptor = entry.Descriptor;
        var lifecycle = entry.Lifecycle;
        return entry.Revision > 0
            && entry.UpdatedAtUtc != default
            && entry.UpdatedAtUtc.Offset == TimeSpan.Zero
            && entry.UpdatedAtUtc <= evidenceAtUtc
            && CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _)
            && Equals(identity, expected.Capability)
            && Equals(descriptor.Implementation, expected.Implementation)
            && lifecycle.SchemaVersion == CapabilityLifecycleSnapshot.CurrentSchemaVersion
            && Equals(lifecycle.DescriptorIdentity, identity)
            && lifecycle.Declaration == CapabilityDeclarationState.Declared
            && lifecycle.Installation == CapabilityInstallationState.Installed
            && lifecycle.Enablement == CapabilityEnablementState.Enabled
            && lifecycle.Health == CapabilityHealthState.Healthy
            && lifecycle.Retirement is CapabilityRetirementState.Active or CapabilityRetirementState.Deprecated
            && lifecycle.Trust == CapabilityTrustState.Verified
            && descriptor.Compatibility?.HostVersionRange?.Contains(CapabilityHostRuntime.HostContractVersion) == true
            && descriptor.Compatibility.SupportedPlatforms?.Any(platform => platform?.Equals(CapabilityPlatform.Any) == true || platform?.Equals(CapabilityHostRuntime.Platform) == true) == true;
    }

    private static bool IsStructurallyValidCatalogEntry(CapabilityCatalogEntry? entry, DateTimeOffset evidenceAtUtc)
    {
        var descriptor = entry?.Descriptor;
        var lifecycle = entry?.Lifecycle;
        return descriptor is not null
            && lifecycle is not null
            && entry!.Revision > 0
            && entry.UpdatedAtUtc != default
            && entry.UpdatedAtUtc.Offset == TimeSpan.Zero
            && entry.UpdatedAtUtc <= evidenceAtUtc
            && CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _)
            && lifecycle.SchemaVersion == CapabilityLifecycleSnapshot.CurrentSchemaVersion
            && Equals(lifecycle.DescriptorIdentity, identity)
            && Enum.IsDefined(lifecycle.Declaration)
            && Enum.IsDefined(lifecycle.Installation)
            && Enum.IsDefined(lifecycle.Enablement)
            && Enum.IsDefined(lifecycle.Health)
            && Enum.IsDefined(lifecycle.Retirement)
            && Enum.IsDefined(lifecycle.Trust);
    }

    private static bool HasIndeterminateLifecycle(CapabilityLifecycleSnapshot lifecycle)
        => lifecycle.Declaration == CapabilityDeclarationState.Unknown
            || lifecycle.Installation == CapabilityInstallationState.Unknown
            || lifecycle.Enablement == CapabilityEnablementState.Unknown
            || lifecycle.Health == CapabilityHealthState.Unknown
            || lifecycle.Retirement == CapabilityRetirementState.Unknown;

    private static bool HasExactArtifactHash(GovernedLoopGraphRevisionArtifact artifact)
    {
        try
        {
            return string.Equals(GovernedLoopGraphRevisionContractHash.ComputeArtifactHash(artifact), artifact.ArtifactHash, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NullReferenceException or OverflowException)
        {
            return false;
        }
    }

    private static bool IsCanonicalCapabilityIds(IReadOnlyList<string>? values)
        => values is not null
            && values.Count <= CustomLoopLimits.MaxGraphAuthorityCapabilities
            && values.All(value => CapabilityId.TryParse(value, out _, out _))
            && values.SequenceEqual(values.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal)
            && values.Distinct(StringComparer.Ordinal).Count() == values.Count;

    private static bool? TargetDisposition(AuthorityGrantDependencyStatus status) => status switch
    {
        AuthorityGrantDependencyStatus.Active => null,
        AuthorityGrantDependencyStatus.Disabled or AuthorityGrantDependencyStatus.Expired or AuthorityGrantDependencyStatus.Stale or AuthorityGrantDependencyStatus.NotFound => true,
        AuthorityGrantDependencyStatus.Unavailable => false,
        _ => false,
    };

    private static bool? GrantDisposition(AuthorityGrantResolutionStatus status) => status switch
    {
        AuthorityGrantResolutionStatus.Active => null,
        AuthorityGrantResolutionStatus.NotEffective or AuthorityGrantResolutionStatus.Suspended or AuthorityGrantResolutionStatus.Revoked or AuthorityGrantResolutionStatus.Expired or AuthorityGrantResolutionStatus.Stale or AuthorityGrantResolutionStatus.CeilingExceeded or AuthorityGrantResolutionStatus.ProfileUnavailable or AuthorityGrantResolutionStatus.NotFound or AuthorityGrantResolutionStatus.RoleUnavailable or AuthorityGrantResolutionStatus.LoopUnavailable => true,
        AuthorityGrantResolutionStatus.Unavailable => false,
        _ => false,
    };

    private static string HashEvidence(
        string workspaceId,
        TriggerWorkerCurrentEvidenceInput input,
        GovernedLoopRevisionPublicationPin publication,
        AuthorityGrantReference grant,
        string targetEvidenceHash,
        string grantEvidenceHash,
        string profileEvidenceHash,
        AuthorityBoundaryReceipt receipt,
        TriggerAdapterReference adapter,
        long catalogRevision,
        CapabilityCatalogEntry catalogEntry)
    {
        var lifecycle = catalogEntry.Lifecycle;
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("domain", EvidenceDomain);
        writer.WriteString("compositionWorkspaceId", workspaceId);
        writer.WriteString("deliveryId", input.DeliveryId);
        writer.WriteString("publicationGraphId", publication.Revision.GraphId);
        writer.WriteString("publicationRevisionId", publication.Revision.RevisionId);
        writer.WriteString("publicationExecutableHash", publication.Revision.ExecutableHash);
        writer.WriteString("publicationOperationId", publication.PublicationOperationId);
        writer.WriteString("publicationValidationHash", publication.ValidationEvidenceHash);
        writer.WriteString("grantId", grant.GrantId.Value);
        writer.WriteString("grantRevision", grant.Revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteString("grantHash", grant.ContentHash);
        writer.WriteString("targetEvidenceHash", targetEvidenceHash);
        writer.WriteString("grantEvidenceHash", grantEvidenceHash);
        writer.WriteString("profileEvidenceHash", profileEvidenceHash);
        writer.WriteString("actorId", input.ActorId);
        writer.WriteString("surfaceId", input.SurfaceId);
        writer.WriteString("workspaceId", input.WorkspaceId);
        writer.WriteString("roleId", input.RoleId);
        writer.WriteString("profileId", input.AuthorityProfileId);
        writer.WriteString("profileRevision", input.AuthorityProfileRevision);
        writer.WriteString("adapterCapabilityId", adapter.Capability.Id.Value);
        writer.WriteString("adapterCapabilityVersion", adapter.Capability.Version.Value);
        writer.WriteString("adapterCapabilityHash", adapter.Capability.Hash.Value);
        writer.WriteString("adapterProvider", adapter.Implementation.ProviderId.Value);
        writer.WriteString("adapterImplementation", adapter.Implementation.ImplementationId);
        writer.WriteNumber("adapterCatalogRevision", catalogRevision);
        writer.WriteNumber("adapterEntryRevision", catalogEntry.Revision);
        writer.WriteString("adapterEntryUpdatedAtUtc", catalogEntry.UpdatedAtUtc);
        writer.WriteNumber("adapterLifecycleSchemaVersion", lifecycle.SchemaVersion);
        writer.WriteString("adapterLifecycleCapabilityId", lifecycle.DescriptorIdentity.Id.Value);
        writer.WriteString("adapterLifecycleCapabilityVersion", lifecycle.DescriptorIdentity.Version.Value);
        writer.WriteString("adapterLifecycleCapabilityHash", lifecycle.DescriptorIdentity.Hash.Value);
        writer.WriteNumber("adapterLifecycleDeclaration", (int)lifecycle.Declaration);
        writer.WriteNumber("adapterLifecycleInstallation", (int)lifecycle.Installation);
        writer.WriteNumber("adapterLifecycleEnablement", (int)lifecycle.Enablement);
        writer.WriteNumber("adapterLifecycleHealth", (int)lifecycle.Health);
        writer.WriteNumber("adapterLifecycleRetirement", (int)lifecycle.Retirement);
        writer.WriteNumber("adapterLifecycleTrust", (int)lifecycle.Trust);
        writer.WriteString("authorityDecision", receipt.Decision.ToString());
        writer.WriteStartArray("authorityConditions");
        foreach (var condition in receipt.Conditions)
        {
            writer.WriteStringValue($"{condition.Decision}:{condition.Reason}");
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    private DateTimeOffset UtcNow()
    {
        try
        {
            var value = _timeProvider.GetUtcNow();
            return value.Offset == TimeSpan.Zero ? value : default;
        }
        catch
        {
            return default;
        }
    }

    private static TriggerWorkerAuthorizationResponse Authorized(string evidenceHash) => new("Authorized", evidenceHash, "Exact current governed trigger authority evidence was proved.");

    private static TriggerWorkerAuthorizationResponse Rejected(string detail) => new("Rejected", new string('0', 64), detail);

    private static TriggerWorkerAuthorizationResponse Unavailable(string detail) => new("Unavailable", new string('0', 64), detail);

    private static bool IsSupportedUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero && value.Year is >= 2020 and <= 2100;

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
