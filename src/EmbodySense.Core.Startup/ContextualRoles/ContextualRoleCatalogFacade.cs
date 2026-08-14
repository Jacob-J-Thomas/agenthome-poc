using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.ContextualRoles;
using EmbodySense.Core.Startup.ContextualRoles.Models;
using System.Text.Json;

namespace EmbodySense.Core.Startup.ContextualRoles;

/// <summary>Exposes safe read-only contextual-role catalog and exact registered-source inspection.</summary>
/// <remarks>
/// The facade never returns instruction contents, filesystem paths, workspace scopes, native diagnostics, or trust
/// evidence. Catalog presence and source readiness remain non-granting posture for later separate admission policy.
/// </remarks>
public sealed class ContextualRoleCatalogFacade : IContextualRoleCatalogFacade
{
    private readonly WorkspacePaths _paths;
    private readonly string _workspaceId;

    /// <summary>Creates a facade bound to one exact workspace root and its server-derived workspace identity.</summary>
    /// <param name="workingDirectory">The workspace root whose normalized path determines the exact workspace identity.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="workingDirectory"/> is empty or whitespace.</exception>
    public ContextualRoleCatalogFacade(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        _paths = new WorkspacePaths(workingDirectory);
        var workspaceId = CapabilityWorkspaceScopeId.Create(_paths.RootPath);
        if (!ContextualRoleWorkspaceId.IsValid(workspaceId))
        {
            throw new InvalidOperationException("The server-derived workspace identity is outside the contextual-role contract.");
        }

        _workspaceId = workspaceId;
    }

    /// <inheritdoc />
    public async Task<ContextualRoleCatalogResponse> ReadCatalogAsync(string? startAfterRoleId, int maximumCount, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var store = new ContextualRoleRevisionStore(_paths, _workspaceId);
            var service = CreateService(store);
            var result = await service.ReadCatalogAsync(new ContextualRoleCatalogReadRequest(startAfterRoleId, maximumCount), cancellationToken);
            var available = result.Status == ContextualRoleCatalogReadStatus.Available;
            return new ContextualRoleCatalogResponse(
                Token(result.Status),
                available ? result.Entries.Select(Map).ToArray() : [],
                available ? result.NextCursor : null,
                available ? null : Error(result.Status));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return UnavailableCatalog();
        }
        catch (InvalidOperationException)
        {
            return AmbiguousCatalog();
        }
    }

    /// <inheritdoc />
    public async Task<ContextualRoleResponse> InspectAsync(ContextualRoleInspectionInput input, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input is null)
        {
            return InvalidInspection();
        }

        try
        {
            using var store = new ContextualRoleRevisionStore(_paths, _workspaceId);
            var service = CreateService(store);
            var result = await service.InspectAsync(new ContextualRoleInspectionRequest(input.RoleId, input.Revision, input.ContentHash), cancellationToken);
            return new ContextualRoleResponse(Token(result.Status), result.Entry is null ? null : Map(result.Entry), result.Status == ContextualRoleInspectionStatus.Ready ? null : Error(result.Status));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ContextualRoleResponse("unavailable", null, new ContextualRoleError("contextual_role_unavailable", "Contextual-role evidence is unavailable."));
        }
        catch (InvalidOperationException)
        {
            return new ContextualRoleResponse("ambiguous", null, new ContextualRoleError("contextual_role_ambiguous", "Exact contextual-role evidence could not be proved."));
        }
    }

    private ContextualRoleInspectionService CreateService(ContextualRoleRevisionStore store)
        => new(_workspaceId, store, store, store, new WorkspaceContextualRoleInstructionSourceProbe(_paths));

    private static ContextualRoleSnapshot Map(ContextualRoleInspectionEntry entry)
    {
        var revision = entry.Revision;
        return new ContextualRoleSnapshot(
            revision.Identity.RoleId,
            revision.Identity.Revision,
            revision.ContentHash,
            revision.DisplayName,
            revision.Purpose,
            Token(revision.Status),
            Token(entry.Lifecycle.State),
            revision.Provenance.AuthorId,
            revision.Provenance.CreatedAtUtc,
            revision.Provenance.RecordedAtUtc,
            entry.Lifecycle.UpdatedAtUtc,
            Token(revision.InstructionSource.Kind),
            revision.InstructionSource.ReferenceId,
            Token(entry.SourceStatus),
            entry.IsApplicableToWorkspace,
            entry.IsAdmissionReady,
            revision.PolicyMaxima.CapabilityIds.Order(StringComparer.Ordinal).ToArray(),
            entry.Dependents.Select(dependent => new ContextualRoleDependentSnapshot(dependent.Kind, dependent.Identity, dependent.Revision)).ToArray(),
            entry.AreDependentsComplete,
            entry.DependentsTruncated);
    }

    private static ContextualRoleCatalogResponse UnavailableCatalog()
        => new("unavailable", [], null, new ContextualRoleError("contextual_role_catalog_unavailable", "Contextual-role catalog evidence is unavailable."));

    private static ContextualRoleCatalogResponse AmbiguousCatalog()
        => new("ambiguous", [], null, new ContextualRoleError("contextual_role_catalog_ambiguous", "Exact contextual-role catalog evidence could not be proved."));

    private static ContextualRoleResponse InvalidInspection()
        => new("invalid", null, new ContextualRoleError("invalid_contextual_role_inspection", "The contextual-role inspection request is outside the bounded contract."));

    private static ContextualRoleError Error(ContextualRoleCatalogReadStatus status) => status switch
    {
        ContextualRoleCatalogReadStatus.Invalid => new ContextualRoleError("invalid_contextual_role_catalog_request", "The contextual-role catalog request is outside the bounded contract."),
        ContextualRoleCatalogReadStatus.Ambiguous => new ContextualRoleError("contextual_role_catalog_ambiguous", "Exact contextual-role catalog evidence could not be proved."),
        _ => new ContextualRoleError("contextual_role_catalog_unavailable", "Contextual-role catalog evidence is unavailable.")
    };

    private static ContextualRoleError Error(ContextualRoleInspectionStatus status) => status switch
    {
        ContextualRoleInspectionStatus.Invalid => new ContextualRoleError("invalid_contextual_role_inspection", "The contextual-role inspection request is outside the bounded contract."),
        ContextualRoleInspectionStatus.NotFound => new ContextualRoleError("contextual_role_not_found", "The exact contextual-role revision was not found."),
        ContextualRoleInspectionStatus.Stale => new ContextualRoleError("contextual_role_stale", "The supplied contextual-role revision is no longer exact and current."),
        ContextualRoleInspectionStatus.Ineligible => new ContextualRoleError("contextual_role_ineligible", "The exact contextual-role revision is not currently eligible."),
        ContextualRoleInspectionStatus.WorkspaceMismatch => new ContextualRoleError("contextual_role_workspace_mismatch", "The exact contextual role does not apply to this workspace."),
        ContextualRoleInspectionStatus.SourceMissing => new ContextualRoleError("contextual_role_source_missing", "The registered contextual-role instruction source is missing."),
        ContextualRoleInspectionStatus.SourceUnsupported => new ContextualRoleError("contextual_role_source_unsupported", "The contextual-role instruction source is not server-registered."),
        ContextualRoleInspectionStatus.SourceOversized => new ContextualRoleError("contextual_role_source_oversized", "The contextual-role instruction source exceeds the server-owned bound."),
        ContextualRoleInspectionStatus.SourceSubstituted => new ContextualRoleError("contextual_role_source_substituted", "The contextual-role instruction source failed physical identity validation."),
        ContextualRoleInspectionStatus.Unavailable => new ContextualRoleError("contextual_role_unavailable", "Contextual-role evidence is unavailable."),
        _ => new ContextualRoleError("contextual_role_ambiguous", "Exact contextual-role evidence could not be proved.")
    };

    private static string Token<T>(T value) where T : struct, Enum => JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString());
}
