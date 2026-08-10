using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using System.Security.Cryptography;
using System.Text;

namespace EmbodySense.Core.Application.ContextualRoles;

/// <summary>Orchestrates read-only contextual-role catalog, lifecycle, and registered-source inspection.</summary>
/// <remarks>Inspection never assigns a role, admits a loop, loads instruction content, or grants effective authority.</remarks>
public sealed class ContextualRoleInspectionService
{
    private readonly string _workspaceId;
    private readonly IContextualRoleCatalogReader _catalogReader;
    private readonly IContextualRoleRevisionReader _revisionReader;
    private readonly IContextualRoleLifecycleReader _lifecycleReader;
    private readonly IContextualRoleInstructionSourceProbe _sourceProbe;

    /// <summary>Creates the workspace-bound read-only inspection policy.</summary>
    public ContextualRoleInspectionService(
        string workspaceId,
        IContextualRoleCatalogReader catalogReader,
        IContextualRoleRevisionReader revisionReader,
        IContextualRoleLifecycleReader lifecycleReader,
        IContextualRoleInstructionSourceProbe sourceProbe)
    {
        if (!ContextualRoleId.IsValid(workspaceId))
        {
            throw new ArgumentException("Workspace id must be a bounded lowercase ASCII identifier.", nameof(workspaceId));
        }

        ArgumentNullException.ThrowIfNull(catalogReader);
        ArgumentNullException.ThrowIfNull(revisionReader);
        ArgumentNullException.ThrowIfNull(lifecycleReader);
        ArgumentNullException.ThrowIfNull(sourceProbe);
        _workspaceId = workspaceId;
        _catalogReader = catalogReader;
        _revisionReader = revisionReader;
        _lifecycleReader = lifecycleReader;
        _sourceProbe = sourceProbe;
    }

    /// <summary>Reads one bounded role page and evaluates each entry's current fail-closed source posture.</summary>
    public async Task<ContextualRoleInspectionCatalogResult> ReadCatalogAsync(ContextualRoleCatalogReadRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValid(request))
        {
            return new ContextualRoleInspectionCatalogResult(ContextualRoleCatalogReadStatus.Invalid, [], null);
        }

        var catalog = await _catalogReader.ReadCatalogAsync(request, cancellationToken);
        if (catalog is null || catalog.Status != ContextualRoleCatalogReadStatus.Available)
        {
            var status = catalog?.Status switch
            {
                ContextualRoleCatalogReadStatus.Invalid => ContextualRoleCatalogReadStatus.Invalid,
                ContextualRoleCatalogReadStatus.Unavailable => ContextualRoleCatalogReadStatus.Unavailable,
                ContextualRoleCatalogReadStatus.Ambiguous => ContextualRoleCatalogReadStatus.Ambiguous,
                _ => ContextualRoleCatalogReadStatus.Ambiguous
            };
            return new ContextualRoleInspectionCatalogResult(status, [], null);
        }

        if (!IsValid(catalog, request))
        {
            return new ContextualRoleInspectionCatalogResult(ContextualRoleCatalogReadStatus.Ambiguous, [], null);
        }

        var entries = new List<ContextualRoleInspectionEntry>(catalog.Entries.Count);
        foreach (var entry in catalog.Entries)
        {
            var inspected = await InspectCurrentAsync(entry, cancellationToken);
            if (inspected.Entry is null)
            {
                var status = inspected.Status == ContextualRoleInspectionStatus.Unavailable
                    ? ContextualRoleCatalogReadStatus.Unavailable
                    : ContextualRoleCatalogReadStatus.Ambiguous;
                return new ContextualRoleInspectionCatalogResult(status, [], null);
            }

            entries.Add(inspected.Entry);
        }

        return new ContextualRoleInspectionCatalogResult(ContextualRoleCatalogReadStatus.Available, entries, catalog.NextCursor);
    }

    /// <summary>Validates one caller-observed exact role revision and its currently registered source.</summary>
    public async Task<ContextualRoleInspectionResult> InspectAsync(ContextualRoleInspectionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValid(request))
        {
            return new ContextualRoleInspectionResult(ContextualRoleInspectionStatus.Invalid, null);
        }

        var identity = new ContextualRoleRevisionIdentity(request.RoleId, request.Revision);
        var revisionRead = await _revisionReader.ReadAsync(new ContextualRoleRevisionReadRequest(identity), cancellationToken);
        if (revisionRead is null || revisionRead.Status != ContextualRoleRevisionReadStatus.Found || revisionRead.Revision is null)
        {
            return new ContextualRoleInspectionResult(Map(revisionRead?.Status ?? ContextualRoleRevisionReadStatus.Unknown), null);
        }

        if (revisionRead.Revision.Identity != identity || !ContextualRoleRevisionValidator.Validate(revisionRead.Revision).IsValid)
        {
            return new ContextualRoleInspectionResult(ContextualRoleInspectionStatus.Ambiguous, null);
        }

        if (!FixedTimeEquals(revisionRead.Revision.ContentHash, request.ContentHash))
        {
            return new ContextualRoleInspectionResult(ContextualRoleInspectionStatus.Stale, null);
        }

        var lifecycleRead = await _lifecycleReader.ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest(request.RoleId), cancellationToken);
        if (lifecycleRead is null || lifecycleRead.Status != ContextualRoleLifecycleReadStatus.Found || lifecycleRead.Snapshot is null)
        {
            return new ContextualRoleInspectionResult(MapLifecycle(lifecycleRead?.Status ?? ContextualRoleLifecycleReadStatus.Unknown), null);
        }

        if (lifecycleRead.Snapshot.CurrentIdentity != identity)
        {
            return new ContextualRoleInspectionResult(ContextualRoleInspectionStatus.Stale, null);
        }

        if (!IsValid(lifecycleRead.Snapshot, revisionRead.Revision) || !Matches(revisionRead.Disposition, lifecycleRead.Snapshot.State))
        {
            return new ContextualRoleInspectionResult(ContextualRoleInspectionStatus.Ambiguous, null);
        }

        var inspected = await InspectCurrentAsync(new ContextualRoleCatalogEntry(revisionRead.Revision, lifecycleRead.Snapshot), cancellationToken);
        return new ContextualRoleInspectionResult(inspected.Status, inspected.Entry);
    }

    private async Task<ContextualRoleInspectionResult> InspectCurrentAsync(ContextualRoleCatalogEntry entry, CancellationToken cancellationToken)
    {
        var applies = entry.Revision.WorkspaceApplicability.AppliesTo(_workspaceId);
        ContextualRoleInstructionSourceProbeStatus sourceStatus;
        if (!applies)
        {
            sourceStatus = ContextualRoleInstructionSourceProbeStatus.WorkspaceMismatch;
        }
        else if (entry.Lifecycle.State != ContextualRoleLifecycleState.Active || entry.Revision.Status != ContextualRoleStatus.Published)
        {
            sourceStatus = ContextualRoleInstructionSourceProbeStatus.Ineligible;
        }
        else
        {
            sourceStatus = (await _sourceProbe.ProbeAsync(entry.Revision.InstructionSource, cancellationToken))?.Status
                ?? ContextualRoleInstructionSourceProbeStatus.Ambiguous;
        }

        var inspected = new ContextualRoleInspectionEntry(
            entry.Revision,
            entry.Lifecycle,
            sourceStatus,
            applies,
            applies && entry.Lifecycle.State == ContextualRoleLifecycleState.Active && entry.Revision.Status == ContextualRoleStatus.Published && sourceStatus == ContextualRoleInstructionSourceProbeStatus.Ready,
            [],
            AreDependentsComplete: true,
            DependentsTruncated: false);

        var confirmation = await _lifecycleReader.ReadLifecycleAsync(new ContextualRoleLifecycleReadRequest(entry.Revision.Identity.RoleId), cancellationToken);
        if (confirmation is null || confirmation.Status != ContextualRoleLifecycleReadStatus.Found || confirmation.Snapshot is null)
        {
            return new ContextualRoleInspectionResult(MapLifecycle(confirmation?.Status ?? ContextualRoleLifecycleReadStatus.Unknown), null);
        }

        if (!IsValid(confirmation.Snapshot, entry.Revision))
        {
            return new ContextualRoleInspectionResult(ContextualRoleInspectionStatus.Ambiguous, null);
        }

        if (confirmation.Snapshot != entry.Lifecycle)
        {
            return new ContextualRoleInspectionResult(ContextualRoleInspectionStatus.Stale, null);
        }

        return new ContextualRoleInspectionResult(Map(inspected), inspected);
    }

    private static bool IsValid(ContextualRoleInspectionRequest? request)
        => request is not null
            && ContextualRoleId.IsValid(request.RoleId)
            && request.Revision > 0
            && request.ContentHash is { Length: ContextualRoleLimits.Sha256HexCharacters }
            && request.ContentHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsValid(ContextualRoleCatalogReadRequest? request)
        => request is not null
            && request.MaximumCount is >= 1 and <= ContextualRoleCatalogLimits.MaximumPageSize
            && (request.StartAfterRoleId is null || ContextualRoleId.IsValid(request.StartAfterRoleId));

    private static bool IsValid(ContextualRoleCatalogReadResult catalog, ContextualRoleCatalogReadRequest request)
    {
        if (catalog.Entries.Count > request.MaximumCount)
        {
            return false;
        }

        var previousRoleId = request.StartAfterRoleId;
        foreach (var entry in catalog.Entries)
        {
            if (entry is null
                || !ContextualRoleRevisionValidator.Validate(entry.Revision).IsValid
                || !IsValid(entry.Lifecycle, entry.Revision)
                || previousRoleId is not null && string.Compare(entry.Revision.Identity.RoleId, previousRoleId, StringComparison.Ordinal) <= 0)
            {
                return false;
            }

            previousRoleId = entry.Revision.Identity.RoleId;
        }

        return catalog.NextCursor is null
            || catalog.Entries.Count == request.MaximumCount
                && string.Equals(catalog.NextCursor, previousRoleId, StringComparison.Ordinal);
    }

    private static bool IsValid(ContextualRoleLifecycleSnapshot? lifecycle, ContextualRoleRevision revision)
        => lifecycle is not null
            && lifecycle.SchemaVersion == ContextualRoleLimits.SchemaVersion
            && string.Equals(lifecycle.RoleId, revision.Identity.RoleId, StringComparison.Ordinal)
            && lifecycle.CurrentIdentity == revision.Identity
            && lifecycle.State is ContextualRoleLifecycleState.Active or ContextualRoleLifecycleState.Disabled or ContextualRoleLifecycleState.Tombstoned
            && ContextualRoleId.IsValid(lifecycle.LastOperationId)
            && Enum.IsDefined(lifecycle.LastMutationKind)
            && lifecycle.LastMutationKind != ContextualRoleRevisionMutationKind.Unknown
            && lifecycle.UpdatedAtUtc != default
            && lifecycle.UpdatedAtUtc.Offset == TimeSpan.Zero;

    private static bool Matches(ContextualRoleRevisionDisposition disposition, ContextualRoleLifecycleState lifecycle)
        => (disposition, lifecycle) switch
        {
            (ContextualRoleRevisionDisposition.Active, ContextualRoleLifecycleState.Active) => true,
            (ContextualRoleRevisionDisposition.Disabled, ContextualRoleLifecycleState.Disabled) => true,
            (ContextualRoleRevisionDisposition.Tombstoned, ContextualRoleLifecycleState.Tombstoned) => true,
            _ => false
        };

    private static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var actualBytes = Encoding.ASCII.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static ContextualRoleInspectionStatus Map(ContextualRoleRevisionReadStatus status) => status switch
    {
        ContextualRoleRevisionReadStatus.NotFound => ContextualRoleInspectionStatus.NotFound,
        ContextualRoleRevisionReadStatus.Invalid => ContextualRoleInspectionStatus.Invalid,
        ContextualRoleRevisionReadStatus.Unavailable => ContextualRoleInspectionStatus.Unavailable,
        _ => ContextualRoleInspectionStatus.Ambiguous
    };

    private static ContextualRoleInspectionStatus MapLifecycle(ContextualRoleLifecycleReadStatus status) => status switch
    {
        ContextualRoleLifecycleReadStatus.NotFound => ContextualRoleInspectionStatus.Stale,
        ContextualRoleLifecycleReadStatus.Invalid => ContextualRoleInspectionStatus.Invalid,
        ContextualRoleLifecycleReadStatus.Unavailable => ContextualRoleInspectionStatus.Unavailable,
        _ => ContextualRoleInspectionStatus.Ambiguous
    };

    private static ContextualRoleInspectionStatus Map(ContextualRoleInspectionEntry entry)
    {
        if (entry.IsAdmissionReady)
        {
            return ContextualRoleInspectionStatus.Ready;
        }

        return entry.SourceStatus switch
        {
            ContextualRoleInstructionSourceProbeStatus.WorkspaceMismatch => ContextualRoleInspectionStatus.WorkspaceMismatch,
            ContextualRoleInstructionSourceProbeStatus.Ineligible => ContextualRoleInspectionStatus.Ineligible,
            ContextualRoleInstructionSourceProbeStatus.Missing => ContextualRoleInspectionStatus.SourceMissing,
            ContextualRoleInstructionSourceProbeStatus.Unsupported => ContextualRoleInspectionStatus.SourceUnsupported,
            ContextualRoleInstructionSourceProbeStatus.Oversized => ContextualRoleInspectionStatus.SourceOversized,
            ContextualRoleInstructionSourceProbeStatus.Substituted => ContextualRoleInspectionStatus.SourceSubstituted,
            ContextualRoleInstructionSourceProbeStatus.Unavailable => ContextualRoleInspectionStatus.Unavailable,
            _ => ContextualRoleInspectionStatus.Ambiguous
        };
    }
}
