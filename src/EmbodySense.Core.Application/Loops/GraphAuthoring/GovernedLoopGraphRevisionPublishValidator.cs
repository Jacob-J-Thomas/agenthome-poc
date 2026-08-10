using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.GraphAuthoring;

internal sealed class GovernedLoopGraphRevisionPublishValidator : IGovernedLoopRevisionPublishValidator
{
    private readonly IGovernedLoopGraphRevisionStore _store;
    private readonly GovernedLoopGraphValidationService _validationService;
    private readonly string _authoringRequestHash;
    private readonly GovernedLoopGraphDefinition? _pendingGraph;

    internal GovernedLoopGraphRevisionPublishValidator(
        IGovernedLoopGraphRevisionStore store,
        GovernedLoopGraphValidationService validationService,
        string authoringRequestHash,
        GovernedLoopGraphDefinition? pendingGraph)
    {
        _store = store;
        _validationService = validationService;
        _authoringRequestHash = authoringRequestHash;
        _pendingGraph = pendingGraph;
    }

    public async Task<GovernedLoopRevisionPublishValidation> ValidateAsync(
        GovernedLoopRevisionPublishValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        GovernedLoopGraphRevisionArtifact? artifact;
        if (_pendingGraph is not null
            && SameRevision(_pendingGraph.RevisionReference, request.Artifact.Revision))
        {
            try
            {
                artifact = GovernedLoopGraphRevisionArtifactFactory.Create(
                    GovernedLoopGraphDefinition.CurrentSchemaVersion,
                    request.Artifact,
                    _pendingGraph);
            }
            catch (ArgumentException)
            {
                return Result(request, GovernedLoopRevisionPublishValidationStatus.Unavailable, FailureHash(request, "pending-artifact-invalid"));
            }
        }
        else
        {
            GovernedLoopGraphRevisionArtifactReadResult read;
            try
            {
                read = await _store.ReadArtifactAsync(request.Artifact.Revision, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return Result(request, GovernedLoopRevisionPublishValidationStatus.Unavailable, FailureHash(request, "artifact-read-failed"));
            }

            if (read is null
                || read.Status != GovernedLoopRevisionStoreReadStatus.Ready
                || read.StoreGeneration <= 0
                || read.Artifact is null
                || !Equals(read.Artifact.RevisionArtifact, request.Artifact))
            {
                return Result(request, GovernedLoopRevisionPublishValidationStatus.Unavailable, FailureHash(request, "artifact-unproved"));
            }

            artifact = read.Artifact;
        }

        GovernedLoopGraphValidationResult validation;
        try
        {
            validation = await _validationService.ValidateAsync(
                GovernedLoopGraphCandidateProjection.FromDefinition(artifact.Graph),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(request, GovernedLoopRevisionPublishValidationStatus.Unavailable, FailureHash(request, "validation-failed"));
        }

        if (validation is null)
        {
            return Result(request, GovernedLoopRevisionPublishValidationStatus.Unavailable, FailureHash(request, "validation-missing"));
        }

        var evidenceHash = validation.Evidence is null
            ? FailureHash(request, "validation-evidence-missing")
            : GovernedLoopGraphValidationBindingHash.Compute(
                _authoringRequestHash,
                artifact,
                validation.Evidence.CombinedHash);
        return Result(
            request,
            validation.IsValid
                ? GovernedLoopRevisionPublishValidationStatus.Valid
                : GovernedLoopRevisionPublishValidationStatus.Invalid,
            evidenceHash);
    }

    private static GovernedLoopRevisionPublishValidation Result(
        GovernedLoopRevisionPublishValidationRequest request,
        GovernedLoopRevisionPublishValidationStatus status,
        string evidenceHash)
    {
        return new GovernedLoopRevisionPublishValidation(
            status,
            request.OperationId,
            request.RequestHash,
            request.Artifact.Revision,
            evidenceHash);
    }

    private static string FailureHash(GovernedLoopRevisionPublishValidationRequest request, string reason)
    {
        var value = string.Join(
            '\0',
            "governed-loop-graph-publish-validation-failure-v1",
            request.OperationId,
            request.RequestHash,
            request.Artifact.Revision.GraphId,
            request.Artifact.Revision.RevisionId,
            reason);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static bool SameRevision(
        GovernedLoopRevisionReference left,
        GovernedLoopRevisionReference right)
    {
        return left.SchemaVersion == right.SchemaVersion
            && string.Equals(left.GraphId, right.GraphId, StringComparison.Ordinal)
            && string.Equals(left.RevisionId, right.RevisionId, StringComparison.Ordinal)
            && string.Equals(left.ExecutableHash, right.ExecutableHash, StringComparison.Ordinal);
    }
}
