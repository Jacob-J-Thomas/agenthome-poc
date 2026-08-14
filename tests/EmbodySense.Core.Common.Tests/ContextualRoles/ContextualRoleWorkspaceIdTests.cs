using System.Collections.Immutable;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Common.Tests.ContextualRoles;

public sealed class ContextualRoleWorkspaceIdTests
{
    private static readonly string _workspaceId = "workspace-sha256:" + new string('a', ContextualRoleLimits.Sha256HexCharacters);

    [Fact]
    public void Only_the_exact_workspace_sha256_contract_is_valid()
    {
        Assert.True(ContextualRoleWorkspaceId.IsValid(_workspaceId));

        string?[] invalid =
        [
            null,
            string.Empty,
            "workspace-sha256:" + new string('a', ContextualRoleLimits.Sha256HexCharacters - 1),
            "workspace-sha256:" + new string('a', ContextualRoleLimits.Sha256HexCharacters + 1),
            "workspace-sha256:" + new string('A', ContextualRoleLimits.Sha256HexCharacters),
            "Workspace-sha256:" + new string('a', ContextualRoleLimits.Sha256HexCharacters),
            "workspace_sha256:" + new string('a', ContextualRoleLimits.Sha256HexCharacters),
            "workspace-sha256:" + new string('g', ContextualRoleLimits.Sha256HexCharacters),
        ];

        Assert.All(invalid, value => Assert.False(ContextualRoleWorkspaceId.IsValid(value)));
    }

    [Fact]
    public void Applicability_matches_only_exact_canonical_workspace_ids()
    {
        var applicability = new ContextualRoleWorkspaceApplicability([_workspaceId, "workspace-one"]);

        Assert.True(applicability.AppliesTo(_workspaceId));
        Assert.False(applicability.AppliesTo("workspace-one"));
        Assert.False(applicability.AppliesTo(null));
        Assert.False(new ContextualRoleWorkspaceApplicability(ImmutableArray<string>.Empty).AppliesTo(_workspaceId));
        Assert.False(new ContextualRoleWorkspaceApplicability(default).AppliesTo(_workspaceId));
    }
}
