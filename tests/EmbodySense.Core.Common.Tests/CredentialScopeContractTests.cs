using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Tests;

public sealed class CredentialScopeContractTests
{
    [Fact]
    public void Intersection_only_narrows_all_dimensions_and_round_trips_canonically()
    {
        var broad = CredentialContractTestData.Scope(role: null, loop: null, revision: null, node: null, target: null, operation: null, actor: null, notBefore: CredentialContractTestData.Now.AddDays(-1), notAfter: CredentialContractTestData.Now.AddDays(1));
        var narrow = CredentialContractTestData.Scope();

        Assert.True(CredentialScopeRules.TryIntersect(broad, narrow, out var intersection, out var error), error?.Message);
        Assert.Equal(narrow, intersection);
        Assert.True(CredentialScopeRules.IsNarrowerThanOrEqual(intersection, broad));
        Assert.True(CredentialScopeRules.IsNarrowerThanOrEqual(intersection, narrow));
        Assert.False(CredentialScopeRules.IsNarrowerThanOrEqual(broad, narrow));

        Assert.True(CredentialContractJson.TrySerialize(intersection, out var json, out _));
        Assert.True(CredentialContractJson.TryDeserializeScope(json, out var parsed, out _));
        Assert.Equal(intersection, parsed);
    }

    [Theory]
    [InlineData("workspace-2", "role-1", "loop-1", "api.example.com")]
    [InlineData("workspace-1", "role-2", "loop-1", "api.example.com")]
    [InlineData("workspace-1", "role-1", "loop-2", "api.example.com")]
    [InlineData("workspace-1", "role-1", "loop-1", "other.example.com")]
    public void Conflicting_cross_scope_dimensions_fail_closed(string workspace, string role, string loop, string target)
    {
        var left = CredentialContractTestData.Scope();
        var right = CredentialContractTestData.Scope(workspace: workspace, role: role, loop: loop, target: target);

        Assert.False(CredentialScopeRules.TryIntersect(left, right, out var intersection, out var error));
        Assert.Null(intersection);
        Assert.Equal(CredentialContractErrorCode.CredentialScopeConflict, error?.Code);
    }

    [Fact]
    public void Missing_ambiguous_and_nonoverlapping_scope_proof_fails_closed()
    {
        var missingWorkspace = CredentialContractTestData.Scope() with { WorkspaceId = null };
        var orphanLoop = CredentialContractTestData.Scope() with { RoleId = null };
        var orphanRevision = CredentialContractTestData.Scope() with { LoopId = null, LoopRevision = 4 };
        var orphanTarget = CredentialContractTestData.Scope() with { Service = null };
        var missingImplementation = CredentialContractTestData.Scope() with { Implementation = null };
        var noncanonicalImplementation = CredentialContractTestData.Scope();
        noncanonicalImplementation = noncanonicalImplementation with { Implementation = new CapabilityImplementationIdentity(noncanonicalImplementation.Implementation!.ProviderId, "http//call") };
        var future = CredentialContractTestData.Scope(notBefore: CredentialContractTestData.Now.AddHours(2), notAfter: CredentialContractTestData.Now.AddHours(3));
        var past = CredentialContractTestData.Scope(notBefore: CredentialContractTestData.Now.AddHours(-3), notAfter: CredentialContractTestData.Now.AddHours(-2));

        Assert.False(CredentialContractValidator.Validate(missingWorkspace).IsValid);
        Assert.Contains(CredentialContractValidator.Validate(orphanLoop).Errors, error => error.Code == CredentialContractErrorCode.AmbiguousLoopScope);
        Assert.False(CredentialContractValidator.Validate(orphanRevision).IsValid);
        Assert.False(CredentialContractValidator.Validate(orphanTarget).IsValid);
        Assert.False(CredentialContractValidator.Validate(missingImplementation).IsValid);
        Assert.Contains(CredentialContractValidator.Validate(noncanonicalImplementation).Errors, error => error.Code == CredentialContractErrorCode.InvalidCapabilityImplementation);
        Assert.False(CredentialScopeRules.TryIntersect(future, past, out _, out var error));
        Assert.Equal(CredentialContractErrorCode.CredentialScopeTimeConflict, error?.Code);
    }

    [Fact]
    public void Randomized_intersection_is_commutative_idempotent_and_monotone()
    {
        var random = new Random(2_140);
        for (var index = 0; index < 500; index++)
        {
            var baseScope = CredentialContractTestData.Scope(role: random.Next(2) == 0 ? null : "role-1", loop: null, revision: null, node: null, target: random.Next(2) == 0 ? null : "api.example.com", operation: null, actor: null, notBefore: CredentialContractTestData.Now.AddHours(-random.Next(2, 20)), notAfter: CredentialContractTestData.Now.AddHours(random.Next(2, 20)));
            var specific = CredentialContractTestData.Scope();
            Assert.True(CredentialScopeRules.TryIntersect(baseScope, specific, out var first, out _));
            Assert.True(CredentialScopeRules.TryIntersect(specific, baseScope, out var second, out _));
            Assert.Equal(first, second);
            Assert.True(CredentialScopeRules.TryIntersect(first, first, out var repeated, out _));
            Assert.Equal(first, repeated);
            Assert.True(CredentialScopeRules.IsNarrowerThanOrEqual(first, baseScope));
            Assert.True(CredentialScopeRules.IsNarrowerThanOrEqual(first, specific));
        }
    }
}
