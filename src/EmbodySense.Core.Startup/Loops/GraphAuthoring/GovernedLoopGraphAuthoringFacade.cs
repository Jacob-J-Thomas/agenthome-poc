using System.Text.Json;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Startup.ContextualRoles;
using EmbodySense.Core.Startup.Inference.Profiles;
using EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;

namespace EmbodySense.Core.Startup.Loops.GraphAuthoring;

/// <summary>Exposes canonical graph catalog, immutable revision reads, and authenticated lifecycle mutations through Core.Startup.</summary>
/// <remarks>
/// Actor, surface, workspace, current role authority, validation, persistence, and lifecycle policy are retained by
/// composition. Interface adapters may submit graph content and optimistic lifecycle evidence but cannot submit trusted
/// actor or authority evidence. Every mutation re-proves the exact active owning-role revision and binds authorization to
/// the canonical request hash before durable work.
/// </remarks>
public sealed class GovernedLoopGraphAuthoringFacade
{
    private readonly AuthorityActorId _actorId;
    private readonly IGovernedLoopAuthoritySnapshotProvider _authority;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly IGovernedLoopNodeCatalog _catalog;
    private readonly IContextualRoleCatalogFacade _roles;
    private readonly IModelProfileCatalogFacade _modelProfiles;
    private readonly IGovernedLoopGraphRevisionStore _store;
    private readonly string _surfaceId;
    private readonly string _workspaceId;

    internal GovernedLoopGraphAuthoringFacade(
        string workspaceId,
        string actorId,
        string surfaceId,
        IGovernedLoopGraphRevisionStore store,
        IGovernedLoopNodeCatalog catalog,
        IGovernedLoopAuthoritySnapshotProvider authority,
        ICapabilityAuthorityTransaction authorityTransaction,
        IContextualRoleCatalogFacade roles,
        IModelProfileCatalogFacade modelProfiles)
    {
        if (!AuthorityActorId.TryParse(actorId, out var parsedActor, out _))
        {
            throw new ArgumentException("The configured graph-authoring actor is invalid.", nameof(actorId));
        }

        _workspaceId = workspaceId ?? throw new ArgumentNullException(nameof(workspaceId));
        _surfaceId = surfaceId ?? throw new ArgumentNullException(nameof(surfaceId));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
        _modelProfiles = modelProfiles ?? throw new ArgumentNullException(nameof(modelProfiles));
        _actorId = parsedActor!;
    }

    /// <summary>Reads the exact executable descriptor catalog and safe active-role choices.</summary>
    /// <param name="cancellationToken">Cancels catalog and role reads.</param>
    /// <returns>A bounded catalog response that contains no instructions, filesystem paths, payloads, or secrets.</returns>
    public async Task<GovernedLoopGraphCatalogResponse> ReadCatalogAsync(CancellationToken cancellationToken = default)
    {
        GovernedLoopNodeCatalogSnapshot? catalog;
        try
        {
            catalog = await _catalog.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            catalog = null;
        }

        var roles = await ContextualRoleCatalogAggregator.ReadAsync(_roles, cancellationToken).ConfigureAwait(false);
        var modelProfiles = await _modelProfiles.ReadAsync(null, 50, cancellationToken).ConfigureAwait(false);
        var available = catalog is { IsAvailable: true }
            && catalog.Descriptors is not null
            && string.Equals(roles.Status, "available", StringComparison.Ordinal)
            && string.Equals(modelProfiles.Status, "available", StringComparison.Ordinal);
        var descriptors = available
            ? catalog!.Descriptors!.Select(Map).ToArray()
            : Array.Empty<GovernedLoopGraphCatalogNodeSnapshot>();
        return new GovernedLoopGraphCatalogResponse(
            available ? "available" : "unavailable",
            catalog?.SourceEvidenceId ?? string.Empty,
            Array.AsReadOnly(descriptors),
            roles,
            modelProfiles);
    }

    /// <summary>Reads one exact graph aggregate without selecting a revision for the caller.</summary>
    /// <param name="graphId">The canonical custom graph identity.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The exact immutable history and optimistic lifecycle head when safely available.</returns>
    public async Task<GovernedLoopGraphReadResponse> ReadAsync(string graphId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(graphId))
        {
            return Read("invalid", 0, null);
        }

        GovernedLoopGraphRevisionReadResult result;
        try
        {
            result = await _store.ReadGraphAsync(graphId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Read("unavailable", 0, null);
        }

        return Read(Token(result.Status), result.StoreGeneration, result.Snapshot);
    }

    /// <summary>Executes one exact graph mutation under current role-bound authenticated surface authority.</summary>
    /// <param name="input">The graph content and exact optimistic lifecycle evidence.</param>
    /// <param name="cancellationToken">Cancels before durable integrity boundaries.</param>
    /// <returns>The durable mutation result, structured element errors, and a refreshed aggregate when available.</returns>
    public async Task<GovernedLoopGraphMutationResponse> MutateAsync(
        GovernedLoopGraphMutationInput? input,
        CancellationToken cancellationToken = default)
    {
        if (input is null || !Enum.IsDefined(input.Kind))
        {
            return Invalid(input?.OperationId, "graph-mutation-kind-invalid", "graph", null, "kind");
        }

        GovernedLoopGraphDefinition? normalizedGraph = null;
        if (input.Kind is GovernedLoopGraphMutationKind.CreateDraft or GovernedLoopGraphMutationKind.ReplaceDraft)
        {
            var normalized = GovernedLoopGraphNormalizer.Normalize(input.GraphCandidate);
            if (!normalized.IsValid)
            {
                return Invalid(input.OperationId, normalized.Errors);
            }
            normalizedGraph = normalized.Graph;
            if (!string.Equals(normalizedGraph!.GraphId, input.GraphId, StringComparison.Ordinal))
            {
                return Invalid(input.OperationId, "graph-id-mismatch", "graph", input.GraphId, "graphId");
            }
        }
        else if (input.GraphCandidate is not null)
        {
            return Invalid(input.OperationId, "graph-candidate-unexpected", "graph", input.GraphId, "graphCandidate");
        }

        var owningRole = input.Kind == GovernedLoopGraphMutationKind.CreateDraft
            ? normalizedGraph?.OwningRole
            : await ResolveTargetOwningRoleAsync(input, cancellationToken).ConfigureAwait(false);
        if (owningRole is null)
        {
            return new GovernedLoopGraphMutationResponse(
                "unavailable",
                input.OperationId ?? string.Empty,
                string.Empty,
                null,
                "unknown",
                Array.AsReadOnly(Array.Empty<GovernedLoopElementErrorSnapshot>()),
                await ReadAsync(input.GraphId, cancellationToken).ConfigureAwait(false));
        }

        var lifecycle = CreateLifecycleRequest(input, normalizedGraph);
        var authorizer = new CurrentRoleBoundRevisionActorAuthorizer(
            _workspaceId,
            _actorId,
            _surfaceId,
            owningRole,
            _authority);
        var service = new GovernedLoopGraphAuthoringService(
            _store,
            new GovernedLoopGraphValidationService(_catalog, _authority),
            authorizer,
            _authorityTransaction);
        var result = await service.MutateAsync(
            new GovernedLoopGraphAuthoringRequest(
                GovernedLoopGraphDefinition.CurrentSchemaVersion,
                lifecycle,
                input.GraphCandidate),
            cancellationToken).ConfigureAwait(false);
        var current = await ReadAsync(input.GraphId, cancellationToken).ConfigureAwait(false);
        return Map(result, current);
    }

    private async Task<ContextualRoleRevisionPin?> ResolveTargetOwningRoleAsync(
        GovernedLoopGraphMutationInput input,
        CancellationToken cancellationToken)
    {
        var target = SelectTargetRevision(input);
        if (target is null)
        {
            return null;
        }

        var artifact = await _store.ReadArtifactAsync(target, cancellationToken).ConfigureAwait(false);
        return artifact.Status == GovernedLoopRevisionStoreReadStatus.Ready
            && artifact.Artifact is { } found
            && Equals(found.Graph.RevisionReference, target)
                ? found.Graph.OwningRole
                : null;
    }

    /// <summary>Selects the exact immutable artifact whose owning-role authority governs the requested lifecycle effect.</summary>
    /// <remarks>Disable and archive target the publication even when a distinct successor draft is retained.</remarks>
    public static GovernedLoopRevisionReference? SelectTargetRevision(GovernedLoopGraphMutationInput? input)
    {
        if (input is null)
        {
            return null;
        }

        return input.Kind switch
        {
            GovernedLoopGraphMutationKind.ReplaceDraft
                => input.ExpectedDraftRevision ?? input.ExpectedPublishedRevision?.Revision,
            GovernedLoopGraphMutationKind.Publish => input.ExpectedDraftRevision,
            GovernedLoopGraphMutationKind.Disable or GovernedLoopGraphMutationKind.Archive
                => input.ExpectedPublishedRevision?.Revision,
            _ => null,
        };
    }

    private GovernedLoopRevisionLifecycleRequest CreateLifecycleRequest(
        GovernedLoopGraphMutationInput input,
        GovernedLoopGraphDefinition? graph)
    {
        var kind = input.Kind switch
        {
            GovernedLoopGraphMutationKind.CreateDraft => GovernedLoopRevisionOperationKind.CreateDraft,
            GovernedLoopGraphMutationKind.ReplaceDraft => GovernedLoopRevisionOperationKind.ReplaceDraft,
            GovernedLoopGraphMutationKind.Publish => GovernedLoopRevisionOperationKind.Publish,
            GovernedLoopGraphMutationKind.Disable => GovernedLoopRevisionOperationKind.Disable,
            _ => GovernedLoopRevisionOperationKind.Archive,
        };
        var target = kind switch
        {
            GovernedLoopRevisionOperationKind.Publish => input.ExpectedDraftRevision,
            GovernedLoopRevisionOperationKind.Disable or GovernedLoopRevisionOperationKind.Archive => input.ExpectedPublishedRevision?.Revision,
            GovernedLoopRevisionOperationKind.ReplaceDraft => input.ExpectedDraftRevision ?? input.ExpectedPublishedRevision?.Revision,
            _ => null,
        };
        return new GovernedLoopRevisionLifecycleRequest(
            1,
            input.OperationId,
            kind,
            input.GraphId,
            _actorId,
            input.ExpectedLifecycleStatus,
            input.ExpectedLifecycleVersion,
            input.ExpectedDraftRevision,
            input.ExpectedPublishedRevision,
            graph?.RevisionReference,
            target,
            null);
    }

    private static GovernedLoopGraphMutationResponse Map(
        GovernedLoopGraphAuthoringResult result,
        GovernedLoopGraphReadResponse current)
    {
        var graphErrors = result.GraphValidationErrors.Select(error => new GovernedLoopElementErrorSnapshot(
            error.Code,
            Token(error.Element.Kind),
            error.Element.Id,
            error.Element.Path,
            error.Message));
        var lifecycleErrors = result.LifecycleValidationErrors.Select(error => new GovernedLoopElementErrorSnapshot(
            Token(error.Code),
            "lifecycle",
            null,
            error.Path,
            null));
        return new GovernedLoopGraphMutationResponse(
            Token(result.Status),
            result.OperationId,
            result.AuthoringRequestHash,
            result.GraphValidationEvidenceHash,
            Token(result.ChangeKind),
            Array.AsReadOnly(graphErrors.Concat(lifecycleErrors).ToArray()),
            current);
    }

    private static GovernedLoopGraphMutationResponse Invalid(
        string? operationId,
        IReadOnlyList<GovernedLoopGraphValidationError> errors)
        => new(
            "invalid",
            operationId ?? string.Empty,
            string.Empty,
            null,
            "unknown",
            Array.AsReadOnly(errors.Select(error => new GovernedLoopElementErrorSnapshot(
                error.Code,
                Token(error.Element.Kind),
                error.Element.Id,
                error.Element.Path,
                error.Message)).ToArray()),
            null);

    private static GovernedLoopGraphMutationResponse Invalid(
        string? operationId,
        string code,
        string kind,
        string? id,
        string path)
        => new(
            "invalid",
            operationId ?? string.Empty,
            string.Empty,
            null,
            "unknown",
            Array.AsReadOnly(new[] { new GovernedLoopElementErrorSnapshot(code, kind, id, path, null) }),
            null);

    private static GovernedLoopGraphReadResponse Read(
        string status,
        long generation,
        GovernedLoopGraphRevisionSnapshot? snapshot)
        => new(
            status,
            generation,
            snapshot?.Lifecycle.Head,
            Array.AsReadOnly((snapshot?.Artifacts ?? []).ToArray()));

    private static GovernedLoopGraphCatalogNodeSnapshot Map(GovernedLoopNodeCatalogDescriptor item)
        => new(
            item.Descriptor,
            item.IsAdvertised,
            item.IsExecutable,
            item.IsLegalEntry,
            item.IsLegalTerminal,
            Array.AsReadOnly(item.AllowedControlOutcomes.Select(Token).ToArray()),
            Array.AsReadOnly(item.RequiredControlOutcomes.Select(Token).ToArray()),
            Token(item.JoinPolicy),
            item.MinimumIncomingControlEdges,
            item.AllowsCycle,
            item.CycleIterationBudgetParameterId,
            item.CycleTimeBudgetMillisecondsParameterId,
            Array.AsReadOnly(item.Ports.Select(port => new GovernedLoopGraphCatalogPortSnapshot(
                port.Id,
                Token(port.Direction),
                Token(port.BindingKind),
                Array.AsReadOnly(port.AllowedValueKinds.Kinds.Select(Token).ToArray()),
                port.Required)).ToArray()),
            Array.AsReadOnly(item.Parameters.Select(parameter => new GovernedLoopGraphCatalogParameterSnapshot(
                parameter.Id,
                Token(parameter.ValueKind),
                parameter.Required,
                parameter.MinimumCharacters,
                parameter.MaximumCharacters,
                parameter.MinimumInteger,
                parameter.MaximumInteger,
                Array.AsReadOnly(parameter.AllowedValues.ToArray()))).ToArray()),
            Array.AsReadOnly(item.RequiredCapabilityIds.ToArray()));

    private static string Token<T>(T value) where T : struct, Enum
        => JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString());
}
