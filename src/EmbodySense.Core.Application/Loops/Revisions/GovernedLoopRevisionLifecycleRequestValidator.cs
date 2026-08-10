using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Revisions;

/// <summary>Validates one lifecycle request without consulting authority, persistence, or graph payloads.</summary>
public static class GovernedLoopRevisionLifecycleRequestValidator
{
    /// <summary>Returns every bounded deterministic request-shape error.</summary>
    /// <param name="request">The candidate lifecycle request.</param>
    /// <returns>At most the schema-1 maximum number of value-free errors.</returns>
    public static IReadOnlyList<GovernedLoopRevisionLifecycleValidationError> Validate(GovernedLoopRevisionLifecycleRequest? request)
    {
        var errors = new List<GovernedLoopRevisionLifecycleValidationError>();
        if (request is null)
        {
            Add(errors, GovernedLoopRevisionLifecycleValidationErrorCode.RequestRequired, "$");
            return Array.AsReadOnly(errors.ToArray());
        }

        if (request.SchemaVersion != GovernedLoopRevisionContractLimits.CurrentSchemaVersion)
        {
            Add(errors, GovernedLoopRevisionLifecycleValidationErrorCode.UnsupportedSchemaVersion, "schemaVersion");
        }

        ValidateIdentifier(request.OperationId, "operationId", errors);
        ValidateIdentifier(request.GraphId, "graphId", errors);
        if (request.ActorId is null)
        {
            Add(errors, GovernedLoopRevisionLifecycleValidationErrorCode.InvalidActor, "actorId");
        }

        if (!Enum.IsDefined(request.Kind) || request.Kind == GovernedLoopRevisionOperationKind.Unknown)
        {
            Add(errors, GovernedLoopRevisionLifecycleValidationErrorCode.InvalidOperationKind, "kind");
        }

        ValidateLifecycleExpectation(request, errors);
        ValidateReference(request.ExpectedDraftRevision, request.GraphId, "expectedDraftRevision", false, errors);
        ValidatePublication(request.ExpectedPublishedRevision, request.GraphId, "expectedPublishedRevision", false, errors);
        ValidateReference(request.CandidateRevision, request.GraphId, "candidateRevision", false, errors);
        ValidateReference(request.TargetRevision, request.GraphId, "targetRevision", false, errors);
        ValidatePublication(request.RollbackSourcePublication, request.GraphId, "rollbackSourcePublication", false, errors);
        ValidateOperationShape(request, errors);
        return Array.AsReadOnly(errors.ToArray());
    }

    private static void ValidateLifecycleExpectation(
        GovernedLoopRevisionLifecycleRequest request,
        List<GovernedLoopRevisionLifecycleValidationError> errors)
    {
        if (request.ExpectedLifecycleVersion is < 0 or > GovernedLoopRevisionContractLimits.MaxLifecycleVersion)
        {
            Add(errors, GovernedLoopRevisionLifecycleValidationErrorCode.InvalidLifecycleExpectation, "expectedLifecycleVersion");
            return;
        }

        if (request.ExpectedLifecycleVersion == 0)
        {
            if (request.ExpectedLifecycleStatus != GovernedLoopRevisionLifecycleStatus.Unknown
                || request.ExpectedDraftRevision is not null
                || request.ExpectedPublishedRevision is not null)
            {
                Add(errors, GovernedLoopRevisionLifecycleValidationErrorCode.InvalidLifecycleExpectation, "expectedLifecycle");
            }

            return;
        }

        if (!Enum.IsDefined(request.ExpectedLifecycleStatus)
            || request.ExpectedLifecycleStatus == GovernedLoopRevisionLifecycleStatus.Unknown)
        {
            Add(errors, GovernedLoopRevisionLifecycleValidationErrorCode.InvalidLifecycleExpectation, "expectedLifecycleStatus");
            return;
        }

        switch (request.ExpectedLifecycleStatus)
        {
            case GovernedLoopRevisionLifecycleStatus.Draft when request.ExpectedDraftRevision is null || request.ExpectedPublishedRevision is not null:
            case GovernedLoopRevisionLifecycleStatus.Published when request.ExpectedPublishedRevision is null:
            case GovernedLoopRevisionLifecycleStatus.Disabled when request.ExpectedPublishedRevision is null:
            case GovernedLoopRevisionLifecycleStatus.Archived when request.ExpectedPublishedRevision is null || request.ExpectedDraftRevision is not null:
                Add(errors, GovernedLoopRevisionLifecycleValidationErrorCode.InvalidLifecycleExpectation, "expectedLifecycle");
                break;
        }
    }

    private static void ValidateOperationShape(
        GovernedLoopRevisionLifecycleRequest request,
        List<GovernedLoopRevisionLifecycleValidationError> errors)
    {
        switch (request.Kind)
        {
            case GovernedLoopRevisionOperationKind.CreateDraft:
                Require(request.CandidateRevision, "candidateRevision", errors);
                Reject(request.TargetRevision, "targetRevision", errors);
                Reject(request.RollbackSourcePublication, "rollbackSourcePublication", errors);
                if (request.ExpectedLifecycleVersion != 0)
                {
                    Add(errors, GovernedLoopRevisionLifecycleValidationErrorCode.InvalidLifecycleExpectation, "expectedLifecycleVersion");
                }

                break;
            case GovernedLoopRevisionOperationKind.ReplaceDraft:
                Require(request.CandidateRevision, "candidateRevision", errors);
                Require(request.TargetRevision, "targetRevision", errors);
                Reject(request.RollbackSourcePublication, "rollbackSourcePublication", errors);
                ValidateTargetMatchesExpectedDraftOrPublication(request, errors);
                break;
            case GovernedLoopRevisionOperationKind.Publish:
                Reject(request.CandidateRevision, "candidateRevision", errors);
                Require(request.TargetRevision, "targetRevision", errors);
                Reject(request.RollbackSourcePublication, "rollbackSourcePublication", errors);
                if (!SameRevision(request.TargetRevision, request.ExpectedDraftRevision))
                {
                    Add(errors, GovernedLoopRevisionLifecycleValidationErrorCode.InvalidLifecycleExpectation, "targetRevision");
                }

                break;
            case GovernedLoopRevisionOperationKind.Disable:
            case GovernedLoopRevisionOperationKind.Archive:
                Reject(request.CandidateRevision, "candidateRevision", errors);
                Require(request.TargetRevision, "targetRevision", errors);
                Reject(request.RollbackSourcePublication, "rollbackSourcePublication", errors);
                ValidateTargetMatchesExpectedPublication(request, errors);
                break;
            case GovernedLoopRevisionOperationKind.Rollback:
                Require(request.CandidateRevision, "candidateRevision", errors);
                Require(request.TargetRevision, "targetRevision", errors);
                Require(request.RollbackSourcePublication, "rollbackSourcePublication", errors);
                ValidateTargetMatchesExpectedDraftOrPublication(request, errors);
                if (string.Equals(
                    request.OperationId,
                    request.RollbackSourcePublication?.PublicationOperationId,
                    StringComparison.Ordinal))
                {
                    Add(
                        errors,
                        GovernedLoopRevisionLifecycleValidationErrorCode.InvalidReference,
                        "rollbackSourcePublication.publicationOperationId");
                }

                if (request.CandidateRevision is { } candidate && request.RollbackSourcePublication?.Revision is { } source)
                {
                    if (SameRevisionIdentity(candidate, source))
                    {
                        Add(errors, GovernedLoopRevisionLifecycleValidationErrorCode.CandidateNotDistinct, "candidateRevision");
                    }

                    if (!string.Equals(candidate.ExecutableHash, source.ExecutableHash, StringComparison.Ordinal))
                    {
                        Add(errors, GovernedLoopRevisionLifecycleValidationErrorCode.RollbackContentMismatch, "candidateRevision.executableHash");
                    }
                }

                break;
        }

        if (request.CandidateRevision is { } candidateRevision
            && (SameRevisionIdentity(candidateRevision, request.ExpectedDraftRevision)
                || SameRevisionIdentity(candidateRevision, request.ExpectedPublishedRevision?.Revision)
                || SameRevisionIdentity(candidateRevision, request.TargetRevision)))
        {
            Add(errors, GovernedLoopRevisionLifecycleValidationErrorCode.CandidateNotDistinct, "candidateRevision");
        }
    }

    private static void ValidateTargetMatchesExpectedDraftOrPublication(
        GovernedLoopRevisionLifecycleRequest request,
        List<GovernedLoopRevisionLifecycleValidationError> errors)
    {
        var expected = request.ExpectedDraftRevision ?? request.ExpectedPublishedRevision?.Revision;
        if (!SameRevision(request.TargetRevision, expected))
        {
            Add(errors, GovernedLoopRevisionLifecycleValidationErrorCode.InvalidLifecycleExpectation, "targetRevision");
        }
    }

    private static void ValidateTargetMatchesExpectedPublication(
        GovernedLoopRevisionLifecycleRequest request,
        List<GovernedLoopRevisionLifecycleValidationError> errors)
    {
        if (!SameRevision(request.TargetRevision, request.ExpectedPublishedRevision?.Revision))
        {
            Add(errors, GovernedLoopRevisionLifecycleValidationErrorCode.InvalidLifecycleExpectation, "targetRevision");
        }
    }

    private static void ValidateIdentifier(
        string? value,
        string path,
        List<GovernedLoopRevisionLifecycleValidationError> errors)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(value, GovernedLoopRevisionContractLimits.MaxIdentifierCharacters))
        {
            Add(errors, GovernedLoopRevisionLifecycleValidationErrorCode.InvalidIdentifier, path);
        }
    }

    private static void ValidateReference(
        GovernedLoopRevisionReference? revision,
        string graphId,
        string path,
        bool required,
        List<GovernedLoopRevisionLifecycleValidationError> errors)
    {
        if (revision is null)
        {
            if (required)
            {
                Add(errors, GovernedLoopRevisionLifecycleValidationErrorCode.RequiredReferenceMissing, path);
            }

            return;
        }

        try
        {
            _ = GovernedLoopRevisionReference.Create(
                revision.SchemaVersion,
                revision.GraphId,
                revision.RevisionId,
                revision.ExecutableHash);
        }
        catch (ArgumentException)
        {
            Add(errors, GovernedLoopRevisionLifecycleValidationErrorCode.InvalidReference, path);
            return;
        }

        if (!string.Equals(revision.GraphId, graphId, StringComparison.Ordinal))
        {
            Add(errors, GovernedLoopRevisionLifecycleValidationErrorCode.GraphMismatch, path);
        }
    }

    private static void ValidatePublication(
        GovernedLoopRevisionPublicationPin? publication,
        string graphId,
        string path,
        bool required,
        List<GovernedLoopRevisionLifecycleValidationError> errors)
    {
        if (publication is null)
        {
            if (required)
            {
                Add(errors, GovernedLoopRevisionLifecycleValidationErrorCode.RequiredReferenceMissing, path);
            }

            return;
        }

        ValidateReference(publication.Revision, graphId, path + ".revision", true, errors);
        if (publication.SchemaVersion != GovernedLoopRevisionContractLimits.CurrentSchemaVersion
            || !CustomLoopArtifactIdentifier.IsValid(publication.PublicationOperationId, GovernedLoopRevisionContractLimits.MaxIdentifierCharacters)
            || !IsSha256(publication.ValidationEvidenceHash))
        {
            Add(errors, GovernedLoopRevisionLifecycleValidationErrorCode.InvalidReference, path);
        }
    }

    private static void Require(
        object? value,
        string path,
        List<GovernedLoopRevisionLifecycleValidationError> errors)
    {
        if (value is null)
        {
            Add(errors, GovernedLoopRevisionLifecycleValidationErrorCode.RequiredReferenceMissing, path);
        }
    }

    private static void Reject(
        object? value,
        string path,
        List<GovernedLoopRevisionLifecycleValidationError> errors)
    {
        if (value is not null)
        {
            Add(errors, GovernedLoopRevisionLifecycleValidationErrorCode.UnexpectedReference, path);
        }
    }

    private static bool SameRevision(GovernedLoopRevisionReference? left, GovernedLoopRevisionReference? right)
        => left is not null && right is not null
            && string.Equals(left.GraphId, right.GraphId, StringComparison.Ordinal)
            && string.Equals(left.RevisionId, right.RevisionId, StringComparison.Ordinal)
            && string.Equals(left.ExecutableHash, right.ExecutableHash, StringComparison.Ordinal);

    private static bool SameRevisionIdentity(GovernedLoopRevisionReference? left, GovernedLoopRevisionReference? right)
        => left is not null && right is not null
            && string.Equals(left.GraphId, right.GraphId, StringComparison.Ordinal)
            && string.Equals(left.RevisionId, right.RevisionId, StringComparison.Ordinal);

    private static bool IsSha256(string? value)
        => value is { Length: GovernedLoopRevisionContractLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Add(
        List<GovernedLoopRevisionLifecycleValidationError> errors,
        GovernedLoopRevisionLifecycleValidationErrorCode code,
        string path)
    {
        if (errors.Count < GovernedLoopRevisionContractLimits.MaxValidationErrors)
        {
            errors.Add(new GovernedLoopRevisionLifecycleValidationError(code, path));
        }
    }
}
