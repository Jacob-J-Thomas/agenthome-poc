using System.Globalization;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Startup.Loops.Execution.Models;

namespace EmbodySense.Core.Startup.Loops.Execution;

/// <summary>Maps a closed primitive invocation transport into canonical immutable loop and grant references.</summary>
public static class GovernedLoopRunInvocationTransport
{
    /// <summary>Validates and maps one primitive transport request without granting authority.</summary>
    /// <param name="transport">The untrusted surface request.</param>
    /// <param name="input">The canonical invocation input when every immutable coordinate is valid.</param>
    /// <returns><see langword="true"/> when the complete transport maps to canonical schema-1 references; otherwise <see langword="false"/>.</returns>
    public static bool TryCreate(GovernedLoopRunInvocationTransportInput? transport, out GovernedLoopRunInvocationInput? input)
    {
        input = null;
        if (transport?.Publication is null
            || transport.AuthorityGrant is null
            || !AuthorityGrantId.TryParse(transport.AuthorityGrant.GrantId, out var grantId, out _)
            || !AuthorityGrantRevision.TryParse(transport.AuthorityGrant.Revision.ToString(CultureInfo.InvariantCulture), out var grantRevision, out _))
        {
            return false;
        }

        GovernedLoopRevisionReference revision;
        try
        {
            revision = GovernedLoopRevisionReference.Create(
                transport.Publication.RevisionSchemaVersion,
                transport.Publication.GraphId,
                transport.Publication.RevisionId,
                transport.Publication.ExecutableHash);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var publication = new GovernedLoopRevisionPublicationPin(
            transport.Publication.SchemaVersion,
            revision,
            transport.Publication.PublicationOperationId,
            transport.Publication.ValidationEvidenceHash);
        if (!GovernedLoopRevisionContractValidator.Validate(publication).IsValid)
        {
            return false;
        }

        input = new GovernedLoopRunInvocationInput(
            transport.OperationId,
            publication,
            new AuthorityGrantReference(grantId!, grantRevision!, transport.AuthorityGrant.ContentHash),
            transport.InvocationPrompt);
        return true;
    }

    /// <summary>Projects a canonical response into the bounded primitive interface contract.</summary>
    /// <param name="response">The canonical runtime response.</param>
    /// <returns>The transport-safe response without internal authority or persistence evidence.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="response"/> is <see langword="null"/>.</exception>
    public static GovernedLoopRunInvocationTransportResponse CreateResponse(GovernedLoopRunInvocationResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var run = response.Run is null
            ? null
            : new GovernedLoopRunTransportSnapshot(
                response.Run.Id,
                response.Run.Status,
                response.Run.FinalOutput,
                response.Run.FailureCode,
                response.Run.FailureDetail);
        return new GovernedLoopRunInvocationTransportResponse(
            response.Status,
            response.AdmissionStatus,
            response.AdmissionFailureCode,
            response.MaterializationStatus,
            response.ExecutionStatus,
            response.WasDispatched,
            run,
            response.Detail);
    }
}
