using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants;

/// <summary>Resolves exact published graph ownership and capability evidence under one shared workspace authority fence.</summary>
public sealed class GovernedLoopGrantBindingSource : IGovernedLoopGrantBindingSource
{
    private readonly IGovernedLoopPublishedRevisionSource _publicationSource;
    private readonly IGovernedLoopGraphRevisionStore _graphStore;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;

    /// <summary>Creates an exact binding source over publication and graph-artifact ports.</summary>
    /// <param name="publicationSource">The exact current publication source.</param>
    /// <param name="graphStore">The exact immutable graph-artifact store.</param>
    /// <param name="authorityTransaction">The shared reentrant workspace authority fence.</param>
    /// <exception cref="ArgumentNullException">Thrown when a required port is <see langword="null"/>.</exception>
    public GovernedLoopGrantBindingSource(
        IGovernedLoopPublishedRevisionSource publicationSource,
        IGovernedLoopGraphRevisionStore graphStore,
        ICapabilityAuthorityTransaction authorityTransaction)
    {
        _publicationSource = publicationSource ?? throw new ArgumentNullException(nameof(publicationSource));
        _graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
    }

    /// <inheritdoc />
    public async Task<GovernedLoopGrantBindingResolution> ResolveAsync(GovernedLoopRevisionPublicationPin? pin, CancellationToken cancellationToken = default)
    {
        GovernedLoopGrantBindingResolution? completedResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async transactionToken =>
                {
                    completedResult = await ResolveUnderFenceAsync(pin, transactionToken).ConfigureAwait(false);
                    return completedResult;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && completedResult is null)
        {
            throw;
        }
        catch (Exception)
        {
            if (HasExactActiveProof(completedResult))
            {
                return completedResult!;
            }

            return Result(
                completedResult is null ? AuthorityGrantDependencyStatus.Unavailable : AuthorityGrantDependencyStatus.Ambiguous,
                SafePin(pin));
        }
    }

    private async Task<GovernedLoopGrantBindingResolution> ResolveUnderFenceAsync(GovernedLoopRevisionPublicationPin? pin, CancellationToken cancellationToken)
    {
        if (!GovernedLoopRevisionContractValidator.Validate(pin).IsValid)
        {
            return Result(AuthorityGrantDependencyStatus.Invalid, null);
        }

        GovernedLoopPublishedRevisionResolution? publication;
        try
        {
            publication = await _publicationSource.ResolveAsync(pin, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(AuthorityGrantDependencyStatus.Unavailable, pin);
        }

        var publicationStatus = MapPublication(publication, pin!);
        if (publicationStatus != AuthorityGrantDependencyStatus.Active)
        {
            return Result(publicationStatus, pin);
        }

        GovernedLoopGraphRevisionArtifactReadResult? read;
        try
        {
            read = await _graphStore.ReadArtifactAsync(pin!.Revision, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(AuthorityGrantDependencyStatus.Unavailable, pin);
        }

        if (read is null || read.StoreGeneration < 0 || !Enum.IsDefined(read.Status) || read.Status == GovernedLoopRevisionStoreReadStatus.Unknown)
        {
            return Result(AuthorityGrantDependencyStatus.Ambiguous, pin);
        }

        if (read.Status == GovernedLoopRevisionStoreReadStatus.NotFound && read.Artifact is null)
        {
            return Result(AuthorityGrantDependencyStatus.NotFound, pin);
        }

        if (read.Status == GovernedLoopRevisionStoreReadStatus.Unavailable && read.Artifact is null)
        {
            return Result(AuthorityGrantDependencyStatus.Unavailable, pin);
        }

        if (read.Status != GovernedLoopRevisionStoreReadStatus.Ready
            || read.StoreGeneration < 1
            || !TryValidateArtifact(read.Artifact, publication!.Artifact!, pin, out var artifact))
        {
            return Result(AuthorityGrantDependencyStatus.Ambiguous, pin);
        }

        var owner = artifact!.Graph.OwningRole;
        var capabilities = artifact.Graph.AuthorityCeiling.CapabilityIds;
        var evidenceValues = new List<string>
        {
            pin.Revision.GraphId,
            pin.Revision.RevisionId,
            pin.Revision.ExecutableHash,
            pin.PublicationOperationId,
            pin.ValidationEvidenceHash,
            publication.ObservedLifecycleVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            publication.ObservedLifecycleHeadOperationId,
            artifact.ArtifactHash,
            owner.Identity.RoleId,
            owner.Identity.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            owner.ContentHash,
        };
        evidenceValues.AddRange(capabilities.Order(StringComparer.Ordinal));
        return new GovernedLoopGrantBindingResolution(
            AuthorityGrantDependencyStatus.Active,
            pin,
            artifact,
            owner,
            capabilities,
            AuthorityGrantEvidenceHash.Compute(evidenceValues.ToArray()));
    }

    private static AuthorityGrantDependencyStatus MapPublication(GovernedLoopPublishedRevisionResolution? publication, GovernedLoopRevisionPublicationPin pin)
    {
        if (publication is null || !Enum.IsDefined(publication.Status) || publication.Status == GovernedLoopPublishedRevisionResolutionStatus.Unknown)
        {
            return AuthorityGrantDependencyStatus.Ambiguous;
        }

        if (!Equals(publication.RequestedPin, pin))
        {
            return AuthorityGrantDependencyStatus.Ambiguous;
        }

        if (publication.Status == GovernedLoopPublishedRevisionResolutionStatus.Active)
        {
            return IsExactActivePublication(publication, pin)
                ? AuthorityGrantDependencyStatus.Active
                : AuthorityGrantDependencyStatus.Ambiguous;
        }

        return publication.Status switch
        {
            GovernedLoopPublishedRevisionResolutionStatus.Disabled or GovernedLoopPublishedRevisionResolutionStatus.Archived => AuthorityGrantDependencyStatus.Disabled,
            GovernedLoopPublishedRevisionResolutionStatus.Stale => AuthorityGrantDependencyStatus.Stale,
            GovernedLoopPublishedRevisionResolutionStatus.NotFound => AuthorityGrantDependencyStatus.NotFound,
            GovernedLoopPublishedRevisionResolutionStatus.Invalid => AuthorityGrantDependencyStatus.Invalid,
            GovernedLoopPublishedRevisionResolutionStatus.Unavailable => AuthorityGrantDependencyStatus.Unavailable,
            _ => AuthorityGrantDependencyStatus.Ambiguous,
        };
    }

    private static bool IsExactActivePublication(GovernedLoopPublishedRevisionResolution publication, GovernedLoopRevisionPublicationPin pin)
        => Equals(publication.RequestedPin, pin)
            && publication.Artifact is not null
            && GovernedLoopRevisionContractValidator.Validate(publication.Artifact).IsValid
            && SameRevision(publication.Artifact.Revision, pin.Revision)
            && publication.ObservedLifecycleStatus == GovernedLoopRevisionLifecycleStatus.Published
            && publication.ObservedLifecycleVersion > 0
            && CustomLoopArtifactIdentifier.IsValid(publication.ObservedLifecycleHeadOperationId, GovernedLoopRevisionContractLimits.MaxIdentifierCharacters);

    private static bool TryValidateArtifact(
        GovernedLoopGraphRevisionArtifact? candidate,
        GovernedLoopRevisionArtifact publicationArtifact,
        GovernedLoopRevisionPublicationPin pin,
        out GovernedLoopGraphRevisionArtifact? artifact)
    {
        artifact = null;
        var graphCapabilities = candidate?.Graph.AuthorityCeiling.CapabilityIds.ToHashSet(StringComparer.Ordinal);
        if (candidate is null
            || !Equals(candidate.RevisionArtifact, publicationArtifact)
            || !SameRevision(candidate.RevisionArtifact.Revision, pin.Revision)
            || candidate.Graph.OwningRole?.Identity is null
            || !ContextualRoleId.IsValid(candidate.Graph.OwningRole.Identity.RoleId)
            || candidate.Graph.OwningRole.Identity.Revision < 1
            || !IsSha256(candidate.Graph.OwningRole.ContentHash)
            || candidate.Graph.AuthorityCeiling.CapabilityIds.Count > CustomLoopLimits.MaxGraphAuthorityCapabilities
            || candidate.Graph.AuthorityCeiling.CapabilityIds.Any(value => !CapabilityId.TryParse(value, out _, out _))
            || candidate.Graph.AuthorityCeiling.CapabilityIds.Distinct(StringComparer.Ordinal).Count() != candidate.Graph.AuthorityCeiling.CapabilityIds.Count
            || candidate.Graph.Nodes.Any(node => node.AuthorityCeiling.CapabilityIds.Any(capability => !graphCapabilities!.Contains(capability))))
        {
            return false;
        }

        try
        {
            if (!string.Equals(GovernedLoopGraphRevisionContractHash.ComputeArtifactHash(candidate), candidate.ArtifactHash, StringComparison.Ordinal))
            {
                return false;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }

        artifact = candidate;
        return true;
    }

    private static bool HasExactActiveProof(GovernedLoopGrantBindingResolution? result)
        => result is
        {
            Status: AuthorityGrantDependencyStatus.Active,
            PublicationPin: { } pin,
            Artifact: { } artifact,
            OwningRole: { } owner,
            CapabilityIds: not null,
        }
            && TryValidateArtifact(artifact, artifact.RevisionArtifact, pin, out _)
            && Equals(owner, artifact.Graph.OwningRole)
            && result.CapabilityIds.SequenceEqual(artifact.Graph.AuthorityCeiling.CapabilityIds, StringComparer.Ordinal)
            && AuthorityGrantEvidenceHash.IsSha256(result.EvidenceHash);

    private static GovernedLoopGrantBindingResolution Result(AuthorityGrantDependencyStatus status, GovernedLoopRevisionPublicationPin? pin)
        => new(status, pin, null, null, [], string.Empty);

    private static GovernedLoopRevisionPublicationPin? SafePin(GovernedLoopRevisionPublicationPin? pin)
        => GovernedLoopRevisionContractValidator.Validate(pin).IsValid ? pin : null;

    private static bool SameRevision(GovernedLoopRevisionReference left, GovernedLoopRevisionReference right)
        => left.SchemaVersion == right.SchemaVersion
            && string.Equals(left.GraphId, right.GraphId, StringComparison.Ordinal)
            && string.Equals(left.RevisionId, right.RevisionId, StringComparison.Ordinal)
            && string.Equals(left.ExecutableHash, right.ExecutableHash, StringComparison.Ordinal);

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
