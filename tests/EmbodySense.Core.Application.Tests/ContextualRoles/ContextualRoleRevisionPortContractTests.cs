using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Application.Tests.ContextualRoles;

public sealed class ContextualRoleRevisionPortContractTests
{
    [Fact]
    public void Read_and_mutation_models_preserve_exact_immutable_identity_without_authority_effects()
    {
        var identity = new ContextualRoleRevisionIdentity("reviewer", 3);
        var predecessor = new ContextualRoleRevisionIdentity("reviewer", 2);
        var read = new ContextualRoleRevisionReadRequest(identity);
        var mutation = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest("replace-reviewer", string.Empty, ContextualRoleRevisionMutationKind.Replace, "reviewer", "user-jake", CreateRevision(identity), predecessor, DateTimeOffset.UnixEpoch));
        var readResult = new ContextualRoleRevisionReadResult(ContextualRoleRevisionReadStatus.NotFound, null, ContextualRoleRevisionDisposition.Unknown, []);
        var mutationResult = new ContextualRoleRevisionMutationResult(ContextualRoleRevisionMutationStatus.Conflict, mutation.OperationId, mutation.RequestHash, mutation.Kind, null, null, []);
        var diagnostic = new ContextualRoleRevisionMutationDiagnostic(ContextualRolePersistenceDiagnosticStage.PublicationRename, ContextualRoleNativeErrorKind.Win32, 5);
        var unavailable = mutationResult with { Status = ContextualRoleRevisionMutationStatus.Unavailable, Diagnostic = diagnostic };

        Assert.Same(identity, read.Identity);
        Assert.Same(predecessor, mutation.ExpectedPreviousIdentity);
        Assert.Same(identity, mutation.Revision!.Identity);
        Assert.Equal(ContextualRoleRevisionReadStatus.NotFound, readResult.Status);
        Assert.Empty(readResult.ValidationErrors);
        Assert.Equal(ContextualRoleRevisionMutationStatus.Conflict, mutationResult.Status);
        Assert.Empty(mutationResult.ValidationErrors);
        Assert.Null(mutationResult.Diagnostic);
        Assert.Equal(diagnostic, unavailable.Diagnostic);
    }

    [Fact]
    public void Canonical_request_hash_binds_operation_kind_metadata_and_immutable_revision_content()
    {
        var revision = CreateRevision(new ContextualRoleRevisionIdentity("reviewer", 1));
        var request = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest("create-reviewer", string.Empty, ContextualRoleRevisionMutationKind.Create, "reviewer", "user-jake", revision, null, DateTimeOffset.UnixEpoch));

        Assert.True(ContextualRoleRevisionMutationRequestHash.Matches(request));
        Assert.False(ContextualRoleRevisionMutationRequestHash.Matches(request with { Revision = revision with { DisplayName = "Changed" } }));
        Assert.False(ContextualRoleRevisionMutationRequestHash.Matches(request with { RequestedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(1) }));
        Assert.False(ContextualRoleRevisionMutationRequestHash.Matches(request with { ActorId = "user-other" }));
        Assert.Empty(ContextualRoleRevisionMutationRequestValidator.Validate(request));
    }

    [Fact]
    public void Mutation_validation_keeps_revision_and_lifecycle_operations_explicit()
    {
        var identity = new ContextualRoleRevisionIdentity("reviewer", 1);
        var revision = CreateRevision(identity);
        var disable = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest("disable-reviewer", string.Empty, ContextualRoleRevisionMutationKind.Disable, "reviewer", "user-jake", revision, identity, DateTimeOffset.UnixEpoch));
        var replacement = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest("replace-reviewer", string.Empty, ContextualRoleRevisionMutationKind.Replace, "reviewer", "user-jake", revision with { Identity = new ContextualRoleRevisionIdentity("reviewer", 3) }, identity, DateTimeOffset.UnixEpoch));

        Assert.Contains(ContextualRoleRevisionMutationRequestValidator.Validate(disable), error => error.Code == "unexpected_revision");
        Assert.Contains(ContextualRoleRevisionMutationRequestValidator.Validate(replacement), error => error.Code == "nonsequential_replacement");
    }

    [Fact]
    public void Mutation_validation_reports_malformed_idempotency_lifecycle_and_predecessor_contracts()
    {
        var valid = CreateRevision(new ContextualRoleRevisionIdentity("reviewer", 1));
        var allMalformed = new ContextualRoleRevisionMutationRequest("../unsafe", "not-a-hash", ContextualRoleRevisionMutationKind.Unknown, "Reviewer", "../unsafe", null, new ContextualRoleRevisionIdentity("../unsafe", 0), default);
        var malformedCodes = ContextualRoleRevisionMutationRequestValidator.Validate(allMalformed).Select(error => error.Code).ToHashSet(StringComparer.Ordinal);
        var createWithPredecessor = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest("create-reviewer", string.Empty, ContextualRoleRevisionMutationKind.Create, "reviewer", "user-jake", ContextualRoleRevisionContentHash.Apply(valid with { Identity = new ContextualRoleRevisionIdentity("writer", 2), Status = ContextualRoleStatus.Disabled }), valid.Identity, DateTimeOffset.UnixEpoch));
        var missingPrevious = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest("disable-reviewer", string.Empty, ContextualRoleRevisionMutationKind.Disable, "reviewer", "user-jake", null, null, DateTimeOffset.UnixEpoch));
        var invalidPrevious = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest("disable-reviewer", string.Empty, ContextualRoleRevisionMutationKind.Disable, "reviewer", "user-jake", null, new ContextualRoleRevisionIdentity("../unsafe", 0), DateTimeOffset.UnixEpoch));
        var otherRole = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest("disable-reviewer", string.Empty, ContextualRoleRevisionMutationKind.Disable, "reviewer", "user-jake", null, new ContextualRoleRevisionIdentity("writer", 1), DateTimeOffset.UnixEpoch));
        var tamperedHash = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest("create-reviewer", string.Empty, ContextualRoleRevisionMutationKind.Create, "reviewer", "user-jake", valid, null, DateTimeOffset.UnixEpoch)) with { RequestHash = new string('a', 64) };

        Assert.Contains("invalid_operation_id", malformedCodes);
        Assert.Contains("invalid_role_id", malformedCodes);
        Assert.Contains("invalid_actor_id", malformedCodes);
        Assert.Contains("invalid_mutation_kind", malformedCodes);
        Assert.Contains("invalid_requested_at_utc", malformedCodes);
        Assert.Contains("invalid_request_hash", malformedCodes);
        Assert.Contains(ContextualRoleRevisionMutationRequestValidator.Validate(createWithPredecessor), error => error.Code == "role_identity_mismatch");
        Assert.Contains(ContextualRoleRevisionMutationRequestValidator.Validate(createWithPredecessor), error => error.Code == "invalid_revision_mutation_status");
        Assert.Contains(ContextualRoleRevisionMutationRequestValidator.Validate(createWithPredecessor), error => error.Code == "unexpected_previous_identity");
        Assert.Contains(ContextualRoleRevisionMutationRequestValidator.Validate(createWithPredecessor), error => error.Code == "invalid_initial_revision");
        Assert.Contains(ContextualRoleRevisionMutationRequestValidator.Validate(missingPrevious), error => error.Code == "previous_identity_required");
        Assert.Contains(ContextualRoleRevisionMutationRequestValidator.Validate(invalidPrevious), error => error.Code == "invalid_previous_identity");
        Assert.Contains(ContextualRoleRevisionMutationRequestValidator.Validate(otherRole), error => error.Code == "previous_role_mismatch");
        Assert.Contains(ContextualRoleRevisionMutationRequestValidator.Validate(tamperedHash), error => error.Code == "request_hash_mismatch");
        Assert.Contains(ContextualRoleRevisionMutationRequestValidator.Validate(null), error => error.Code == "request_required");
    }

    [Fact]
    public void Canonical_request_hash_rejects_uninitialized_collections_and_malformed_utf16()
    {
        var valid = CreateRevision(new ContextualRoleRevisionIdentity("reviewer", 1));
        var defaultScopes = valid with { WorkspaceApplicability = new ContextualRoleWorkspaceApplicability(default) };
        var malformedHigh = new ContextualRoleRevisionMutationRequest("bad\ud800", string.Empty, ContextualRoleRevisionMutationKind.Create, "reviewer", "user-jake", valid, null, DateTimeOffset.UnixEpoch);
        var malformedLow = malformedHigh with { OperationId = "bad\udc00" };
        var emoji = malformedHigh with { OperationId = "create-reviewer", Revision = valid with { DisplayName = "Reviewer 😀" } };

        Assert.Throws<ArgumentException>(() => ContextualRoleRevisionMutationRequestHash.Compute(malformedHigh));
        Assert.Throws<ArgumentException>(() => ContextualRoleRevisionMutationRequestHash.Compute(malformedLow));
        Assert.Throws<ArgumentException>(() => ContextualRoleRevisionMutationRequestHash.Compute(malformedHigh with { OperationId = "create-reviewer", Revision = defaultScopes }));
        Assert.Equal(64, ContextualRoleRevisionMutationRequestHash.Compute(emoji).Length);
        Assert.False(ContextualRoleRevisionMutationRequestHash.Matches(emoji));
    }

    private static ContextualRoleRevision CreateRevision(ContextualRoleRevisionIdentity identity)
    {
        var revision = new ContextualRoleRevision(
            1,
            identity,
            string.Empty,
            "Reviewer",
            "Review.",
            ContextualRoleStatus.Draft,
            new ContextualRoleProvenance("user-jake", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            new ContextualRoleWorkspaceApplicability(["workspace"]),
            new ContextualRoleInstructionSourceReference(ContextualRoleInstructionSourceKind.RoleArtifact, "role-source", ContextualRoleInstructionClassification.RoleInstruction),
            new ContextualRolePolicyMaxima([]));
        return ContextualRoleRevisionContentHash.Apply(revision);
    }
}
