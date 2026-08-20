using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Triggers.Schedules.Models;

namespace EmbodySense.Core.Startup.Triggers.Schedules;

/// <summary>Resolves one fresh schedule target, adapter, authority, actor, and payload snapshot.</summary>
/// <remarks>
/// Mutable authority observations execute under the composition's shared reentrant capability-authority
/// fence. Opaque immutable payload resolution deliberately runs after that fence is released, so an external
/// content source cannot stall or deadlock authority mutations. The adapter never follows replacement revisions,
/// grants authority, interprets payload identity as a locator, or retains source-owned payload arrays.
/// </remarks>
public sealed class ScheduleCurrentEvidenceAdapter : IScheduleCurrentEvidencePort
{
    private const int CatalogPageSize = 100;
    private const int MaximumCatalogEntries = 512;
    private const string EvidenceDomain = "embodysense-schedule-current-evidence-v1";
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);
    private readonly string _workspaceId;
    private readonly IGovernedLoopGrantBindingSource _targetSource;
    private readonly IAuthorityGrantResolver _grantResolver;
    private readonly IAuthorityGrantProfileSource _profileSource;
    private readonly ICapabilityCatalogStore _capabilityCatalog;
    private readonly IScheduleGovernedPayloadSource _payloadSource;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the current-evidence adapter from composition-owned exact sources.</summary>
    public ScheduleCurrentEvidenceAdapter(
        string workspaceId,
        IGovernedLoopGrantBindingSource targetSource,
        IAuthorityGrantResolver grantResolver,
        IAuthorityGrantProfileSource profileSource,
        ICapabilityCatalogStore capabilityCatalog,
        IScheduleGovernedPayloadSource payloadSource,
        ICapabilityAuthorityTransaction authorityTransaction,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        _workspaceId = workspaceId;
        _targetSource = targetSource ?? throw new ArgumentNullException(nameof(targetSource));
        _grantResolver = grantResolver ?? throw new ArgumentNullException(nameof(grantResolver));
        _profileSource = profileSource ?? throw new ArgumentNullException(nameof(profileSource));
        _capabilityCatalog = capabilityCatalog ?? throw new ArgumentNullException(nameof(capabilityCatalog));
        _payloadSource = payloadSource ?? throw new ArgumentNullException(nameof(payloadSource));
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<ScheduleCurrentEvidenceResult> ResolveAsync(
        ScheduleDefinition definition,
        ScheduleOccurrence occurrence,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ScheduleContractHash.TryComputeDefinition(definition, out var definitionHash, out _)
            || !ScheduleContractValidator.ValidateOccurrence(occurrence).IsValid
            || !IsSupportedUtc(observedAtUtc)
            || occurrence.ScheduledAtUtc > observedAtUtc
            || !Equals(occurrence.TimeZone, definition.TimeZone))
        {
            return Failure(ScheduleCurrentEvidenceStatus.Corrupt);
        }

        if (!string.Equals(definition.WorkspaceId, _workspaceId, StringComparison.Ordinal))
        {
            return Failure(ScheduleCurrentEvidenceStatus.ActorUnavailable);
        }

        AuthoritySnapshotResult authorityResult;
        try
        {
            authorityResult = await _authorityTransaction.ExecuteAsync(
                token => ResolveAuthorityUnderFenceAsync(definition, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Failure(ScheduleCurrentEvidenceStatus.Unavailable);
        }

        if (authorityResult.Status != ScheduleCurrentEvidenceStatus.Available || authorityResult.Snapshot is null)
        {
            return Failure(authorityResult.Status);
        }

        ScheduleGovernedPayloadResolution? payloadResult;
        try
        {
            payloadResult = await _payloadSource.ResolveAsync(definition.Payload.GovernedReference, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Failure(ScheduleCurrentEvidenceStatus.PayloadUnavailable);
        }

        var payloadFailure = PayloadFailure(payloadResult);
        if (payloadFailure is not null)
        {
            return Failure(payloadFailure.Value);
        }

        var payload = payloadResult!.GetContent()!;
        if (!PayloadMatches(payloadResult, definition.Payload, payload))
        {
            return Failure(ScheduleCurrentEvidenceStatus.Corrupt);
        }

        var resolvedAtUtc = UtcNow();
        var authority = authorityResult.Snapshot;
        if (!IsSupportedUtc(resolvedAtUtc)
            || resolvedAtUtc < observedAtUtc
            || resolvedAtUtc < authority.Authority.BoundaryReceipt.EvaluatedAtUtc)
        {
            return Failure(ScheduleCurrentEvidenceStatus.Unavailable);
        }

        var evidenceHash = HashEvidence(
            definitionHash!,
            occurrence,
            observedAtUtc,
            resolvedAtUtc,
            authority.TargetEvidenceHash,
            authority.GrantEvidenceHash,
            authority.ProfileEvidenceHash,
            authority.Authority,
            definition.TimeAdapter,
            definition.Payload);
        return new ScheduleCurrentEvidenceResult(
            ScheduleCurrentEvidenceStatus.Available,
            new ScheduleCurrentEvidence(
                evidenceHash,
                resolvedAtUtc,
                definition.Target,
                definition.TimeAdapter,
                authority.Actor,
                authority.Authority,
                true,
                payload));
    }

    private async Task<AuthoritySnapshotResult> ResolveAuthorityUnderFenceAsync(
        ScheduleDefinition definition,
        CancellationToken cancellationToken)
    {
        var target = await _targetSource.ResolveAsync(definition.Target.GovernedPublication, cancellationToken).ConfigureAwait(false);
        var targetFailure = TargetFailure(target?.Status ?? AuthorityGrantDependencyStatus.Unknown);
        if (targetFailure is not null)
        {
            return AuthorityFailure(targetFailure.Value);
        }

        if (!IsExactTarget(target, definition))
        {
            return AuthorityFailure(ScheduleCurrentEvidenceStatus.Corrupt);
        }

        var exactTarget = target!;
        if (!string.Equals(exactTarget.OwningRole!.Identity.RoleId, definition.RoleId, StringComparison.Ordinal))
        {
            return AuthorityFailure(ScheduleCurrentEvidenceStatus.ActorUnavailable);
        }

        var grant = await _grantResolver.ResolveAsync(definition.Target.AuthorityGrant, cancellationToken).ConfigureAwait(false);
        var grantFailure = GrantFailure(grant?.Status ?? AuthorityGrantResolutionStatus.Unknown);
        if (grantFailure is not null)
        {
            return AuthorityFailure(grantFailure.Value);
        }

        if (!IsExactGrant(grant, definition, exactTarget))
        {
            return AuthorityFailure(ScheduleCurrentEvidenceStatus.Corrupt);
        }

        var exactGrant = grant!;
        var profile = await _profileSource.ResolveAsync(
            exactGrant.Grant!.Binding.Profile,
            exactGrant.EvaluatedAtUtc,
            cancellationToken).ConfigureAwait(false);
        var profileFailure = ProfileFailure(profile?.Status ?? AuthorityGrantDependencyStatus.Unknown);
        if (profileFailure is not null)
        {
            return AuthorityFailure(profileFailure.Value);
        }

        if (!IsExactProfile(profile, exactGrant, definition))
        {
            return AuthorityFailure(ScheduleCurrentEvidenceStatus.Corrupt);
        }

        var intersection = AuthorityCeilingIntersection.Evaluate([profile!.Profile!], exactGrant.EvaluatedAtUtc);
        if (!intersection.Validation.IsValid
            || !AuthorityBoundaryReceiptFactory.Validate(intersection.Receipt).IsValid
            || !intersection.Receipt.Profiles.SequenceEqual([definition.AuthorityProfile]))
        {
            return AuthorityFailure(ScheduleCurrentEvidenceStatus.Corrupt);
        }

        if (intersection.Receipt.Decision != AuthorityBoundaryDecision.Direct)
        {
            return AuthorityFailure(ScheduleCurrentEvidenceStatus.PermissionDenied);
        }

        if (!exactGrant.EffectiveCeiling.AllowsRecurrence || !intersection.EffectiveCeiling.AllowsRecurrence)
        {
            return AuthorityFailure(ScheduleCurrentEvidenceStatus.RecurrenceDenied);
        }

        if (!exactGrant.EffectiveCeiling.Capabilities.Contains(definition.TimeAdapter.Capability)
            || !intersection.EffectiveCeiling.Capabilities.Contains(definition.TimeAdapter.Capability)
            || !exactTarget.CapabilityIds.Contains(definition.TimeAdapter.Capability.Id.Value, StringComparer.Ordinal))
        {
            return AuthorityFailure(ScheduleCurrentEvidenceStatus.PermissionDenied);
        }

        var adapter = await ResolveAdapterAsync(definition.TimeAdapter, exactGrant.EvaluatedAtUtc, cancellationToken).ConfigureAwait(false);
        if (adapter != ScheduleCurrentEvidenceStatus.Available)
        {
            return AuthorityFailure(adapter);
        }

        if (!TriggerDeliveryFactory.TryCreateActorContext(
                definition.ActorId,
                definition.SurfaceId,
                definition.WorkspaceId,
                definition.RoleId,
                out var actor,
                out _))
        {
            return AuthorityFailure(ScheduleCurrentEvidenceStatus.Corrupt);
        }

        return new AuthoritySnapshotResult(
            ScheduleCurrentEvidenceStatus.Available,
            new AuthoritySnapshot(
                exactTarget.EvidenceHash,
                exactGrant.DependencyEvidenceHash,
                profile.EvidenceHash,
                actor!,
                new TriggerAuthorityEvidence(definition.AuthorityProfile, intersection.Receipt)));
    }

    private async Task<ScheduleCurrentEvidenceStatus> ResolveAdapterAsync(
        TriggerAdapterReference expected,
        DateTimeOffset evidenceAtUtc,
        CancellationToken cancellationToken)
    {
        string? cursor = null;
        long? revision = null;
        var count = 0;
        var entries = new List<CapabilityCatalogEntry>();
        var capabilityIds = new HashSet<string>(StringComparer.Ordinal);
        var observedCursors = new HashSet<string>(StringComparer.Ordinal);
        string? previousId = null;
        do
        {
            var read = await _capabilityCatalog.ReadAsync(cursor, CatalogPageSize, cancellationToken).ConfigureAwait(false);
            if (read is null || !Enum.IsDefined(read.Status))
            {
                return ScheduleCurrentEvidenceStatus.Corrupt;
            }

            if (read.Status != CapabilityCatalogReadStatus.Available || read.Page is null)
            {
                return read.Status is CapabilityCatalogReadStatus.Unavailable or CapabilityCatalogReadStatus.RecoveredLastProved
                    ? ScheduleCurrentEvidenceStatus.AdapterUnavailable
                    : ScheduleCurrentEvidenceStatus.Corrupt;
            }

            var pageEntries = read.Page.Entries;
            if (read.Page.CatalogRevision < 0
                || pageEntries is null
                || revision is not null && revision != read.Page.CatalogRevision)
            {
                return ScheduleCurrentEvidenceStatus.Corrupt;
            }

            if (pageEntries.Count > MaximumCatalogEntries - count)
            {
                return ScheduleCurrentEvidenceStatus.Backpressured;
            }

            revision ??= read.Page.CatalogRevision;
            foreach (var entry in pageEntries)
            {
                if (!IsStructurallyValidCatalogEntry(entry, evidenceAtUtc))
                {
                    return ScheduleCurrentEvidenceStatus.Corrupt;
                }

                var id = entry.Descriptor.Id.Value;
                if (previousId is not null && string.CompareOrdinal(previousId, id) >= 0
                    || !capabilityIds.Add(id))
                {
                    return ScheduleCurrentEvidenceStatus.Corrupt;
                }

                previousId = id;
                entries.Add(entry);
            }

            count += pageEntries.Count;
            var nextCursor = read.Page.NextCursor;
            if (nextCursor is not null
                && (pageEntries.Count == 0
                    || !CapabilityId.TryParse(nextCursor, out _, out _)
                    || !string.Equals(nextCursor, previousId, StringComparison.Ordinal)
                    || !observedCursors.Add(nextCursor)))
            {
                return ScheduleCurrentEvidenceStatus.Corrupt;
            }

            cursor = nextCursor;
        }
        while (cursor is not null);

        var exact = entries.SingleOrDefault(entry => entry.Descriptor.Id.Equals(expected.Capability.Id));
        return exact is not null && IsExactAvailableAdapter(exact, expected, evidenceAtUtc)
            ? ScheduleCurrentEvidenceStatus.Available
            : ScheduleCurrentEvidenceStatus.AdapterUnavailable;
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

    private static bool IsExactAvailableAdapter(
        CapabilityCatalogEntry? entry,
        TriggerAdapterReference expected,
        DateTimeOffset evidenceAtUtc)
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
            && Equals(identity, expected.Capability)
            && Equals(descriptor.Implementation, expected.Implementation)
            && lifecycle.SchemaVersion == CapabilityLifecycleSnapshot.CurrentSchemaVersion
            && Equals(lifecycle.DescriptorIdentity, identity)
            && lifecycle.Declaration == CapabilityDeclarationState.Declared
            && lifecycle.Installation == CapabilityInstallationState.Installed
            && lifecycle.Enablement == CapabilityEnablementState.Enabled
            && lifecycle.Health == CapabilityHealthState.Healthy
            && lifecycle.Retirement != CapabilityRetirementState.Removed
            && lifecycle.Trust == CapabilityTrustState.Verified
            && descriptor.Compatibility?.HostVersionRange?.Contains(CapabilityHostRuntime.HostContractVersion) == true
            && descriptor.Compatibility.SupportedPlatforms?.Any(
                platform => platform?.Equals(CapabilityPlatform.Any) == true || platform?.Equals(CapabilityHostRuntime.Platform) == true) == true;
    }

    private static bool IsStructurallyValidCatalogEntry(
        CapabilityCatalogEntry? entry,
        DateTimeOffset evidenceAtUtc)
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

    private static bool IsExactTarget(GovernedLoopGrantBindingResolution? resolution, ScheduleDefinition definition)
    {
        var pin = definition.Target.GovernedPublication;
        var artifact = resolution?.Artifact;
        var revisionArtifact = artifact?.RevisionArtifact;
        var graph = artifact?.Graph;
        var owner = resolution?.OwningRole;
        var capabilityIds = resolution?.CapabilityIds;
        if (resolution?.Status != AuthorityGrantDependencyStatus.Active
            || pin is null
            || !Equals(resolution.PublicationPin, pin)
            || !GovernedLoopRevisionContractValidator.Validate(pin).IsValid
            || artifact is null
            || artifact.SchemaVersion != GovernedLoopGraphRevisionArtifact.CurrentSchemaVersion
            || revisionArtifact is null
            || !GovernedLoopRevisionContractValidator.Validate(revisionArtifact).IsValid
            || graph is null
            || graph.AuthorityCeiling is null
            || owner?.Identity is null
            || !ContextualRoleId.IsValid(owner.Identity.RoleId)
            || !Equals(revisionArtifact.Revision, pin.Revision)
            || !Equals(graph.OwningRole, owner)
            || capabilityIds is null
            || !IsCanonicalCapabilityIds(capabilityIds)
            || !capabilityIds.SequenceEqual(graph.AuthorityCeiling.CapabilityIds, StringComparer.Ordinal)
            || !IsSha256(resolution.EvidenceHash))
        {
            return false;
        }

        try
        {
            return string.Equals(
                GovernedLoopGraphRevisionContractHash.ComputeArtifactHash(artifact),
                artifact.ArtifactHash,
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            InvalidOperationException or
            NullReferenceException or
            OverflowException)
        {
            return false;
        }
    }

    private static bool IsExactGrant(
        AuthorityGrantResolution? resolution,
        ScheduleDefinition definition,
        GovernedLoopGrantBindingResolution target)
    {
        var grant = resolution?.Grant;
        var binding = grant?.Binding;
        var role = binding?.Role;
        return resolution?.Status == AuthorityGrantResolutionStatus.Active
            && Equals(resolution.RequestedReference, definition.Target.AuthorityGrant)
            && grant is not null
            && binding is not null
            && role?.Identity is not null
            && AuthorityGrantContractValidator.Validate(grant).IsValid
            && Equals(binding.Loop, definition.Target.GovernedPublication)
            && Equals(role, target.OwningRole)
            && string.Equals(role.Identity.RoleId, definition.RoleId, StringComparison.Ordinal)
            && Equals(binding.Profile?.Reference, definition.AuthorityProfile)
            && AuthorityCeilingSubset.IsEqual(resolution.EffectiveCeiling, grant.RequestedCeiling)
            && IsSha256(resolution.DependencyEvidenceHash)
            && IsSupportedUtc(resolution.EvaluatedAtUtc);
    }

    private static bool IsExactProfile(
        AuthorityGrantProfileResolution? resolution,
        AuthorityGrantResolution grant,
        ScheduleDefinition definition)
    {
        var expectedPin = grant.Grant?.Binding?.Profile;
        var profile = resolution?.Profile;
        return resolution?.Status == AuthorityGrantDependencyStatus.Active
            && expectedPin is not null
            && Equals(resolution.RequestedPin, expectedPin)
            && profile is not null
            && Equals(new AuthorityProfileReference(profile.ProfileId, profile.Revision), definition.AuthorityProfile)
            && AuthorityProfileHash.TryCompute(profile, out var profileHash, out var validation)
            && validation.IsValid
            && Equals(profileHash, expectedPin.ContentHash)
            && IsSha256(resolution.EvidenceHash);
    }

    private static bool PayloadMatches(
        ScheduleGovernedPayloadResolution resolution,
        SchedulePayloadReference expected,
        byte[] payload)
    {
        if (!string.Equals(resolution.GovernedReference, expected.GovernedReference, StringComparison.Ordinal)
            || resolution.ContentHash is null
            || !CapabilityIntegrityDigest.TryParse(resolution.ContentHash.Value, out _, out _)
            || payload.Length > TriggerDeliveryLimits.MaxInlinePayloadBytes
            || !resolution.ContentHash.FixedTimeEquals(expected.ContentHash)
            || !CapabilityIntegrityDigest.Compute(payload).FixedTimeEquals(expected.ContentHash))
        {
            return false;
        }

        try
        {
            _ = _strictUtf8.GetString(payload);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static ScheduleCurrentEvidenceStatus? TargetFailure(AuthorityGrantDependencyStatus status) => status switch
    {
        AuthorityGrantDependencyStatus.Active => null,
        AuthorityGrantDependencyStatus.Disabled or AuthorityGrantDependencyStatus.Expired or AuthorityGrantDependencyStatus.Stale or AuthorityGrantDependencyStatus.NotFound => ScheduleCurrentEvidenceStatus.TargetUnavailable,
        AuthorityGrantDependencyStatus.Unavailable => ScheduleCurrentEvidenceStatus.Unavailable,
        _ => ScheduleCurrentEvidenceStatus.Corrupt,
    };

    private static ScheduleCurrentEvidenceStatus? ProfileFailure(AuthorityGrantDependencyStatus status) => status switch
    {
        AuthorityGrantDependencyStatus.Active => null,
        AuthorityGrantDependencyStatus.Disabled or AuthorityGrantDependencyStatus.Expired or AuthorityGrantDependencyStatus.Stale or AuthorityGrantDependencyStatus.NotFound => ScheduleCurrentEvidenceStatus.AuthorityUnavailable,
        AuthorityGrantDependencyStatus.Unavailable => ScheduleCurrentEvidenceStatus.Unavailable,
        _ => ScheduleCurrentEvidenceStatus.Corrupt,
    };

    private static ScheduleCurrentEvidenceStatus? GrantFailure(AuthorityGrantResolutionStatus status) => status switch
    {
        AuthorityGrantResolutionStatus.Active => null,
        AuthorityGrantResolutionStatus.NotEffective or AuthorityGrantResolutionStatus.Suspended or AuthorityGrantResolutionStatus.Revoked or AuthorityGrantResolutionStatus.Expired or AuthorityGrantResolutionStatus.Stale or AuthorityGrantResolutionStatus.CeilingExceeded => ScheduleCurrentEvidenceStatus.PermissionDenied,
        AuthorityGrantResolutionStatus.ProfileUnavailable or AuthorityGrantResolutionStatus.NotFound => ScheduleCurrentEvidenceStatus.AuthorityUnavailable,
        AuthorityGrantResolutionStatus.RoleUnavailable => ScheduleCurrentEvidenceStatus.ActorUnavailable,
        AuthorityGrantResolutionStatus.LoopUnavailable => ScheduleCurrentEvidenceStatus.TargetUnavailable,
        AuthorityGrantResolutionStatus.Unavailable => ScheduleCurrentEvidenceStatus.Unavailable,
        _ => ScheduleCurrentEvidenceStatus.Corrupt,
    };

    private static ScheduleCurrentEvidenceStatus? PayloadFailure(ScheduleGovernedPayloadResolution? resolution)
    {
        if (resolution is null || !Enum.IsDefined(resolution.Status))
        {
            return ScheduleCurrentEvidenceStatus.Corrupt;
        }

        return resolution.Status switch
        {
            ScheduleGovernedPayloadResolutionStatus.Available => resolution.GovernedReference is null
                || resolution.ContentHash is null
                || !resolution.HasBoundedContent
                ? ScheduleCurrentEvidenceStatus.Corrupt
                : null,
            ScheduleGovernedPayloadResolutionStatus.NotFound or ScheduleGovernedPayloadResolutionStatus.Unavailable => ScheduleCurrentEvidenceStatus.PayloadUnavailable,
            ScheduleGovernedPayloadResolutionStatus.Backpressured => ScheduleCurrentEvidenceStatus.Backpressured,
            _ => ScheduleCurrentEvidenceStatus.Corrupt,
        };
    }

    private static string HashEvidence(
        string definitionHash,
        ScheduleOccurrence occurrence,
        DateTimeOffset observedAtUtc,
        DateTimeOffset resolvedAtUtc,
        string targetEvidenceHash,
        string grantEvidenceHash,
        string profileEvidenceHash,
        TriggerAuthorityEvidence authority,
        TriggerAdapterReference adapter,
        SchedulePayloadReference payload)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("domain", EvidenceDomain);
            writer.WriteString("definitionHash", definitionHash);
            writer.WriteNumber("occurrenceOrdinal", occurrence.Ordinal);
            writer.WriteString("scheduledLocal", occurrence.ScheduledLocal.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString("scheduledAtUtc", occurrence.ScheduledAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString("initialObservedAtUtc", observedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString("resolvedAtUtc", resolvedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString("targetEvidenceHash", targetEvidenceHash);
            writer.WriteString("grantEvidenceHash", grantEvidenceHash);
            writer.WriteString("profileEvidenceHash", profileEvidenceHash);
            writer.WriteString("authorityEvaluatedAtUtc", authority.BoundaryReceipt.EvaluatedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString("authorityDecision", authority.BoundaryReceipt.Decision.ToString());
            writer.WriteStartArray("authorityConditions");
            foreach (var condition in authority.BoundaryReceipt.Conditions)
            {
                writer.WriteStringValue($"{condition.Decision}:{condition.Reason}");
            }

            writer.WriteEndArray();
            writer.WriteString("adapterCapabilityId", adapter.Capability.Id.Value);
            writer.WriteString("adapterCapabilityVersion", adapter.Capability.Version.Value);
            writer.WriteString("adapterCapabilityHash", adapter.Capability.Hash.Value);
            writer.WriteString("adapterProvider", adapter.Implementation.ProviderId.Value);
            writer.WriteString("adapterImplementation", adapter.Implementation.ImplementationId);
            writer.WriteString("payloadReference", payload.GovernedReference);
            writer.WriteString("payloadHash", payload.ContentHash.Value);
            writer.WriteEndObject();
            writer.Flush();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    private static bool IsSupportedUtc(DateTimeOffset value)
        => value != default
            && value.Offset == TimeSpan.Zero
            && value.Year is >= ScheduleContractLimits.MinimumSupportedYear and <= ScheduleContractLimits.MaximumSupportedYear;

    private static bool IsSha256(string? value)
        => value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsCanonicalCapabilityIds(IReadOnlyList<string> values)
        => values.Count <= CustomLoopLimits.MaxGraphAuthorityCapabilities
            && values.All(value => CapabilityId.TryParse(value, out _, out _))
            && values.Distinct(StringComparer.Ordinal).Count() == values.Count;

    private static ScheduleCurrentEvidenceResult Failure(ScheduleCurrentEvidenceStatus status)
        => new(status, null);

    private static AuthoritySnapshotResult AuthorityFailure(ScheduleCurrentEvidenceStatus status)
        => new(status, null);

    private sealed record AuthoritySnapshot(
        string TargetEvidenceHash,
        string GrantEvidenceHash,
        string ProfileEvidenceHash,
        TriggerActorContext Actor,
        TriggerAuthorityEvidence Authority);

    private sealed record AuthoritySnapshotResult(
        ScheduleCurrentEvidenceStatus Status,
        AuthoritySnapshot? Snapshot);
}
