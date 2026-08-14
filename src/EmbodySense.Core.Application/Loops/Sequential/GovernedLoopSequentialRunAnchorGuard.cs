using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Fails closed unless exact admission, invocation, graph, workspace, and run evidence compose before dispatch.</summary>
public static class GovernedLoopSequentialRunAnchorGuard
{
    /// <summary>Validates every immutable coordinate and issues an unforgeable Application run anchor on success.</summary>
    public static GovernedLoopSequentialRunAnchorResult Create(
        GovernedLoopSequentialAdapterBinding? adapterBinding,
        GovernedLoopAdmissionRequest? admissionRequest,
        GovernedLoopAdmissionReceipt? admissionReceipt,
        GovernedLoopSequentialInvocationSnapshot? invocationSnapshot,
        GovernedLoopGraphRevisionArtifact? graphArtifact)
    {
        if (!GovernedLoopSequentialContractValidator.Validate(adapterBinding).IsValid)
        {
            return Failure(GovernedLoopSequentialRunAnchorStatus.InvalidAdapterBinding);
        }

        if (!IsValidRequest(admissionRequest))
        {
            return Failure(GovernedLoopSequentialRunAnchorStatus.InvalidAdmissionRequest);
        }

        if (!GovernedLoopAdmissionValidator.Validate(admissionReceipt).IsValid)
        {
            return Failure(GovernedLoopSequentialRunAnchorStatus.InvalidAdmissionReceipt);
        }

        if (!GovernedLoopSequentialContractValidator.Validate(invocationSnapshot).IsValid)
        {
            return Failure(GovernedLoopSequentialRunAnchorStatus.InvalidInvocationSnapshot);
        }

        if (!IsValidArtifact(graphArtifact))
        {
            return Failure(GovernedLoopSequentialRunAnchorStatus.InvalidGraphArtifact);
        }

        var binding = adapterBinding!;
        var request = admissionRequest!;
        var receipt = admissionReceipt!;
        var invocation = invocationSnapshot!;
        var artifact = graphArtifact!;
        if (invocation.ContextCapturedAtUtc > receipt.Evidence.EvaluatedAtUtc
            || invocation.ContextCapturedAtUtc > receipt.RecordedAtUtc)
        {
            return Failure(GovernedLoopSequentialRunAnchorStatus.AdmissionCausalityMismatch);
        }

        if (!string.Equals(binding.WorkspaceId, receipt.Intent.WorkspaceId, StringComparison.Ordinal))
        {
            return Failure(GovernedLoopSequentialRunAnchorStatus.WorkspaceMismatch);
        }

        if (!string.Equals(binding.AdmissionOperationId, request.OperationId, StringComparison.Ordinal)
            || !string.Equals(request.OperationId, receipt.Intent.OperationId, StringComparison.Ordinal))
        {
            return Failure(GovernedLoopSequentialRunAnchorStatus.OperationMismatch);
        }

        if (!string.Equals(binding.AdmissionRequestHash, request.RequestHash, StringComparison.Ordinal)
            || !string.Equals(request.RequestHash, receipt.Intent.RequestHash, StringComparison.Ordinal))
        {
            return Failure(GovernedLoopSequentialRunAnchorStatus.RequestMismatch);
        }

        if (!string.Equals(binding.AdmissionReceiptHash, receipt.ContentHash, StringComparison.Ordinal))
        {
            return Failure(GovernedLoopSequentialRunAnchorStatus.ReceiptMismatch);
        }

        if (!string.Equals(binding.InvocationPayloadHash, invocation.ContentHash, StringComparison.Ordinal)
            || !string.Equals(request.InvocationPayloadHash, invocation.ContentHash, StringComparison.Ordinal))
        {
            return Failure(GovernedLoopSequentialRunAnchorStatus.InvocationMismatch);
        }

        if (!Equals(binding.ExecutionBinding, receipt.Evidence.Binding)
            || !Equals(binding.ExecutionBinding.Revision, request.Publication.Revision)
            || !Equals(binding.ExecutionBinding.Revision, artifact.RevisionArtifact.Revision)
            || binding.ExecutionBinding.ExecutionGeneration != 1)
        {
            return Failure(GovernedLoopSequentialRunAnchorStatus.RunBindingMismatch);
        }

        if (!string.Equals(binding.GraphArtifactHash, artifact.ArtifactHash, StringComparison.Ordinal)
            || !string.Equals(receipt.Intent.GraphArtifactHash, artifact.ArtifactHash, StringComparison.Ordinal))
        {
            return Failure(GovernedLoopSequentialRunAnchorStatus.GraphArtifactMismatch);
        }

        if (!string.Equals(binding.GraphLayoutHash, artifact.LayoutHash, StringComparison.Ordinal)
            || !string.Equals(receipt.Intent.GraphLayoutHash, artifact.LayoutHash, StringComparison.Ordinal))
        {
            return Failure(GovernedLoopSequentialRunAnchorStatus.GraphLayoutMismatch);
        }

        if (!Equals(receipt.Intent.Role, artifact.Graph.OwningRole))
        {
            return Failure(GovernedLoopSequentialRunAnchorStatus.RoleMismatch);
        }

        if (!Equals(receipt.Intent.Publication, request.Publication)
            || !Equals(receipt.Intent.AuthorityGrant, request.AuthorityGrant)
            || !Equals(receipt.Intent.ActorId, request.ActorId)
            || !string.Equals(receipt.Intent.Surface, request.Surface, StringComparison.Ordinal))
        {
            return Failure(GovernedLoopSequentialRunAnchorStatus.AdmissionCoordinateMismatch);
        }

        return new GovernedLoopSequentialRunAnchorResult(
            GovernedLoopSequentialRunAnchorStatus.Ready,
            new GovernedLoopSequentialRunAnchor(binding, invocation));
    }

    private static bool IsValidRequest(GovernedLoopAdmissionRequest? request)
        => request is not null
            && request.SchemaVersion == GovernedLoopAdmissionRequest.CurrentSchemaVersion
            && IsToken(request.OperationId, GovernedLoopAdmissionLimits.MaxIdentifierCharacters)
            && IsHash(request.InvocationPayloadHash)
            && GovernedLoopAdmissionRequestHash.Matches(request)
            && GovernedLoopRevisionContractValidator.Validate(request.Publication).IsValid
            && request.AuthorityGrant?.GrantId is not null
            && request.AuthorityGrant.Revision is not null
            && IsOciHash(request.AuthorityGrant.ContentHash)
            && request.ActorId is not null
            && AuthorityActorId.TryParse(request.ActorId.Value, out var actor, out _)
            && request.ActorId.Equals(actor)
            && IsToken(request.Surface, GovernedLoopAdmissionLimits.MaxSurfaceCharacters);

    private static bool IsValidArtifact(GovernedLoopGraphRevisionArtifact? artifact)
    {
        if (artifact is null)
        {
            return false;
        }

        try
        {
            return string.Equals(GovernedLoopGraphRevisionContractHash.ComputeArtifactHash(artifact), artifact.ArtifactHash, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsToken(string? value, int maximum)
        => !string.IsNullOrEmpty(value)
            && value.Length <= maximum
            && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.');

    private static bool IsHash(string? value)
        => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsOciHash(string? value)
        => value is { Length: 71 }
            && value.StartsWith("sha256:", StringComparison.Ordinal)
            && IsHash(value[7..]);

    private static GovernedLoopSequentialRunAnchorResult Failure(GovernedLoopSequentialRunAnchorStatus status)
        => new(status, null);
}
