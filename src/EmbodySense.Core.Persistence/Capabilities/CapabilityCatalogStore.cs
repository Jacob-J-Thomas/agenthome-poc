using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Persists one bounded, integrity-protected, schema-version-1 workspace capability catalog.</summary>
/// <remarks>
/// A guarded cross-process file lock serializes reads and optimistic mutations. Workspace artifacts are untrusted
/// projections authenticated by an injected server-owned trust provider outside the workspace. That provider binds proofs
/// to canonical workspace identity and retains only current and immediately previous generation digests. Commits write the
/// prior current proof, then the candidate primary, then advance the monotonic anchor. Any artifact matching only the
/// retained previous generation is exposed read-only and never becomes a mutation base. Durable operation receipts are
/// bounded and never evicted, preserving idempotency identity.
/// </remarks>
public sealed class CapabilityCatalogStore : ICapabilityCatalogStore
{
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions(writeIndented: true);
    private static readonly JsonSerializerOptions _hashOptions = CreateJsonOptions(writeIndented: false);
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly WorkspacePaths _paths;
    private readonly CapabilityCatalogPathGuard _pathGuard;
    private readonly ICapabilityCatalogTrustProvider _trustProvider;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a catalog store rooted in one workspace.</summary>
    /// <param name="paths">The canonical workspace paths.</param>
    /// <param name="timeProvider">The optional trusted store clock.</param>
    /// <param name="durabilityBarrier">The optional trusted post-rename durability adapter.</param>
    public CapabilityCatalogStore(WorkspacePaths paths, TimeProvider? timeProvider = null, ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null) : this(paths, FileCapabilityCatalogTrustProvider.CreateDefault(), timeProvider, durabilityBarrier)
    {
    }

    /// <summary>Creates a catalog store over an explicit server-owned trust provider.</summary>
    /// <param name="paths">The canonical workspace paths.</param>
    /// <param name="trustProvider">The server-owned trust provider outside mutable workspace storage.</param>
    /// <param name="timeProvider">The optional trusted store clock.</param>
    /// <param name="durabilityBarrier">The optional trusted post-rename durability adapter.</param>
    public CapabilityCatalogStore(WorkspacePaths paths, ICapabilityCatalogTrustProvider trustProvider, TimeProvider? timeProvider = null, ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(trustProvider);
        _paths = paths;
        _pathGuard = new CapabilityCatalogPathGuard(paths.RootPath, durabilityBarrier ?? NativeCapabilityCatalogDurabilityBarrier.Instance);
        _trustProvider = trustProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<CapabilityCatalogReadResult> ReadAsync(string? startAfterId, int maximumCount, CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > CapabilityCatalogLimits.MaximumPageSize || startAfterId is not null && !CapabilityId.TryParse(startAfterId, out _, out _))
        {
            return new CapabilityCatalogReadResult(CapabilityCatalogReadStatus.Unavailable, null, "The catalog query is outside the bounded schema-1 contract.");
        }

        try
        {
            await using var ownership = await AcquireLockAsync(cancellationToken);
            var workspaceIdentity = CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(ownership.PhysicalIdentityMaterial);
            var trust = await _trustProvider.ReadAsync(workspaceIdentity, cancellationToken);
            var loaded = await LoadAsync(ownership, workspaceIdentity, trust, cancellationToken);
            if (loaded.Document is null)
            {
                return new CapabilityCatalogReadResult(CapabilityCatalogReadStatus.Unavailable, null, "No trustworthy capability catalog state is available.");
            }

            var ordered = loaded.Document.Entries
                .Select(MapEntry)
                .Where(entry => startAfterId is null || string.Compare(entry.Descriptor.Id.Value, startAfterId, StringComparison.Ordinal) > 0)
                .OrderBy(entry => entry.Descriptor.Id.Value, StringComparer.Ordinal)
                .ToArray();
            var pageEntries = ordered.Take(maximumCount).ToArray();
            var nextCursor = ordered.Length > maximumCount ? pageEntries[^1].Descriptor.Id.Value : null;
            var page = new CapabilityCatalogPage(loaded.Document.CatalogRevision, pageEntries, nextCursor);
            var status = loaded.Recovered ? CapabilityCatalogReadStatus.RecoveredLastProved : CapabilityCatalogReadStatus.Available;
            var detail = loaded.Recovered ? "The primary catalog was unsafe; the last proved state is available read-only." : "The current capability catalog is available.";
            return new CapabilityCatalogReadResult(status, page, detail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return new CapabilityCatalogReadResult(CapabilityCatalogReadStatus.Unavailable, null, "The capability catalog could not be read safely.");
        }
    }

    /// <summary>Reads every bounded integrity-proved operation receipt retained for one exact capability.</summary>
    /// <param name="id">The canonical capability identity.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The current or recovered receipt snapshots without mutation authority.</returns>
    public async Task<CapabilityCatalogOperationReceiptReadResult> ReadOperationReceiptsAsync(CapabilityId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        try
        {
            await using var ownership = await AcquireLockAsync(cancellationToken);
            var workspaceIdentity = CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(ownership.PhysicalIdentityMaterial);
            var trust = await _trustProvider.ReadAsync(workspaceIdentity, cancellationToken);
            var loaded = await LoadAsync(ownership, workspaceIdentity, trust, cancellationToken);
            if (loaded.Document is null)
            {
                return new CapabilityCatalogOperationReceiptReadResult(CapabilityCatalogReadStatus.Unavailable, null, [], "No trustworthy capability catalog operation receipts are available.");
            }

            var receipts = loaded.Document.Operations
                .Where(operation => string.Equals(operation.CapabilityId, id.Value, StringComparison.Ordinal))
                .OrderBy(operation => operation.OperationId, StringComparer.Ordinal)
                .Select(operation => new CapabilityCatalogOperationReceipt(operation.OperationId, operation.Outcome, operation.CatalogRevision, MapReceipt(loaded.Document, operation)))
                .ToArray();
            var status = loaded.Recovered ? CapabilityCatalogReadStatus.RecoveredLastProved : CapabilityCatalogReadStatus.Available;
            var detail = loaded.Recovered ? "The primary catalog was unsafe; last-proved operation receipts are available read-only." : "The current integrity-proved operation receipts are available.";
            return new CapabilityCatalogOperationReceiptReadResult(status, loaded.Document.Generation, receipts, detail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return new CapabilityCatalogOperationReceiptReadResult(CapabilityCatalogReadStatus.Unavailable, null, [], "The capability catalog operation receipts could not be read safely.");
        }
    }

    /// <inheritdoc />
    public Task<CapabilityCatalogMutationResult> MutateAsync(CapabilityCatalogMutation mutation, CancellationToken cancellationToken = default) => MutateAsync(mutation, null, cancellationToken);

    /// <summary>Applies one mutation only when the authenticated catalog remains at the caller-proved generation.</summary>
    /// <param name="mutation">The mutation.</param>
    /// <param name="expectedCatalogGeneration">The exact authenticated document generation proved by the caller.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The durable structured outcome.</returns>
    public Task<CapabilityCatalogMutationResult> MutateAtGenerationAsync(CapabilityCatalogMutation mutation, long expectedCatalogGeneration, CancellationToken cancellationToken = default)
    {
        return expectedCatalogGeneration < 0
            ? Task.FromResult(Result(CapabilityCatalogMutationStatus.Invalid, mutation?.OperationId ?? string.Empty, null, null, "The expected catalog generation is invalid."))
            : MutateAsync(mutation, expectedCatalogGeneration, cancellationToken);
    }

    private async Task<CapabilityCatalogMutationResult> MutateAsync(CapabilityCatalogMutation mutation, long? expectedCatalogGeneration, CancellationToken cancellationToken)
    {
        var validation = ValidateMutation(mutation);
        if (validation is not null)
        {
            return Result(CapabilityCatalogMutationStatus.Invalid, mutation?.OperationId ?? string.Empty, null, null, validation);
        }

        var operationId = mutation.OperationId;
        try
        {
            await using var ownership = await AcquireLockAsync(cancellationToken);
            var workspaceIdentity = CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(ownership.PhysicalIdentityMaterial);
            var trust = await _trustProvider.ReadAsync(workspaceIdentity, cancellationToken);
            var loaded = await LoadAsync(ownership, workspaceIdentity, trust, cancellationToken);
            if (loaded.Document is null || loaded.Recovered)
            {
                return Result(CapabilityCatalogMutationStatus.Unavailable, operationId, loaded.Document?.CatalogRevision, null, "Mutation requires the current proved primary catalog state.");
            }

            var current = loaded.Document;
            if (expectedCatalogGeneration is not null && expectedCatalogGeneration.Value != current.Generation)
            {
                return Result(CapabilityCatalogMutationStatus.Conflict, operationId, current.CatalogRevision, FindEntry(current, mutation.CapabilityId!.Value), "The authenticated catalog generation changed after the caller's proof.");
            }

            var requestHash = ComputeRequestHash(mutation);
            var existingReceipt = current.Operations.SingleOrDefault(item => string.Equals(item.OperationId, operationId, StringComparison.Ordinal));
            if (existingReceipt is not null)
            {
                if (!string.Equals(existingReceipt.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    return Result(CapabilityCatalogMutationStatus.Conflict, operationId, current.CatalogRevision, FindEntry(current, existingReceipt.CapabilityId), "The operation id is already bound to different mutation content.");
                }

                return Result(CapabilityCatalogMutationStatus.Replayed, operationId, existingReceipt.CatalogRevision, MapReceipt(current, existingReceipt), $"Replayed durable {existingReceipt.Outcome} outcome.");
            }

            if (mutation.ExpectedCatalogRevision != current.CatalogRevision)
            {
                return Result(CapabilityCatalogMutationStatus.Conflict, operationId, current.CatalogRevision, FindEntry(current, mutation.CapabilityId!.Value), "The expected catalog revision is stale.");
            }

            if (current.Operations.Count >= CapabilityCatalogLimits.MaximumOperationReceipts)
            {
                return Result(CapabilityCatalogMutationStatus.Unavailable, operationId, current.CatalogRevision, null, "The durable operation receipt quota is exhausted; no receipt was evicted.");
            }

            var transition = ApplyTransition(current, mutation);
            if (transition.Status is CapabilityCatalogMutationStatus.Invalid or CapabilityCatalogMutationStatus.NotFound or CapabilityCatalogMutationStatus.Unavailable)
            {
                return Result(transition.Status, operationId, current.CatalogRevision, transition.Entry is null ? null : MapEntry(transition.Entry), transition.Detail);
            }

            var resultingRevision = transition.Status == CapabilityCatalogMutationStatus.Applied ? checked(current.CatalogRevision + 1) : current.CatalogRevision;
            var entries = current.Entries.Where(item => !Targets(item, mutation.CapabilityId!.Value)).Append(transition.Entry!).OrderBy(GetCapabilityId, StringComparer.Ordinal).ToArray();
            var receipt = CreateReceipt(operationId, requestHash, transition.Status, resultingRevision, mutation.CapabilityId!.Value, transition.Entry!);
            var candidate = new CapabilityCatalogDocument(CapabilityCatalogDocument.CurrentSchemaVersion, workspaceIdentity, checked(current.Generation + 1), resultingRevision, entries, current.Operations.Append(receipt).OrderBy(item => item.OperationId, StringComparer.Ordinal).ToArray(), string.Empty, string.Empty);
            await CommitAsync(ownership, workspaceIdentity, current, candidate, trust, cancellationToken);
            return Result(transition.Status, operationId, resultingRevision, MapEntry(transition.Entry!), transition.Detail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return Result(CapabilityCatalogMutationStatus.Unavailable, operationId, null, null, "The capability catalog mutation outcome could not be established safely.");
        }
    }

    private Transition ApplyTransition(CapabilityCatalogDocument current, CapabilityCatalogMutation mutation)
    {
        var existing = current.Entries.SingleOrDefault(item => Targets(item, mutation.CapabilityId!.Value));
        if (mutation.Kind == CapabilityCatalogMutationKind.Declare)
        {
            if (existing is not null)
            {
                return new Transition(CapabilityCatalogMutationStatus.Invalid, existing, "The canonical capability id is already declared or retained as a tombstone.");
            }

            if (current.Entries.Count >= CapabilityCatalogLimits.MaximumEntries)
            {
                return new Transition(CapabilityCatalogMutationStatus.Unavailable, null, "The bounded capability entry quota is exhausted.");
            }

            _ = CapabilityDescriptorJson.TrySerialize(mutation.Descriptor, out var descriptorJson, out _);
            var created = new CapabilityCatalogEntryDocument(descriptorJson!, 1, CapabilityDeclarationState.Declared, CapabilityInstallationState.NotInstalled, CapabilityEnablementState.Disabled, CapabilityHealthState.Unknown, CapabilityRetirementState.Active, CapabilityTrustState.Unverified, _timeProvider.GetUtcNow(), mutation.OperationId);
            return new Transition(CapabilityCatalogMutationStatus.Applied, created, "Declared without installation, enablement, trust, assignment, or authority.");
        }

        if (existing is null)
        {
            return new Transition(CapabilityCatalogMutationStatus.NotFound, null, "The target capability is not declared.");
        }

        if (existing.Retirement == CapabilityRetirementState.Removed && mutation.Kind != CapabilityCatalogMutationKind.Remove)
        {
            return new Transition(CapabilityCatalogMutationStatus.Invalid, existing, "A retained removal tombstone cannot be mutated or resurrected.");
        }

        var changed = mutation.Kind switch
        {
            CapabilityCatalogMutationKind.Install => existing with { Installation = CapabilityInstallationState.Installed },
            CapabilityCatalogMutationKind.Enable => existing with { Enablement = CapabilityEnablementState.Enabled },
            CapabilityCatalogMutationKind.Disable => existing with { Enablement = CapabilityEnablementState.Disabled },
            CapabilityCatalogMutationKind.Verify => existing with { Trust = CapabilityTrustState.Verified },
            CapabilityCatalogMutationKind.RejectTrust => existing with { Trust = CapabilityTrustState.Rejected },
            CapabilityCatalogMutationKind.MarkHealthy => existing with { Health = CapabilityHealthState.Healthy },
            CapabilityCatalogMutationKind.MarkDegraded => existing with { Health = CapabilityHealthState.Degraded },
            CapabilityCatalogMutationKind.MarkUnavailable => existing with { Health = CapabilityHealthState.Unavailable },
            CapabilityCatalogMutationKind.Deprecate => existing with { Retirement = CapabilityRetirementState.Deprecated },
            CapabilityCatalogMutationKind.Remove => existing with { Declaration = CapabilityDeclarationState.Withdrawn, Installation = CapabilityInstallationState.NotInstalled, Enablement = CapabilityEnablementState.Disabled, Health = CapabilityHealthState.Unavailable, Retirement = CapabilityRetirementState.Removed },
            _ => existing
        };
        if (changed == existing)
        {
            return new Transition(CapabilityCatalogMutationStatus.NoChange, existing, "The requested lifecycle axis already had the target state.");
        }

        changed = changed with { Revision = checked(existing.Revision + 1), UpdatedAtUtc = _timeProvider.GetUtcNow(), LastOperationId = mutation.OperationId };
        return new Transition(CapabilityCatalogMutationStatus.Applied, changed, "The requested lifecycle axis was updated without changing assignment or authority.");
    }

    private async Task<LoadResult> LoadAsync(CapabilityCatalogPathSession fileSystem, string workspaceIdentity, CapabilityCatalogTrustState? trust, CancellationToken cancellationToken)
    {
        var primaryExists = fileSystem.FileExists(_paths.CapabilityCatalogDocumentPath);
        var proofExists = fileSystem.FileExists(_paths.CapabilityCatalogProofPath);
        var empty = EmptyDocument(workspaceIdentity);
        if (trust is null)
        {
            return primaryExists || proofExists ? new LoadResult(null, Recovered: false) : new LoadResult(empty, Recovered: false);
        }

        if (!primaryExists && !proofExists)
        {
            return MatchesCurrent(empty, trust) ? new LoadResult(empty, Recovered: false) : new LoadResult(null, Recovered: false);
        }

        var primary = primaryExists ? await TryReadAsync(fileSystem, workspaceIdentity, _paths.CapabilityCatalogDocumentPath, cancellationToken) : null;
        var proof = proofExists ? await TryReadAsync(fileSystem, workspaceIdentity, _paths.CapabilityCatalogProofPath, cancellationToken) : null;
        if (primary is not null && MatchesCurrent(primary, trust))
        {
            return new LoadResult(primary, Recovered: false);
        }

        if (proof is not null && (MatchesCurrent(proof, trust) || MatchesPrevious(proof, trust)))
        {
            return new LoadResult(proof, Recovered: true);
        }

        return primary is not null && MatchesPrevious(primary, trust) ? new LoadResult(primary, Recovered: true) : new LoadResult(null, Recovered: false);
    }

    private async Task<CapabilityCatalogDocument?> TryReadAsync(CapabilityCatalogPathSession fileSystem, string workspaceIdentity, string path, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await fileSystem.ReadAllBytesAsync(path, CapabilityCatalogLimits.MaximumArtifactUtf8Bytes, cancellationToken);
            var document = JsonSerializer.Deserialize<CapabilityCatalogDocument>(_strictUtf8.GetString(bytes), _jsonOptions);
            if (document is null || !ValidateDocument(document, workspaceIdentity) || !await _trustProvider.VerifyArtifactAsync(workspaceIdentity, document.Generation, document.ContentDigest, document.AuthenticationTag, cancellationToken))
            {
                return null;
            }

            return document;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task CommitAsync(CapabilityCatalogPathSession fileSystem, string workspaceIdentity, CapabilityCatalogDocument current, CapabilityCatalogDocument candidate, CapabilityCatalogTrustState? trust, CancellationToken cancellationToken)
    {
        var currentDigest = ComputeContentDigest(current).Value;
        trust ??= await _trustProvider.InitializeAsync(workspaceIdentity, current.Generation, currentDigest, cancellationToken);
        if (!MatchesCurrent(current with { ContentDigest = currentDigest }, trust))
        {
            throw new IOException("The server-owned capability catalog trust anchor no longer matches the mutation base.");
        }

        var currentJson = await SerializeAsync(workspaceIdentity, current, cancellationToken);
        await fileSystem.WriteTextAtomicallyAsync(_paths.CapabilityCatalogProofPath, currentJson.Json, cancellationToken);
        var candidateJson = await SerializeAsync(workspaceIdentity, candidate, cancellationToken);
        await fileSystem.WriteTextAtomicallyAsync(_paths.CapabilityCatalogDocumentPath, candidateJson.Json, cancellationToken);
        _ = await _trustProvider.AdvanceAsync(workspaceIdentity, trust.CurrentGeneration, trust.CurrentContentDigest, candidate.Generation, candidateJson.ContentDigest, cancellationToken);
    }

    private async Task<CapabilityCatalogPathSession> AcquireLockAsync(CancellationToken cancellationToken)
    {
        return await _pathGuard.TryAcquireExclusiveSessionAsync(_paths.CapabilityCatalogLockPath, createRoot: false, cancellationToken) ?? throw new IOException("The capability catalog workspace root is unavailable.");
    }

    private bool ValidateDocument(CapabilityCatalogDocument document, string workspaceIdentity)
    {
        if (document.SchemaVersion != CapabilityCatalogDocument.CurrentSchemaVersion || !string.Equals(document.WorkspaceIdentity, workspaceIdentity, StringComparison.Ordinal) || document.Generation < 0 || document.CatalogRevision < 0 || document.Entries is null || document.Operations is null || document.Entries.Count > CapabilityCatalogLimits.MaximumEntries || document.Operations.Count > CapabilityCatalogLimits.MaximumOperationReceipts)
        {
            return false;
        }

        if (!CapabilityIntegrityDigest.TryParse(document.ContentDigest, out var suppliedDigest, out _) || !suppliedDigest!.FixedTimeEquals(ComputeContentDigest(document)))
        {
            return false;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.Entries)
        {
            if (!TryMapEntry(entry, out var mapped) || mapped!.Revision > document.CatalogRevision || !ids.Add(mapped.Descriptor.Id.Value))
            {
                return false;
            }
        }

        if (!document.Entries.Select(GetCapabilityId).SequenceEqual(document.Entries.Select(GetCapabilityId).Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            return false;
        }

        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in document.Operations)
        {
            var currentEntry = document.Entries.SingleOrDefault(entry => Targets(entry, operation.CapabilityId));
            if (!IsOperationIdValid(operation.OperationId) || !operationIds.Add(operation.OperationId) || !CapabilityIntegrityDigest.TryParse(operation.RequestHash, out _, out _) || operation.Outcome is not CapabilityCatalogMutationStatus.Applied and not CapabilityCatalogMutationStatus.NoChange || operation.CatalogRevision < 0 || operation.CatalogRevision > document.CatalogRevision || !CapabilityId.TryParse(operation.CapabilityId, out _, out _) || currentEntry is null || !TryMapReceipt(currentEntry, operation, out _))
            {
                return false;
            }
        }

        return document.Operations.Select(item => item.OperationId).SequenceEqual(document.Operations.Select(item => item.OperationId).Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static bool TryMapEntry(CapabilityCatalogEntryDocument document, out CapabilityCatalogEntry? entry)
    {
        entry = null;
        if (document is null || document.Revision < 1 || document.UpdatedAtUtc.Offset != TimeSpan.Zero || !IsOperationIdValid(document.LastOperationId) || !CapabilityDescriptorJson.TryDeserialize(document.DescriptorJson, out var descriptor, out _) || !CapabilityDescriptorJson.TrySerialize(descriptor, out var canonical, out _) || !string.Equals(canonical, document.DescriptorJson, StringComparison.Ordinal) || !CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _))
        {
            return false;
        }

        var lifecycle = new CapabilityLifecycleSnapshot(CapabilityLifecycleSnapshot.CurrentSchemaVersion, identity!, document.Declaration, document.Installation, document.Enablement, document.Health, document.Retirement, document.Trust);
        if (!CapabilityLifecycleSnapshotValidator.Validate(lifecycle).IsValid || lifecycle.Retirement == CapabilityRetirementState.Removed && (lifecycle.Declaration != CapabilityDeclarationState.Withdrawn || lifecycle.Installation != CapabilityInstallationState.NotInstalled || lifecycle.Enablement != CapabilityEnablementState.Disabled))
        {
            return false;
        }

        entry = new CapabilityCatalogEntry(descriptor!, lifecycle, document.Revision, document.UpdatedAtUtc, document.LastOperationId);
        return true;
    }

    private static CapabilityCatalogEntry MapEntry(CapabilityCatalogEntryDocument document)
    {
        if (!TryMapEntry(document, out var entry))
        {
            throw new FormatException("The capability catalog entry is invalid.");
        }

        return entry!;
    }

    private static CapabilityCatalogEntry? FindEntry(CapabilityCatalogDocument document, string id)
    {
        var entry = document.Entries.SingleOrDefault(item => Targets(item, id));
        return entry is null ? null : MapEntry(entry);
    }

    private static CapabilityCatalogOperationDocument CreateReceipt(string operationId, string requestHash, CapabilityCatalogMutationStatus outcome, long catalogRevision, string capabilityId, CapabilityCatalogEntryDocument entry)
    {
        return new CapabilityCatalogOperationDocument(operationId, requestHash, outcome, catalogRevision, capabilityId, entry.Revision, entry.Declaration, entry.Installation, entry.Enablement, entry.Health, entry.Retirement, entry.Trust, entry.UpdatedAtUtc, entry.LastOperationId);
    }

    private static CapabilityCatalogEntry MapReceipt(CapabilityCatalogDocument document, CapabilityCatalogOperationDocument operation)
    {
        var current = document.Entries.Single(entry => Targets(entry, operation.CapabilityId));
        if (!TryMapReceipt(current, operation, out var entry))
        {
            throw new FormatException("The durable capability operation receipt is invalid.");
        }

        return entry!;
    }

    private static bool TryMapReceipt(CapabilityCatalogEntryDocument current, CapabilityCatalogOperationDocument operation, out CapabilityCatalogEntry? entry)
    {
        entry = null;
        var snapshot = current with
        {
            Revision = operation.EntryRevision,
            Declaration = operation.Declaration,
            Installation = operation.Installation,
            Enablement = operation.Enablement,
            Health = operation.Health,
            Retirement = operation.Retirement,
            Trust = operation.Trust,
            UpdatedAtUtc = operation.UpdatedAtUtc,
            LastOperationId = operation.LastOperationId
        };
        return operation.EntryRevision <= current.Revision && TryMapEntry(snapshot, out entry);
    }

    private static string? ValidateMutation(CapabilityCatalogMutation? mutation)
    {
        if (mutation is null || !Enum.IsDefined(mutation.Kind) || !IsOperationIdValid(mutation.OperationId) || mutation.ExpectedCatalogRevision < 0)
        {
            return "The catalog mutation identity, kind, or expected revision is invalid.";
        }

        if (mutation.Kind == CapabilityCatalogMutationKind.Declare)
        {
            if (mutation.Descriptor is null || mutation.CapabilityId is null || !mutation.CapabilityId.Equals(mutation.Descriptor.Id) || !CapabilityDescriptorJson.TrySerialize(mutation.Descriptor, out _, out _))
            {
                return "A declaration requires one matching validated safe descriptor.";
            }
        }
        else if (mutation.CapabilityId is null || mutation.Descriptor is not null)
        {
            return "A lifecycle transition requires one canonical target and cannot supply descriptor content.";
        }

        return null;
    }

    private static bool IsOperationIdValid(string? value)
    {
        return !string.IsNullOrEmpty(value) && value.Length <= CapabilityCatalogLimits.MaximumOperationIdCharacters && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.');
    }

    private static string ComputeRequestHash(CapabilityCatalogMutation mutation)
    {
        var descriptorJson = mutation.Descriptor is null ? string.Empty : CapabilityDescriptorJson.TrySerialize(mutation.Descriptor, out var serialized, out _) ? serialized! : string.Empty;
        var content = $"{(int)mutation.Kind}\n{mutation.OperationId}\n{mutation.ExpectedCatalogRevision}\n{mutation.CapabilityId!.Value}\n{descriptorJson}";
        return CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(content)).Value;
    }

    private async Task<SerializedDocument> SerializeAsync(string workspaceIdentity, CapabilityCatalogDocument document, CancellationToken cancellationToken)
    {
        var contentDigest = ComputeContentDigest(document).Value;
        var withDigest = document with { ContentDigest = contentDigest, AuthenticationTag = string.Empty };
        var authenticationTag = await _trustProvider.AuthenticateArtifactAsync(workspaceIdentity, document.Generation, contentDigest, cancellationToken);
        var json = JsonSerializer.Serialize(withDigest with { AuthenticationTag = authenticationTag }, _jsonOptions) + Environment.NewLine;
        if (Encoding.UTF8.GetByteCount(json) > CapabilityCatalogLimits.MaximumArtifactUtf8Bytes)
        {
            throw new IOException("The bounded capability catalog artifact limit would be exceeded.");
        }

        return new SerializedDocument(json, contentDigest);
    }

    private static CapabilityIntegrityDigest ComputeContentDigest(CapabilityCatalogDocument document)
    {
        var content = JsonSerializer.Serialize(document with { ContentDigest = string.Empty, AuthenticationTag = string.Empty }, _hashOptions);
        return CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(content));
    }

    private static CapabilityCatalogDocument EmptyDocument(string workspaceIdentity)
    {
        var empty = new CapabilityCatalogDocument(CapabilityCatalogDocument.CurrentSchemaVersion, workspaceIdentity, 0, 0, [], [], string.Empty, string.Empty);
        return empty with { ContentDigest = ComputeContentDigest(empty).Value };
    }

    private static bool MatchesCurrent(CapabilityCatalogDocument document, CapabilityCatalogTrustState trust) => document.Generation == trust.CurrentGeneration && string.Equals(document.ContentDigest, trust.CurrentContentDigest, StringComparison.Ordinal);

    private static bool MatchesPrevious(CapabilityCatalogDocument document, CapabilityCatalogTrustState trust) => trust.PreviousGeneration == document.Generation && string.Equals(document.ContentDigest, trust.PreviousContentDigest, StringComparison.Ordinal);

    private static JsonSerializerOptions CreateJsonOptions(bool writeIndented)
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = writeIndented,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) }
        };
    }

    private static bool Targets(CapabilityCatalogEntryDocument entry, string id) => string.Equals(GetCapabilityId(entry), id, StringComparison.Ordinal);

    private static string GetCapabilityId(CapabilityCatalogEntryDocument entry)
    {
        return CapabilityDescriptorJson.TryDeserialize(entry.DescriptorJson, out var descriptor, out _) ? descriptor!.Id.Value : throw new FormatException("The catalog descriptor is invalid.");
    }

    private static CapabilityCatalogMutationResult Result(CapabilityCatalogMutationStatus status, string operationId, long? revision, CapabilityCatalogEntry? entry, string detail) => new(status, operationId, revision, entry, detail);

    private static bool IsAvailabilityFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or FormatException or JsonException or OverflowException;

    private sealed record LoadResult(CapabilityCatalogDocument? Document, bool Recovered);

    private sealed record SerializedDocument(string Json, string ContentDigest);

    private sealed record Transition(CapabilityCatalogMutationStatus Status, CapabilityCatalogEntryDocument? Entry, string Detail);
}
