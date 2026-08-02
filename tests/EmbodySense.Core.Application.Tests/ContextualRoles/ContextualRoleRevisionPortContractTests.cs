using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Application.Tests.ContextualRoles;

public sealed class ContextualRoleRevisionPortContractTests
{
    [Fact]
    public void Read_and_mutation_models_preserve_exact_immutable_identity_without_authority_effects()
    {
        var identity = new ContextualRoleRevisionIdentity("reviewer", 3);
        var read = new ContextualRoleRevisionReadRequest(identity);
        var mutation = new ContextualRoleRevisionMutationRequest(CreateRevision(identity), identity);
        var readResult = new ContextualRoleRevisionReadResult(ContextualRoleRevisionReadStatus.NotFound, null, []);
        var mutationResult = new ContextualRoleRevisionMutationResult(ContextualRoleRevisionMutationStatus.Conflict, null, []);

        Assert.Same(identity, read.Identity);
        Assert.Same(identity, mutation.ExpectedPreviousIdentity);
        Assert.Same(identity, mutation.Revision.Identity);
        Assert.Equal(ContextualRoleRevisionReadStatus.NotFound, readResult.Status);
        Assert.Empty(readResult.ValidationErrors);
        Assert.Equal(ContextualRoleRevisionMutationStatus.Conflict, mutationResult.Status);
        Assert.Empty(mutationResult.ValidationErrors);
    }

    private static ContextualRoleRevision CreateRevision(ContextualRoleRevisionIdentity identity) => new(
        1,
        identity,
        new string('a', 64),
        "Reviewer",
        "Review.",
        ContextualRoleStatus.Draft,
        new ContextualRoleProvenance("user-jake", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
        new ContextualRoleWorkspaceApplicability([]),
        new ContextualRoleInstructionSourceReference(ContextualRoleInstructionSourceKind.RoleArtifact, "role-source", ContextualRoleInstructionClassification.RoleInstruction),
        new ContextualRolePolicyMaxima([]));
}
