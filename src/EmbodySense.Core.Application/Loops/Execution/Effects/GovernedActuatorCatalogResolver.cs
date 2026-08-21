using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;

namespace EmbodySense.Core.Application.Loops.Execution.Effects;

/// <summary>Resolves immutable server registrations only through exact current capability lifecycle truth.</summary>
public sealed class GovernedActuatorCatalogResolver : IGovernedActuatorCatalogResolver
{
    private const int CatalogPageSize = CapabilityCatalogPageSnapshot.MaximumEntryCount;
    private const int MaximumCatalogEntries = 1024;
    private const int MaximumCatalogPages = 32;
    private const int MaximumOperationPage = 256;
    private readonly ICapabilityCatalogStore _catalogStore;
    private readonly IGovernedActuatorOperationRegistry _registry;
    private readonly CapabilityVersion _hostContractVersion;
    private readonly CapabilityPlatform _hostPlatform;

    /// <summary>Creates a resolver for one exact current host contract and platform.</summary>
    public GovernedActuatorCatalogResolver(
        ICapabilityCatalogStore catalogStore,
        IGovernedActuatorOperationRegistry registry,
        CapabilityVersion hostContractVersion,
        CapabilityPlatform hostPlatform)
    {
        _catalogStore = catalogStore ?? throw new ArgumentNullException(nameof(catalogStore));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _hostContractVersion = hostContractVersion ?? throw new ArgumentNullException(nameof(hostContractVersion));
        _hostPlatform = hostPlatform ?? throw new ArgumentNullException(nameof(hostPlatform));
        if (hostPlatform.Equals(CapabilityPlatform.Any))
        {
            throw new ArgumentException("Actuator resolution requires one exact host platform.", nameof(hostPlatform));
        }
    }

    /// <inheritdoc />
    public async Task<GovernedActuatorCatalogReadResult> ReadAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        try
        {
            return await ReadCoreAsync(maximumCount, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ReadResult(GovernedActuatorCatalogReadStatus.Unavailable, [], "The current actuator catalog could not be proved from bounded authoritative evidence.");
        }
    }

    private async Task<GovernedActuatorCatalogReadResult> ReadCoreAsync(int maximumCount, CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > MaximumOperationPage)
        {
            return ReadResult(GovernedActuatorCatalogReadStatus.InvalidRequest, [], "The requested operation bound is invalid.");
        }
        if (!TrySnapshotRegistry(out var descriptors))
        {
            return ReadResult(GovernedActuatorCatalogReadStatus.Unavailable, [], "The current actuator registry could not be proved from bounded authoritative evidence.");
        }
        var catalog = await ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
        if (catalog.Status != GovernedActuatorCatalogResolutionStatus.Active)
        {
            return ReadResult(GovernedActuatorCatalogReadStatus.Unavailable, [], catalog.Detail);
        }

        var active = new List<Common.Loops.Execution.Effects.Models.GovernedActuatorOperationDescriptor>();
        foreach (var descriptor in descriptors)
        {
            var entries = catalog.Entries.Where(entry => entry.Descriptor.Id.Equals(descriptor.Capability.Id)).ToArray();
            if (entries.Length == 1
                && IsExactActive(entries[0])
                && entries[0].Lifecycle.DescriptorIdentity.Equals(descriptor.Capability)
                && entries[0].Descriptor.Implementation.Equals(descriptor.Implementation)
                && _registry.TryResolve(descriptor, out _))
            {
                active.Add(descriptor);
            }
        }
        active.Sort((left, right) =>
        {
            var capability = string.Compare(left.Capability.Id.Value, right.Capability.Id.Value, StringComparison.Ordinal);
            return capability != 0 ? capability : string.Compare(left.OperationId, right.OperationId, StringComparison.Ordinal);
        });
        var truncated = active.Count > maximumCount;
        return ReadResult(
            truncated ? GovernedActuatorCatalogReadStatus.Truncated : GovernedActuatorCatalogReadStatus.Available,
            active.Take(maximumCount).ToArray(),
            truncated ? "The active actuator operation snapshot was explicitly truncated." : "The active actuator operation snapshot is current and server-backed.");
    }

    /// <inheritdoc />
    public async Task<GovernedActuatorCatalogResolutionResult> ResolveAsync(
        CapabilityAdmissionPin pin,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ResolveCoreAsync(pin, operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Resolution(GovernedActuatorCatalogResolutionStatus.CatalogUnavailable, null, null, null, "The current actuator catalog could not be proved from bounded authoritative evidence.");
        }
    }

    private async Task<GovernedActuatorCatalogResolutionResult> ResolveCoreAsync(
        CapabilityAdmissionPin pin,
        string operationId,
        CancellationToken cancellationToken)
    {
        if (!IsValidPin(pin)
            || !GovernedActuatorOperationContract.IsOperationId(operationId))
        {
            return Resolution(GovernedActuatorCatalogResolutionStatus.InvalidRequest, null, null, null, "The admitted actuator pin or operation id is malformed.");
        }
        if (!TrySnapshotRegistry(out var descriptors))
        {
            return Resolution(GovernedActuatorCatalogResolutionStatus.CatalogUnavailable, null, null, null, "The current actuator registry could not be proved from bounded authoritative evidence.");
        }
        var registered = descriptors
            .Where(descriptor => descriptor.Capability.Equals(pin.DescriptorIdentity)
                && descriptor.Implementation.Equals(pin.Implementation)
                && string.Equals(descriptor.OperationId, operationId, StringComparison.Ordinal))
            .ToArray();
        if (registered.Length != 1
            || !_registry.TryResolve(registered.SingleOrDefault()!, out var operation)
            || operation is null)
        {
            return Resolution(GovernedActuatorCatalogResolutionStatus.OperationUnregistered, null, null, null, "No exact server registration backs the admitted actuator operation.");
        }

        var catalog = await ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
        if (catalog.Status != GovernedActuatorCatalogResolutionStatus.Active)
        {
            return Resolution(catalog.Status, null, registered[0], null, catalog.Detail);
        }
        var sameId = catalog.Entries.Where(entry => entry.Descriptor.Id.Equals(pin.DescriptorIdentity.Id)).ToArray();
        if (sameId.Length == 0)
        {
            return Resolution(GovernedActuatorCatalogResolutionStatus.PinMissing, null, registered[0], null, "The admitted actuator capability is absent from the current catalog.");
        }
        if (sameId.Length != 1)
        {
            return Resolution(GovernedActuatorCatalogResolutionStatus.CatalogAmbiguous, null, registered[0], null, "The current catalog contains ambiguous capability identity evidence.");
        }
        var entry = sameId[0];
        if (!entry.Lifecycle.DescriptorIdentity.Equals(pin.DescriptorIdentity)
            || !entry.Descriptor.Implementation.Equals(pin.Implementation)
            || pin.Kind != CapabilityKind.Actuator
            || entry.Descriptor.Kind != CapabilityKind.Actuator)
        {
            return Resolution(GovernedActuatorCatalogResolutionStatus.PinDrifted, entry.Descriptor, registered[0], null, "The current capability descriptor or implementation drifted from the exact admitted actuator pin.");
        }
        if (!IsExactActive(entry))
        {
            return Resolution(GovernedActuatorCatalogResolutionStatus.PinInactive, entry.Descriptor, registered[0], null, "The exact actuator capability is not currently active, trusted, healthy, and compatible.");
        }
        return Resolution(GovernedActuatorCatalogResolutionStatus.Active, entry.Descriptor, registered[0], operation, "The exact admitted actuator operation is active and server-backed.");
    }

    private async Task<CatalogSnapshot> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        var entries = new List<CapabilityCatalogEntry>();
        string? cursor = null;
        long? revision = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var pageCount = 0;
        do
        {
            if (++pageCount > MaximumCatalogPages || cursor is not null && !seenCursors.Add(cursor))
            {
                return new CatalogSnapshot(GovernedActuatorCatalogResolutionStatus.CatalogAmbiguous, [], "The capability lifecycle catalog cursor cycled or exceeded its finite page bound.");
            }
            var read = await _catalogStore.ReadAsync(cursor, CatalogPageSize, cancellationToken).ConfigureAwait(false);
            if (read is null || read.Status != CapabilityCatalogReadStatus.Available || read.Page is null)
            {
                return new CatalogSnapshot(GovernedActuatorCatalogResolutionStatus.CatalogUnavailable, [], "The current capability lifecycle catalog is unavailable.");
            }
            var page = read.Page;
            if (!TrySnapshotPage(page.Entries, out var pageEntries))
            {
                return new CatalogSnapshot(GovernedActuatorCatalogResolutionStatus.CatalogAmbiguous, [], "The capability lifecycle catalog changed, repeated, was malformed, or exceeded its finite bound during the read.");
            }
            revision ??= page.CatalogRevision;
            var pageIds = pageEntries.Select(entry => entry.Descriptor?.Id?.Value).ToArray();
            if (revision != page.CatalogRevision
                || page.CatalogRevision < 0
                || entries.Count + pageEntries.Count > MaximumCatalogEntries
                || page.NextCursor is not null && pageEntries.Count == 0
                || pageIds.Any(id => id is null)
                || pageIds.Distinct(StringComparer.Ordinal).Count() != pageIds.Length
                || !pageIds.SequenceEqual(pageIds.OrderBy(id => id, StringComparer.Ordinal))
                || entries.Select(entry => entry.Descriptor.Id.Value).Intersect(pageIds!, StringComparer.Ordinal).Any()
                || page.NextCursor is not null && !string.Equals(page.NextCursor, pageIds.LastOrDefault(), StringComparison.Ordinal))
            {
                return new CatalogSnapshot(GovernedActuatorCatalogResolutionStatus.CatalogAmbiguous, [], "The capability lifecycle catalog changed, repeated, was malformed, or exceeded its finite bound during the read.");
            }
            entries.AddRange(pageEntries);
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return new CatalogSnapshot(GovernedActuatorCatalogResolutionStatus.Active, entries, "The complete current capability lifecycle catalog was read.");
    }

    private bool TrySnapshotRegistry(out IReadOnlyList<Common.Loops.Execution.Effects.Models.GovernedActuatorOperationDescriptor> descriptors)
    {
        descriptors = [];
        var source = _registry.Descriptors;
        if (source is null)
        {
            return false;
        }

        var declaredCount = source.Count;
        if (declaredCount is < 0 or > MaximumOperationPage)
        {
            return false;
        }

        var captured = source.Take(MaximumOperationPage + 1).ToArray();
        if (captured.Length != declaredCount
            || captured.Any(descriptor => descriptor is null || GovernedActuatorOperationContract.Validate(descriptor) is not null))
        {
            return false;
        }

        var keys = captured.Select(descriptor => $"{descriptor.Capability.Id.Value}\u001f{descriptor.Capability.Version.Value}\u001f{descriptor.Capability.Hash.Value}\u001f{descriptor.OperationId}").ToArray();
        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
        {
            return false;
        }

        descriptors = Array.AsReadOnly(captured.Select(descriptor => descriptor with { }).ToArray());
        return true;
    }

    private static bool TrySnapshotPage(
        IReadOnlyList<CapabilityCatalogEntry>? source,
        out IReadOnlyList<CapabilityCatalogEntry> entries)
    {
        entries = [];
        if (source is null)
        {
            return false;
        }

        var declaredCount = source.Count;
        if (declaredCount is < 0 or > CatalogPageSize)
        {
            return false;
        }

        var captured = source.Take(CatalogPageSize + 1).ToArray();
        if (captured.Length != declaredCount || captured.Any(entry => entry is null))
        {
            return false;
        }

        entries = Array.AsReadOnly(captured);
        return true;
    }

    private bool IsExactActive(CapabilityCatalogEntry entry)
        => CapabilityDescriptorIdentity.TryCreate(entry.Descriptor, out var identity, out _)
            && entry.Lifecycle.DescriptorIdentity.Equals(identity)
            && entry.Lifecycle.Declaration == CapabilityDeclarationState.Declared
            && entry.Lifecycle.Installation == CapabilityInstallationState.Installed
            && entry.Lifecycle.Enablement == CapabilityEnablementState.Enabled
            && entry.Lifecycle.Health == CapabilityHealthState.Healthy
            && entry.Lifecycle.Retirement is CapabilityRetirementState.Active or CapabilityRetirementState.Deprecated
            && entry.Lifecycle.Trust == CapabilityTrustState.Verified
            && entry.Descriptor.Compatibility.HostVersionRange.Contains(_hostContractVersion)
            && entry.Descriptor.Compatibility.SupportedPlatforms.Any(platform => platform.Equals(CapabilityPlatform.Any) || platform.Equals(_hostPlatform));

    private static bool IsValidPin(CapabilityAdmissionPin? pin)
    {
        if (pin?.DescriptorIdentity?.Id is null
            || pin.DescriptorIdentity.Version is null
            || pin.DescriptorIdentity.Hash is null
            || pin.Implementation?.ProviderId is null)
        {
            return false;
        }

        return pin.Kind == CapabilityKind.Actuator
            && IsExactIdentity(pin.DescriptorIdentity)
            && IsExactProvider(pin.Implementation.ProviderId)
            && IsCanonicalPath(pin.Implementation.ImplementationId, CapabilityContractLimits.MaxImplementationIdCharacters)
            && IsValidProvenance(pin.Provenance)
            && IsValidArtifact(pin.Artifact, pin.DescriptorIdentity.Id)
            && IsSafeNormalized(pin.SafeDescription, CapabilityContractLimits.MaxPurposeCharacters)
            && DescriptorFieldsRoundTrip(pin);
    }

    private static bool IsExactIdentity(CapabilityDescriptorIdentity identity)
        => CapabilityId.TryParse(identity.Id.Value, out var id, out _)
            && id!.Equals(identity.Id)
            && CapabilityVersion.TryParse(identity.Version.Value, out var version, out _)
            && IsExactVersion(version!, identity.Version)
            && CapabilityDescriptorHash.TryParse(identity.Hash.Value, out var hash, out _)
            && hash!.Equals(identity.Hash);

    private static bool IsExactProvider(CapabilityProviderId provider)
        => CapabilityProviderId.TryParse(provider.Value, out var parsed, out _)
            && parsed!.Equals(provider);

    private static bool IsExactVersion(CapabilityVersion parsed, CapabilityVersion supplied)
        => parsed.Equals(supplied)
            && parsed.Major == supplied.Major
            && parsed.Minor == supplied.Minor
            && parsed.Patch == supplied.Patch
            && string.Equals(parsed.BuildMetadata, supplied.BuildMetadata, StringComparison.Ordinal)
            && parsed.PreReleaseIdentifiers.SequenceEqual(supplied.PreReleaseIdentifiers, StringComparer.Ordinal);

    private static bool IsExactDigest(CapabilityIntegrityDigest? digest)
        => digest is null
            || CapabilityIntegrityDigest.TryParse(digest.Value, out var parsed, out _)
                && parsed!.Equals(digest);

    private static bool IsValidProvenance(CapabilityProvenance? provenance)
    {
        if (provenance is null
            || !Enum.IsDefined(provenance.Kind)
            || provenance.Kind == CapabilityProvenanceKind.Unknown
            || !IsSafeSourceUri(provenance.SourceUri)
            || provenance.SourceRevision is not null
                && (provenance.SourceRevision.Length is < 1 or > CapabilityContractLimits.MaxSourceRevisionCharacters
                    || provenance.SourceRevision.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-' or '/' or '@'))))
        {
            return false;
        }

        return IsExactDigest(provenance.Integrity)
            && (provenance.Kind != CapabilityProvenanceKind.RemoteArtifact || provenance.Integrity is not null);
    }

    private static bool IsValidArtifact(CapabilityDependencyArtifactMetadata? artifact, CapabilityId subjectId)
    {
        if (artifact is null
            || !IsExactDigest(artifact.Checksum)
            || artifact.Signature is not null
                && (artifact.Signature.Length is < 1 or > CapabilityContractLimits.MaxArtifactSignatureCharacters
                    || artifact.Signature.Any(character => character is < (char)0x21 or > (char)0x7e)))
        {
            return false;
        }

        var manifest = new CapabilityDependencyManifest(
            CapabilityDependencyManifest.CurrentSchemaVersion,
            CapabilityDependencyManifestKind.CapabilityPackage,
            subjectId,
            [],
            [],
            artifact);
        return CapabilityDependencyManifestJson.TrySerialize(manifest, out var json, out _)
            && CapabilityDependencyManifestJson.TryDeserialize(json, out var parsed, out _)
            && parsed?.Artifact is { } parsedArtifact
            && Equals(parsedArtifact.Checksum, artifact.Checksum)
            && string.Equals(parsedArtifact.Signature, artifact.Signature, StringComparison.Ordinal);
    }

    private static bool DescriptorFieldsRoundTrip(CapabilityAdmissionPin pin)
    {
        if (!CapabilityVersionRange.TryParse("*", out var hostRange, out _)
            || !CapabilityJsonSchema.TryCreate($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\"}}", out var schema, out _))
        {
            return false;
        }

        var descriptor = new CapabilityDescriptor(
            CapabilityDescriptor.CurrentSchemaVersion,
            pin.DescriptorIdentity.Id,
            pin.Kind,
            pin.DescriptorIdentity.Version,
            pin.Implementation,
            pin.Provenance,
            new CapabilityCompatibility(hostRange!, [CapabilityPlatform.Any]),
            pin.SafeDescription,
            schema!,
            schema!,
            new CapabilityResourceLimits(1, 1, 1, 1),
            CapabilitySideEffectClass.None,
            new CapabilityAccessRequirements([], CapabilityEgressMode.None, [], []));
        return CapabilityDescriptorJson.TrySerialize(descriptor, out var json, out _)
            && CapabilityDescriptorJson.TryDeserialize(json, out var parsed, out _)
            && parsed is not null
            && parsed.Id.Equals(pin.DescriptorIdentity.Id)
            && IsExactVersion(parsed.Version, pin.DescriptorIdentity.Version)
            && parsed.Implementation.Equals(pin.Implementation)
            && parsed.Provenance.Equals(pin.Provenance)
            && string.Equals(parsed.Purpose, pin.SafeDescription, StringComparison.Ordinal);
    }

    private static bool IsSafeSourceUri(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > CapabilityContractLimits.MaxSourceUriCharacters
            || value.Any(character => character is < (char)0x21 or > (char)0x7e)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.Scheme is not "https" and not "file" and not "pkg" and not "urn")
        {
            return false;
        }

        return string.Equals(uri.AbsoluteUri, value, StringComparison.Ordinal);
    }

    private static bool IsSafeNormalized(string? value, int maximum)
        => value is { Length: > 0 }
            && value.Length <= maximum
            && value.IsNormalized(System.Text.NormalizationForm.FormC)
            && !value.Any(character => char.IsControl(character) || char.IsSurrogate(character));

    private static bool IsCanonicalPath(string? value, int maximum)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maximum || value[0] == '/' || value[^1] == '/')
        {
            return false;
        }

        var segments = value.Split('/');
        return segments.Length <= 8 && segments.All(IsCanonicalToken);
    }

    private static bool IsCanonicalToken(string value)
        => value.Length is >= 1 and <= 63
            && IsLowerAlphaNumeric(value[0])
            && IsLowerAlphaNumeric(value[^1])
            && value.All(character => IsLowerAlphaNumeric(character) || character is '-' or '_' or '.');

    private static bool IsLowerAlphaNumeric(char character)
        => character is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static GovernedActuatorCatalogReadResult ReadResult(
        GovernedActuatorCatalogReadStatus status,
        IReadOnlyList<Common.Loops.Execution.Effects.Models.GovernedActuatorOperationDescriptor> operations,
        string detail)
        => new(status, operations, detail);

    private static GovernedActuatorCatalogResolutionResult Resolution(
        GovernedActuatorCatalogResolutionStatus status,
        CapabilityDescriptor? capability,
        Common.Loops.Execution.Effects.Models.GovernedActuatorOperationDescriptor? descriptor,
        IGovernedActuatorOperation? operation,
        string detail)
        => new(status, capability, descriptor, operation, detail);

    private sealed record CatalogSnapshot(
        GovernedActuatorCatalogResolutionStatus Status,
        IReadOnlyList<CapabilityCatalogEntry> Entries,
        string Detail);
}
