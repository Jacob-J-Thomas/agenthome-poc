using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Governance.Authority;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Governance.Authority.Models;
using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.EffectAuthorityUsage;
using EmbodySense.Core.Application.Loops.EffectAuthorityUsage.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Startup.Loops.InvocationPreparation.Models;

namespace EmbodySense.Core.Startup.Loops.InvocationPreparation;

/// <summary>Prepares exact current-publication governed invocation authority without accepting browser authority assertions.</summary>
/// <remarks>
/// The facade is intentionally scoped to a local authenticated surface instance. It derives its actor and workspace when it is
/// composed, rereads every selected publication and dependency, and exposes only resolved exact grant references. A missing
/// grant produces a non-persisted preview; confirmation recreates the same stable intent from the selected object and hash so a
/// crash between profile and grant lifecycle operations can resume without selecting ambient authority.
/// </remarks>
public sealed class GovernedLoopInvocationPreparationFacade
{
    private const string PurposeText = "governed-loop-invocation";
    private readonly string _workspaceId;
    private readonly AuthorityActorId _actor;
    private readonly bool _isVisibleWebSurface;
    private readonly IGovernedLoopRevisionLifecycleStore _revisionStore;
    private readonly IGovernedLoopGrantBindingSource _bindingSource;
    private readonly IAuthorityGrantRoleSource _roleSource;
    private readonly IAuthorityGrantCatalogSource _grantCatalog;
    private readonly IAuthorityGrantResolver _grantResolver;
    private readonly IGovernedLoopEffectAuthorityUsageReader _usageReader;
    private readonly IAuthorityProfileStore _profileStore;
    private readonly IAuthorityGrantStore _grantStore;
    private readonly ICapabilityAdmissionService _capabilityAdmission;
    private readonly IModelProfileMetadataSource _modelMetadataSource;
    private readonly IModelProfileAdapterRegistry _modelAdapterRegistry;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a server-owned preparation facade for one exact local Web runtime scope.</summary>
    /// <param name="workspaceId">The canonical server-derived workspace scope.</param>
    /// <param name="actor">The canonical server-derived authenticated surface actor.</param>
    /// <param name="isVisibleWebSurface">Whether this composed runtime is the server-owned visible Web invocation surface.</param>
    /// <param name="revisionStore">The current governed revision lifecycle store.</param>
    /// <param name="bindingSource">The exact current publication binding source.</param>
    /// <param name="roleSource">The exact current role source.</param>
    /// <param name="grantCatalog">The bounded current grant catalog source.</param>
    /// <param name="grantResolver">The exact current grant resolver.</param>
    /// <param name="usageReader">The canonical first-bound-run completion evidence reader.</param>
    /// <param name="profileStore">The authority-profile lifecycle store.</param>
    /// <param name="grantStore">The authority-grant lifecycle store.</param>
    /// <param name="capabilityAdmission">The current implemented-capability admission service.</param>
    /// <param name="modelMetadataSource">The server-owned exact model metadata source.</param>
    /// <param name="modelAdapterRegistry">The composed server-owned exact model adapter posture registry.</param>
    /// <param name="authorityTransaction">The shared workspace authority fence.</param>
    /// <param name="timeProvider">The trusted server clock.</param>
    /// <exception cref="ArgumentException">Thrown when the composed actor or workspace is not canonical.</exception>
    /// <exception cref="ArgumentNullException">Thrown when a required dependency is absent.</exception>
    public GovernedLoopInvocationPreparationFacade(
        string workspaceId,
        string actor,
        bool isVisibleWebSurface,
        IGovernedLoopRevisionLifecycleStore revisionStore,
        IGovernedLoopGrantBindingSource bindingSource,
        IAuthorityGrantRoleSource roleSource,
        IAuthorityGrantCatalogSource grantCatalog,
        IAuthorityGrantResolver grantResolver,
        IGovernedLoopEffectAuthorityUsageReader usageReader,
        IAuthorityProfileStore profileStore,
        IAuthorityGrantStore grantStore,
        ICapabilityAdmissionService capabilityAdmission,
        IModelProfileMetadataSource modelMetadataSource,
        IModelProfileAdapterRegistry modelAdapterRegistry,
        ICapabilityAuthorityTransaction authorityTransaction,
        TimeProvider? timeProvider = null)
    {
        if (!ContextualRoleWorkspaceId.IsValid(workspaceId))
        {
            throw new ArgumentException("The server-derived workspace id must be canonical.", nameof(workspaceId));
        }

        if (!AuthorityActorId.TryParse(actor, out var parsedActor, out _))
        {
            throw new ArgumentException("The server-derived actor must be canonical.", nameof(actor));
        }

        _workspaceId = workspaceId;
        _actor = parsedActor!;
        _isVisibleWebSurface = isVisibleWebSurface;
        _revisionStore = revisionStore ?? throw new ArgumentNullException(nameof(revisionStore));
        _bindingSource = bindingSource ?? throw new ArgumentNullException(nameof(bindingSource));
        _roleSource = roleSource ?? throw new ArgumentNullException(nameof(roleSource));
        _grantCatalog = grantCatalog ?? throw new ArgumentNullException(nameof(grantCatalog));
        _grantResolver = grantResolver ?? throw new ArgumentNullException(nameof(grantResolver));
        _usageReader = usageReader ?? throw new ArgumentNullException(nameof(usageReader));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _grantStore = grantStore ?? throw new ArgumentNullException(nameof(grantStore));
        _capabilityAdmission = capabilityAdmission ?? throw new ArgumentNullException(nameof(capabilityAdmission));
        _modelMetadataSource = modelMetadataSource ?? throw new ArgumentNullException(nameof(modelMetadataSource));
        _modelAdapterRegistry = modelAdapterRegistry ?? throw new ArgumentNullException(nameof(modelAdapterRegistry));
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Returns only server-authorized current exact grant choices or one confirmation preview.</summary>
    /// <param name="request">The selected graph and revision identifiers; they do not assert a publication or authority.</param>
    /// <param name="cancellationToken">The token used to cancel before a durable operation is begun.</param>
    /// <returns>A safe current-publication projection with no ambient grant catalog.</returns>
    public Task<GovernedLoopInvocationPreparationResponse> PrepareAsync(
        GovernedLoopInvocationPreparationRequest? request,
        CancellationToken cancellationToken = default)
        => _authorityTransaction.ExecuteAsync(token => PrepareUnderFenceAsync(request, token), cancellationToken);

    /// <summary>Confirms one server-derived preview and idempotently creates or resumes its exact profile then grant lifecycle.</summary>
    /// <param name="confirmation">The selected graph revision, expected preview digest, and durable operation identity.</param>
    /// <param name="cancellationToken">The token used until a durable profile or grant boundary is reached.</param>
    /// <returns>The exact confirmed grant reference or a fail-closed result.</returns>
    public Task<GovernedLoopInvocationAuthorityConfirmationResult> ConfirmAsync(
        GovernedLoopInvocationAuthorityConfirmation? confirmation,
        CancellationToken cancellationToken = default)
        => _authorityTransaction.ExecuteAsync(token => ConfirmUnderFenceAsync(confirmation, token), cancellationToken);

    private async Task<GovernedLoopInvocationPreparationResponse> PrepareUnderFenceAsync(
        GovernedLoopInvocationPreparationRequest? request,
        CancellationToken cancellationToken)
    {
        var asOfUtc = UtcNow();
        if (!_isVisibleWebSurface)
        {
            return Preparation(GovernedLoopInvocationPreparationStatus.Unavailable, null, [], null, asOfUtc, "Visible governed invocation preparation is available only to the authenticated Web surface.");
        }

        var terms = await ReadTermsAsync(request?.GraphId, request?.RevisionId, asOfUtc, cancellationToken).ConfigureAwait(false);
        if (terms.Status != GovernedLoopInvocationPreparationStatus.Ready)
        {
            return Preparation(terms.Status, terms.Publication, [], null, asOfUtc, terms.Detail);
        }

        var choices = await ReadEligibleGrantChoicesAsync(terms, cancellationToken).ConfigureAwait(false);
        if (choices is null)
        {
            return Preparation(GovernedLoopInvocationPreparationStatus.Unavailable, terms.Publication, [], null, asOfUtc, "Current exact authority-grant state is unavailable or ambiguous.");
        }

        if (choices.Count > 0)
        {
            return Preparation(GovernedLoopInvocationPreparationStatus.Ready, terms.Publication, choices.OrderBy(value => value.Grant.GrantId.Value, StringComparer.Ordinal).ToArray(), null, asOfUtc, "Current exact authority grants are available.");
        }

        var preview = new GovernedLoopInvocationAuthorityPreview(terms.SemanticHash, terms.Publication!, asOfUtc, null);
        return Preparation(GovernedLoopInvocationPreparationStatus.ConfirmationRequired, terms.Publication, [], preview, asOfUtc, "Explicit confirmation is required before the server creates the exact least-authority profile and grant.");
    }

    private async Task<GovernedLoopInvocationAuthorityConfirmationResult> ConfirmUnderFenceAsync(
        GovernedLoopInvocationAuthorityConfirmation? confirmation,
        CancellationToken cancellationToken)
    {
        var asOfUtc = UtcNow();
        if (!_isVisibleWebSurface)
        {
            return Confirmation(GovernedLoopInvocationAuthorityConfirmationStatus.Unavailable, null, asOfUtc, "Visible governed invocation confirmation is available only to the authenticated Web surface.");
        }

        if (!IsConfirmationValid(confirmation))
        {
            return Confirmation(GovernedLoopInvocationAuthorityConfirmationStatus.Invalid, null, asOfUtc, "The confirmation selector, preview hash, or operation identity is invalid.");
        }

        var terms = await ReadTermsAsync(confirmation!.GraphId, confirmation.RevisionId, asOfUtc, cancellationToken).ConfigureAwait(false);
        if (terms.Status != GovernedLoopInvocationPreparationStatus.Ready)
        {
            return Confirmation(MapTermsStatus(terms.Status), null, asOfUtc, terms.Detail);
        }

        if (!FixedEquals(confirmation.ExpectedPreviewHash, terms.SemanticHash))
        {
            return Confirmation(GovernedLoopInvocationAuthorityConfirmationStatus.Stale, null, asOfUtc, "The current exact publication, role, model, capability, workspace, actor, or lifecycle evidence no longer matches the preview.");
        }

        var ids = DeriveIds(confirmation.OperationId, terms);
        var eligibleGrants = await ReadEligibleGrantChoicesAsync(terms, cancellationToken).ConfigureAwait(false);
        if (eligibleGrants is null)
        {
            return Confirmation(GovernedLoopInvocationAuthorityConfirmationStatus.Unavailable, null, asOfUtc, "Current exact authority-grant state is unavailable or ambiguous.");
        }

        if (eligibleGrants.Any(choice => !choice.Grant.GrantId.Equals(ids.GrantId)))
        {
            return Confirmation(GovernedLoopInvocationAuthorityConfirmationStatus.Stale, null, asOfUtc, "A current exact authority grant now exists; obtain a fresh server preparation before selecting it.");
        }

        AuthorityProfileReadResult profileRead;
        try
        {
            profileRead = await _profileStore.ReadAsync(ids.ProfileId.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Confirmation(GovernedLoopInvocationAuthorityConfirmationStatus.Unavailable, null, asOfUtc, "The exact authority profile could not be read safely.");
        }

        if (profileRead is null || !Enum.IsDefined(profileRead.Status))
        {
            return Confirmation(GovernedLoopInvocationAuthorityConfirmationStatus.Unavailable, null, asOfUtc, "The exact authority profile could not be read safely.");
        }

        AuthorityProfileRecord profileRecord;
        if (profileRead.Status == AuthorityProfileReadStatus.Available)
        {
            var existing = profileRead.Record;
            if (existing?.CurrentProfile is null)
            {
                return Confirmation(GovernedLoopInvocationAuthorityConfirmationStatus.Unavailable, null, asOfUtc, "The exact authority profile could not be proved safely.");
            }

            var expected = CreateProfile(ids.ProfileId, terms.Ceiling!, existing.CurrentProfile.IssuedAtUtc);
            if (!MatchesProfile(existing, expected, ids.ProfileOperationId))
            {
                return Confirmation(GovernedLoopInvocationAuthorityConfirmationStatus.Conflict, null, asOfUtc, "The durable operation identity is already bound to a different profile lifecycle intent.");
            }

            profileRecord = existing;
        }
        else if (profileRead.Status == AuthorityProfileReadStatus.NotFound)
        {
            var profile = CreateProfile(ids.ProfileId, terms.Ceiling!, asOfUtc);
            var profileMutation = new AuthorityProfileMutation(
                AuthorityProfileMutationKind.Create,
                ids.ProfileOperationId,
                0,
                profile,
                null,
                null,
                _actor,
                Purpose());
            AuthorityProfileMutationResult profileResult;
            try
            {
                profileResult = await _profileStore.MutateAsync(profileMutation, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return Confirmation(GovernedLoopInvocationAuthorityConfirmationStatus.Unavailable, null, asOfUtc, "The exact authority profile could not be established.");
            }

            if (profileResult is null || profileResult.Status is AuthorityProfileMutationStatus.Unavailable)
            {
                return Confirmation(GovernedLoopInvocationAuthorityConfirmationStatus.Unavailable, null, asOfUtc, "The exact authority profile could not be established.");
            }

            if (profileResult.Status is AuthorityProfileMutationStatus.Conflict or AuthorityProfileMutationStatus.Invalid or AuthorityProfileMutationStatus.NotFound
                || !MatchesProfile(profileResult.Record, profile, ids.ProfileOperationId))
            {
                return Confirmation(GovernedLoopInvocationAuthorityConfirmationStatus.Conflict, null, asOfUtc, "The durable operation identity is already bound to a different profile lifecycle intent.");
            }

            profileRecord = profileResult.Record!;
        }
        else
        {
            return Confirmation(GovernedLoopInvocationAuthorityConfirmationStatus.Unavailable, null, asOfUtc, "The exact authority profile is unavailable or recovered read-only.");
        }

        var binding = new AuthorityGrantBinding(
            new AuthorityGrantProfilePin(new AuthorityProfileReference(profileRecord.CurrentProfile.ProfileId, profileRecord.CurrentProfile.Revision), profileRecord.CurrentHash),
            terms.Role!.RequestedPin!,
            terms.Publication!);
        var grantRequest = AuthorityGrantMutationRequestHash.Apply(new AuthorityGrantMutationRequest(
            AuthorityGrantContractLimits.CurrentSchemaVersion,
            ids.GrantOperationId,
            AuthorityGrantOperationKind.Create,
            ids.GrantId,
            0,
            AuthorityGrantLifecycleStatus.Unknown,
            binding,
            terms.Ceiling,
            new AuthorityGrantBoundary(profileRecord.CurrentProfile.IssuedAtUtc, null, AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion),
            _actor,
            Purpose(),
            string.Empty));
        var lifecycle = new AuthorityGrantLifecycleService(
            _grantStore,
            new GovernedLoopInvocationGrantActorAuthorizer(grantRequest, terms.SemanticHash),
            new AuthorityGrantProfileSource(_profileStore),
            _roleSource,
            new GovernedLoopPublishedRevisionSource(_revisionStore, _authorityTransaction),
            _bindingSource,
            _authorityTransaction,
            _timeProvider);
        AuthorityGrantMutationResult grantResult;
        try
        {
            grantResult = await lifecycle.MutateAsync(grantRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Confirmation(GovernedLoopInvocationAuthorityConfirmationStatus.Unavailable, null, asOfUtc, "The exact authority grant could not be established.");
        }

        if (grantResult is null || grantResult.Status is AuthorityGrantMutationStatus.Unavailable or AuthorityGrantMutationStatus.Ambiguous or AuthorityGrantMutationStatus.DependencyUnavailable)
        {
            return Confirmation(GovernedLoopInvocationAuthorityConfirmationStatus.Unavailable, null, asOfUtc, "The exact authority grant could not be established safely.");
        }

        if (grantResult.Status is AuthorityGrantMutationStatus.Invalid or AuthorityGrantMutationStatus.Conflict or AuthorityGrantMutationStatus.Denied or AuthorityGrantMutationStatus.CeilingExceeded)
        {
            return Confirmation(GovernedLoopInvocationAuthorityConfirmationStatus.Conflict, null, asOfUtc, "The durable operation identity or current authority dependencies no longer match the confirmed intent.");
        }

        if (grantResult.Status is not (AuthorityGrantMutationStatus.Committed or AuthorityGrantMutationStatus.Replayed)
            || grantResult.Grant is null
            || !SameGrantRequest(grantResult.Grant, grantRequest))
        {
            return Confirmation(GovernedLoopInvocationAuthorityConfirmationStatus.Unavailable, null, asOfUtc, "The exact authority grant outcome could not be proved safely.");
        }

        var confirmedReference = new AuthorityGrantReference(grantResult.Grant.GrantId, grantResult.Grant.Revision, grantResult.Grant.ContentHash);
        var confirmedChoices = await ReadEligibleGrantChoicesAsync(terms, CancellationToken.None).ConfigureAwait(false);
        if (confirmedChoices is null)
        {
            return Confirmation(GovernedLoopInvocationAuthorityConfirmationStatus.Unavailable, null, asOfUtc, "The confirmed authority grant could not be revalidated from current durable state.");
        }

        if (!confirmedChoices.Any(choice => Equals(choice.Grant, confirmedReference)))
        {
            return Confirmation(GovernedLoopInvocationAuthorityConfirmationStatus.Stale, null, asOfUtc, "The confirmed authority grant is no longer the exact active current grant.");
        }

        return Confirmation(GovernedLoopInvocationAuthorityConfirmationStatus.Confirmed, confirmedReference, asOfUtc, "The exact least-authority profile and grant are durably confirmed.");
    }

    private async Task<IReadOnlyList<GovernedLoopInvocationGrantChoice>?> ReadEligibleGrantChoicesAsync(InvocationPreparationTerms terms, CancellationToken cancellationToken)
    {
        AuthorityGrantCatalogReadResult catalog;
        try
        {
            catalog = await _grantCatalog.ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        if (catalog is null || !Enum.IsDefined(catalog.Status) || catalog.Status != AuthorityGrantCatalogReadStatus.Available || catalog.StoreGeneration < 0 || catalog.Grants is null)
        {
            return null;
        }

        var choices = new List<GovernedLoopInvocationGrantChoice>();
        foreach (var grant in catalog.Grants)
        {
            if (!IsExactBoundGrant(grant, terms))
            {
                continue;
            }

            var reference = new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash);
            AuthorityGrantResolution resolution;
            try
            {
                resolution = await _grantResolver.ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return null;
            }

            if (resolution is null || resolution.Status is AuthorityGrantResolutionStatus.Unavailable or AuthorityGrantResolutionStatus.Ambiguous)
            {
                return null;
            }

            if (resolution.Status == AuthorityGrantResolutionStatus.Active
                && resolution.Grant is not null
                && SameGrant(reference, resolution.Grant)
                && IsExactBoundGrant(resolution.Grant, terms))
            {
                var usageEligible = await IsUsageEligibleAsync(reference, resolution.Grant, cancellationToken).ConfigureAwait(false);
                if (usageEligible is null)
                {
                    return null;
                }

                if (!usageEligible.Value)
                {
                    continue;
                }

                choices.Add(new GovernedLoopInvocationGrantChoice(reference, resolution.Grant.Boundary.ExpiresAtUtc));
            }
        }

        return choices.OrderBy(value => value.Grant.GrantId.Value, StringComparer.Ordinal).ToArray();
    }

    private async Task<bool?> IsUsageEligibleAsync(
        AuthorityGrantReference reference,
        AuthorityGrant grant,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(grant.Boundary.CompletionConstraint))
        {
            return null;
        }

        if (grant.Boundary.CompletionConstraint == AuthorityGrantCompletionConstraintKind.None)
        {
            return true;
        }

        if (grant.Boundary.CompletionConstraint != AuthorityGrantCompletionConstraintKind.FirstBoundRunCompletion)
        {
            return null;
        }

        GovernedLoopEffectAuthorityGrantUsageReadResult usage;
        try
        {
            usage = await _usageReader.ReadCompletionUsageAsync(reference, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        if (usage is null || !Enum.IsDefined(usage.Status))
        {
            return null;
        }

        return usage.Status switch
        {
            GovernedLoopEffectAuthorityGrantUsageReadStatus.Unconsumed => true,
            GovernedLoopEffectAuthorityGrantUsageReadStatus.Consumed => false,
            _ => null,
        };
    }

    private async Task<InvocationPreparationTerms> ReadTermsAsync(string? graphId, string? revisionId, DateTimeOffset asOfUtc, CancellationToken cancellationToken)
    {
        if (!IsIdentifier(graphId) || !IsIdentifier(revisionId))
        {
            return InvocationPreparationTerms.Failure(GovernedLoopInvocationPreparationStatus.Invalid, null, "The selected graph or revision identifier is invalid.");
        }

        GovernedLoopRevisionGraphReadResult graphRead;
        try
        {
            graphRead = await _revisionStore.ReadGraphAsync(graphId!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return InvocationPreparationTerms.Failure(GovernedLoopInvocationPreparationStatus.Unavailable, null, "The current governed-loop publication is unavailable.");
        }

        if (graphRead is null
            || !Enum.IsDefined(graphRead.Status)
            || graphRead.Status is GovernedLoopRevisionStoreReadStatus.Unknown or GovernedLoopRevisionStoreReadStatus.Unavailable or GovernedLoopRevisionStoreReadStatus.Ambiguous)
        {
            return InvocationPreparationTerms.Failure(GovernedLoopInvocationPreparationStatus.Unavailable, null, "The current governed-loop publication is unavailable or ambiguous.");
        }

        if (graphRead.Status == GovernedLoopRevisionStoreReadStatus.NotFound)
        {
            return InvocationPreparationTerms.Failure(GovernedLoopInvocationPreparationStatus.NotFound, null, "The selected governed loop does not exist.");
        }

        if (graphRead.Snapshot is null)
        {
            return InvocationPreparationTerms.Failure(GovernedLoopInvocationPreparationStatus.Unavailable, null, "The current governed-loop publication is unavailable or ambiguous.");
        }

        var publication = graphRead.Snapshot.Head.PublishedRevision;
        if (publication is null)
        {
            return InvocationPreparationTerms.Failure(GovernedLoopInvocationPreparationStatus.NotFound, null, "The selected governed loop has no current publication.");
        }

        if (!string.Equals(publication.Revision.GraphId, graphId, StringComparison.Ordinal)
            || !string.Equals(publication.Revision.RevisionId, revisionId, StringComparison.Ordinal))
        {
            return InvocationPreparationTerms.Failure(GovernedLoopInvocationPreparationStatus.Stale, publication, "The selected revision is not the current published revision.");
        }

        GovernedLoopGrantBindingResolution binding;
        try
        {
            binding = await _bindingSource.ResolveAsync(publication, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return InvocationPreparationTerms.Failure(GovernedLoopInvocationPreparationStatus.Unavailable, publication, "Exact publication binding evidence is unavailable.");
        }

        if (binding is null || binding.Status is AuthorityGrantDependencyStatus.Unavailable or AuthorityGrantDependencyStatus.Ambiguous)
        {
            return InvocationPreparationTerms.Failure(GovernedLoopInvocationPreparationStatus.Unavailable, publication, "Exact publication binding evidence is unavailable or ambiguous.");
        }

        if (binding.Status != AuthorityGrantDependencyStatus.Active || !Equals(binding.PublicationPin, publication) || binding.OwningRole is null || binding.Artifact is null)
        {
            return InvocationPreparationTerms.Failure(GovernedLoopInvocationPreparationStatus.Stale, publication, "The selected publication is no longer active with its exact role binding.");
        }

        AuthorityGrantRoleResolution role;
        try
        {
            role = await _roleSource.ResolveAsync(binding.OwningRole, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return InvocationPreparationTerms.Failure(GovernedLoopInvocationPreparationStatus.Unavailable, publication, "Exact role evidence is unavailable.");
        }

        if (role is null || role.Status is AuthorityGrantDependencyStatus.Unavailable or AuthorityGrantDependencyStatus.Ambiguous)
        {
            return InvocationPreparationTerms.Failure(GovernedLoopInvocationPreparationStatus.Unavailable, publication, "Exact role evidence is unavailable or ambiguous.");
        }

        if (role.Status != AuthorityGrantDependencyStatus.Active
            || !Equals(role.RequestedPin, binding.OwningRole)
            || role.Revision is null
            || !string.Equals(role.WorkspaceId, _workspaceId, StringComparison.Ordinal))
        {
            return InvocationPreparationTerms.Failure(GovernedLoopInvocationPreparationStatus.Ineligible, publication, "The exact owning role is not currently eligible in this workspace.");
        }

        var model = await ReadCurrentModelAsync(binding.Artifact.Graph, role.Revision, cancellationToken).ConfigureAwait(false);
        if (!model.IsEligible)
        {
            return InvocationPreparationTerms.Failure(model.IsUnavailable ? GovernedLoopInvocationPreparationStatus.Unavailable : GovernedLoopInvocationPreparationStatus.Ineligible, publication, model.Detail);
        }

        if (!SupportsLeastAuthorityProjection(binding.Artifact.Graph))
        {
            return InvocationPreparationTerms.Failure(GovernedLoopInvocationPreparationStatus.Ineligible, publication, "The selected graph contains a node kind outside this first least-authority invocation slice.");
        }

        var allowed = binding.CapabilityIds.Intersect(role.Revision.PolicyMaxima.CapabilityIds, StringComparer.Ordinal).ToArray();
        if (allowed.Length != binding.CapabilityIds.Count)
        {
            return InvocationPreparationTerms.Failure(GovernedLoopInvocationPreparationStatus.Ineligible, publication, "The exact role policy does not permit every capability implemented by the selected graph.");
        }

        if (!TryBuildManifest(binding.Artifact, out var manifest))
        {
            return InvocationPreparationTerms.Failure(GovernedLoopInvocationPreparationStatus.Unavailable, publication, "The selected graph cannot form exact implemented-capability evidence.");
        }

        CapabilityAdmissionResult admission;
        try
        {
            admission = await _capabilityAdmission.AdmitAsync(manifest!, allowed.Select(value => CapabilityId.TryParse(value, out var parsed, out _) ? parsed : null).Where(value => value is not null).Cast<CapabilityId>().ToArray(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return InvocationPreparationTerms.Failure(GovernedLoopInvocationPreparationStatus.Unavailable, publication, "Current implemented-capability evidence is unavailable.");
        }

        if (admission is null || !admission.IsAdmitted || admission.Snapshot is null || CapabilityAdmissionSnapshotValidator.Validate(admission.Snapshot) is not null)
        {
            return InvocationPreparationTerms.Failure(GovernedLoopInvocationPreparationStatus.Ineligible, publication, "The selected graph's exact implemented capabilities are not currently eligible.");
        }

        if (!IsExactAdmittedModelProfile(model, admission.Snapshot))
        {
            return InvocationPreparationTerms.Failure(GovernedLoopInvocationPreparationStatus.Ineligible, publication, "The selected graph's exact model profile is not currently admitted.");
        }

        var ceiling = new AuthorityCeiling(
            admission.Snapshot.Pins.Select(pin => pin.DescriptorIdentity).OrderBy(identity => identity.Id.Value, StringComparer.Ordinal).ToArray(),
            binding.Artifact.Graph.Nodes.SelectMany(node => node.AuthoredInputDataClasses ?? []).Distinct().OrderBy(value => value.Value, StringComparer.Ordinal).ToArray(),
            0,
            CapabilitySideEffectClass.None,
            false,
            false,
            false);
        if (!AuthorityProfileValidator.ValidateCeiling(ceiling).IsValid)
        {
            return InvocationPreparationTerms.Failure(GovernedLoopInvocationPreparationStatus.Unavailable, publication, "The exact least-authority ceiling could not be represented safely.");
        }

        var semanticHash = ComputeSemanticHash(publication, binding, role, model, admission.Snapshot, ceiling);
        return InvocationPreparationTerms.Success(publication, binding, role, ceiling, semanticHash);
    }

    private async Task<InvocationPreparationModelTerms> ReadCurrentModelAsync(GovernedLoopGraphDefinition graph, ContextualRoleRevision role, CancellationToken cancellationToken)
    {
        var inferenceNodes = graph.Nodes.Where(node => node.Descriptor.Kind == GovernedLoopNodeKind.Inference).ToArray();
        if (inferenceNodes.Length == 0)
        {
            return InvocationPreparationModelTerms.Eligible(null, null, null, null);
        }

        var selectedProfileIds = new List<CapabilityId>(inferenceNodes.Length);
        foreach (var node in inferenceNodes)
        {
            var policy = node.ModelRoutingPolicy ?? graph.DefaultModelRoutingPolicy;
            if (policy.Selector.Kind != GovernedModelSelectorKind.Exact || policy.Selector.ExactProfileId is null || policy.FallbackProfileIds.Count != 0)
            {
                return InvocationPreparationModelTerms.Ineligible("The selected graph must pin one exact model profile without fallback for visible invocation.");
            }

            selectedProfileIds.Add(policy.Selector.ExactProfileId);
        }

        var distinctProfileIds = selectedProfileIds.Distinct().ToArray();
        if (distinctProfileIds.Length != 1)
        {
            return InvocationPreparationModelTerms.Ineligible("The selected graph's inference nodes do not pin one exact common model profile.");
        }

        var profileId = distinctProfileIds[0];

        ModelProfileSourceReadResult metadataRead;
        try
        {
            metadataRead = await _modelMetadataSource.ReadAsync(profileId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return InvocationPreparationModelTerms.Unavailable("The current configured model metadata is unavailable.");
        }

        if (metadataRead is null || metadataRead.Status != ModelProfileSourceReadStatus.Found || metadataRead.Metadata is null || !IsSha256(metadataRead.SourceRevisionHash))
        {
            return InvocationPreparationModelTerms.Unavailable("The current configured model metadata is unavailable.");
        }

        var metadata = metadataRead.Metadata;
        ModelProfileAdapterPosture posture;
        try
        {
            posture = await _modelAdapterRegistry.ReadPostureAsync(metadata, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return InvocationPreparationModelTerms.Unavailable("The current configured model adapter posture is unavailable.");
        }

        if (!IsExactAdapterPosture(posture, metadata.ContentHash))
        {
            return InvocationPreparationModelTerms.Unavailable("The current configured model adapter posture is unavailable.");
        }

        if (posture.Status != ModelProfileAdapterPostureStatus.Ready)
        {
            return posture.Status == ModelProfileAdapterPostureStatus.Unavailable
                ? InvocationPreparationModelTerms.Unavailable("The current configured model adapter is unavailable.")
                : InvocationPreparationModelTerms.Ineligible("The current configured model adapter is not eligible.");
        }

        foreach (var node in inferenceNodes)
        {
            var policy = node.ModelRoutingPolicy ?? graph.DefaultModelRoutingPolicy;
            if (!policy.Requirements.StaticallySatisfiedBy(metadata, role.Identity.RoleId, node.Descriptor.TypeId)
                || node.AuthoredInputDataClasses is not null && !policy.Requirements.SatisfiedBy(metadata, node.AuthoredInputDataClasses, role.Identity.RoleId, node.Descriptor.TypeId))
            {
                return InvocationPreparationModelTerms.Ineligible("The selected graph's exact inference routing is not eligible for the current model profile.");
            }
        }

        return InvocationPreparationModelTerms.Eligible(profileId, metadataRead.SourceRevisionHash, metadata, posture.RegistryRevisionHash);
    }

    private static bool IsExactAdapterPosture(ModelProfileAdapterPosture? posture, string metadataHash)
        => posture is not null
            && Enum.IsDefined(posture.Status)
            && posture.Status != 0
            && string.Equals(posture.ProfileMetadataHash, metadataHash, StringComparison.Ordinal)
            && IsSha256(posture.RegistryRevisionHash);

    private static bool IsExactAdmittedModelProfile(InvocationPreparationModelTerms model, CapabilityAdmissionSnapshot admission)
    {
        if (model.ProfileId is null)
        {
            return model.SourceRevisionHash is null && model.Metadata is null && model.AdapterRegistryRevisionHash is null;
        }

        if (model.SourceRevisionHash is null || model.Metadata is null || model.AdapterRegistryRevisionHash is null)
        {
            return false;
        }

        var pins = admission.Pins.Where(pin => pin.DescriptorIdentity.Id.Equals(model.ProfileId)).ToArray();
        return pins.Length == 1
            && pins[0].Kind == CapabilityKind.ModelProfile
            && Equals(pins[0].DescriptorIdentity, model.Metadata.DescriptorIdentity);
    }

    private static bool SupportsLeastAuthorityProjection(GovernedLoopGraphDefinition graph)
        => graph.Nodes.All(IsSupportedLeastAuthorityNode);

    private static bool IsSupportedLeastAuthorityNode(GovernedLoopNodeDefinition node)
        => node.Descriptor.Kind switch
        {
            GovernedLoopNodeKind.Trigger => Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.ManualTrigger),
            GovernedLoopNodeKind.Inference => Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.ProviderInference),
            GovernedLoopNodeKind.Validate or GovernedLoopNodeKind.Condition => node.AuthorityCeiling.CapabilityIds.Count == 0
                && GovernedLoopSequentialNodeDescriptors.IsDeterministic(node.Descriptor),
            GovernedLoopNodeKind.Exit => Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.SuccessExit),
            GovernedLoopNodeKind.Fail => Equals(node.Descriptor, GovernedLoopSequentialNodeDescriptors.FailTerminal),
            _ => false,
        };

    private static bool TryBuildManifest(GovernedLoopGraphRevisionArtifact artifact, out CapabilityDependencyManifest? manifest)
    {
        manifest = null;
        if (!IsSha256(artifact.ArtifactHash)
            || !CapabilityId.TryParse("org.embodysense/loop-" + artifact.ArtifactHash[..32], out var subject, out _)
            || !CapabilityVersionRange.TryParse("*", out var any, out _)
            || !CapabilityIntegrityDigest.TryParse("sha256:" + artifact.ArtifactHash, out var checksum, out _))
        {
            return false;
        }

        var required = new List<CapabilityDependency>();
        foreach (var value in artifact.Graph.AuthorityCeiling.CapabilityIds.Order(StringComparer.Ordinal))
        {
            if (!CapabilityId.TryParse(value, out var capabilityId, out _))
            {
                return false;
            }

            required.Add(new CapabilityDependency(capabilityId!, any!));
        }

        var candidate = new CapabilityDependencyManifest(
            CapabilityDependencyManifest.CurrentSchemaVersion,
            CapabilityDependencyManifestKind.LoopPackage,
            subject!,
            required,
            [],
            new CapabilityDependencyArtifactMetadata(checksum, null));
        if (!CapabilityDependencyManifestHash.TryCompute(candidate, out _, out _))
        {
            return false;
        }

        manifest = candidate;
        return true;
    }

    private static bool IsExactBoundGrant(AuthorityGrant? grant, InvocationPreparationTerms terms)
        => grant is { Status: AuthorityGrantLifecycleStatus.Active, Binding: not null }
            && Equals(grant.Binding.Loop, terms.Publication)
            && Equals(grant.Binding.Role, terms.Role!.RequestedPin)
            && AuthorityCeilingSubset.IsEqual(grant.RequestedCeiling, terms.Ceiling);

    private static bool SameGrant(AuthorityGrantReference reference, AuthorityGrant grant)
        => reference.GrantId.Equals(grant.GrantId) && reference.Revision.Equals(grant.Revision) && string.Equals(reference.ContentHash, grant.ContentHash, StringComparison.Ordinal);

    private static bool SameGrantRequest(AuthorityGrant grant, AuthorityGrantMutationRequest request)
        => grant.Status == AuthorityGrantLifecycleStatus.Active
            && grant.GrantId.Equals(request.GrantId)
            && grant.Revision.Value == 1
            && Equals(grant.Binding, request.CandidateBinding)
            && AuthorityCeilingSubset.IsEqual(grant.RequestedCeiling, request.CandidateCeiling)
            && Equals(grant.Boundary, request.CandidateBoundary)
            && grant.ChangedByActorId.Equals(request.ActorId)
            && Equals(grant.Reason, request.Reason);

    private static bool MatchesProfile(AuthorityProfileRecord? record, AuthorityProfile expected, string operationId)
        => record is { Tombstone: null, CurrentProfile: { } profile, CurrentHash: not null, Operations: not null }
            && profile.SchemaVersion == expected.SchemaVersion
            && profile.ProfileId.Equals(expected.ProfileId)
            && profile.Revision.Equals(expected.Revision)
            && profile.Status == expected.Status
            && profile.Purpose.Equals(expected.Purpose)
            && Equals(profile.Provenance, expected.Provenance)
            && profile.IssuedAtUtc == expected.IssuedAtUtc
            && profile.ExpiresAtUtc == expected.ExpiresAtUtc
            && AuthorityCeilingSubset.IsEqual(profile.Ceiling, expected.Ceiling)
            && profile.BoundaryConditions.SequenceEqual(expected.BoundaryConditions)
            && record.Operations.Any(operation => string.Equals(operation.OperationId, operationId, StringComparison.Ordinal)
                && operation.Kind == AuthorityProfileMutationKind.Create
                && operation.Outcome == AuthorityProfileMutationStatus.Applied);

    private AuthorityProfile CreateProfile(AuthorityProfileId profileId, AuthorityCeiling ceiling, DateTimeOffset issuedAtUtc)
    {
        _ = AuthorityProfileRevision.TryParse("1", out var revision, out _);
        return new AuthorityProfile(
            AuthorityProfile.CurrentSchemaVersion,
            profileId,
            revision!,
            AuthorityProfileStatus.Active,
            Purpose(),
            new AuthorityProvenance(_actor, AuthorityProvenanceKind.UserDeclaration),
            issuedAtUtc,
            null,
            ceiling,
            []);
    }

    private static InvocationIds DeriveIds(string operationId, InvocationPreparationTerms terms)
    {
        var basis = string.Join("\n", "governed-loop-invocation-v1", operationId, terms.SemanticHash, terms.Publication!.Revision.GraphId, terms.Publication.Revision.RevisionId, terms.Publication.Revision.ExecutableHash, terms.Publication.PublicationOperationId, terms.Publication.ValidationEvidenceHash);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(basis))).ToLowerInvariant();
        _ = AuthorityProfileId.TryParse("invocation-profile-" + digest, out var profileId, out _);
        _ = AuthorityGrantId.TryParse("invocation-grant-" + digest, out var grantId, out _);
        return new InvocationIds(profileId!, grantId!, "invocation-profile-op-" + Hash(operationId), "invocation-grant-op-" + Hash(operationId));
    }

    private string ComputeSemanticHash(
        GovernedLoopRevisionPublicationPin publication,
        GovernedLoopGrantBindingResolution binding,
        AuthorityGrantRoleResolution role,
        InvocationPreparationModelTerms model,
        CapabilityAdmissionSnapshot admission,
        AuthorityCeiling ceiling)
    {
        var values = new List<string>
        {
            "governed-loop-invocation-preview-v1",
            _workspaceId,
            _actor.Value,
            publication.Revision.GraphId,
            publication.Revision.RevisionId,
            publication.Revision.ExecutableHash,
            publication.PublicationOperationId,
            publication.ValidationEvidenceHash,
            binding.EvidenceHash,
            role.RequestedPin!.Identity.RoleId,
            role.RequestedPin.Identity.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            role.RequestedPin.ContentHash,
            role.EvidenceHash,
            admission.RequirementsHash,
            ceiling.MaxTargetCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ((int)ceiling.MaxSideEffectClass).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ceiling.AllowsRecurrence ? "1" : "0",
            ceiling.AllowsExternalPublication ? "1" : "0",
            ceiling.AllowsIrreversibleAction ? "1" : "0",
        };
        if (model.ProfileId is null || model.SourceRevisionHash is null || model.Metadata is null || model.AdapterRegistryRevisionHash is null)
        {
            values.Add("model:none");
        }
        else
        {
            values.AddRange([
                model.ProfileId.Value,
                model.SourceRevisionHash,
                model.Metadata.ContentHash,
                model.AdapterRegistryRevisionHash,
            ]);
        }
        values.AddRange(binding.CapabilityIds.OrderBy(value => value, StringComparer.Ordinal).Select(value => "required:" + value));
        values.AddRange(admission.Pins.OrderBy(pin => pin.DescriptorIdentity.Id.Value, StringComparer.Ordinal).Select(pin => string.Join("|", pin.DescriptorIdentity.Id.Value, pin.DescriptorIdentity.Version.Value, pin.DescriptorIdentity.Hash.Value)));
        values.AddRange(ceiling.DataClasses.OrderBy(value => value.Value, StringComparer.Ordinal).Select(value => "data:" + value.Value));
        return Hash(string.Join("\n", values));
    }

    private static bool IsConfirmationValid(GovernedLoopInvocationAuthorityConfirmation? confirmation)
        => confirmation is not null
            && IsIdentifier(confirmation.GraphId)
            && IsIdentifier(confirmation.RevisionId)
            && IsSha256(confirmation.ExpectedPreviewHash)
            && IsIdentifier(confirmation.OperationId);

    private static bool IsIdentifier(string? value)
        => value is { Length: > 0 and <= 128 }
            && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.');

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedEquals(string left, string right)
        => left.Length == right.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static AuthorityPurpose Purpose()
    {
        _ = AuthorityPurpose.TryParse(PurposeText, out var purpose, out _);
        return purpose!;
    }

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private static GovernedLoopInvocationPreparationResponse Preparation(
        GovernedLoopInvocationPreparationStatus status,
        GovernedLoopRevisionPublicationPin? publication,
        IReadOnlyList<GovernedLoopInvocationGrantChoice> grants,
        GovernedLoopInvocationAuthorityPreview? preview,
        DateTimeOffset asOfUtc,
        string detail)
        => new(status, publication, grants, preview, asOfUtc, CommonExpiry(grants, preview), detail);

    private static DateTimeOffset? CommonExpiry(
        IReadOnlyList<GovernedLoopInvocationGrantChoice> grants,
        GovernedLoopInvocationAuthorityPreview? preview)
    {
        if (preview is not null)
        {
            return preview.ExpiresAtUtc;
        }

        var expirations = grants.Select(grant => grant.ExpiresAtUtc).Distinct().ToArray();
        return expirations.Length == 1 ? expirations[0] : null;
    }

    private static GovernedLoopInvocationAuthorityConfirmationResult Confirmation(
        GovernedLoopInvocationAuthorityConfirmationStatus status,
        AuthorityGrantReference? grant,
        DateTimeOffset asOfUtc,
        string detail)
        => new(status, grant, asOfUtc, detail);

    private static GovernedLoopInvocationAuthorityConfirmationStatus MapTermsStatus(GovernedLoopInvocationPreparationStatus status)
        => status switch
        {
            GovernedLoopInvocationPreparationStatus.Invalid => GovernedLoopInvocationAuthorityConfirmationStatus.Invalid,
            GovernedLoopInvocationPreparationStatus.NotFound or GovernedLoopInvocationPreparationStatus.Stale => GovernedLoopInvocationAuthorityConfirmationStatus.Stale,
            GovernedLoopInvocationPreparationStatus.Ineligible => GovernedLoopInvocationAuthorityConfirmationStatus.Ineligible,
            _ => GovernedLoopInvocationAuthorityConfirmationStatus.Unavailable,
        };

    private sealed record InvocationIds(AuthorityProfileId ProfileId, AuthorityGrantId GrantId, string ProfileOperationId, string GrantOperationId);

}
