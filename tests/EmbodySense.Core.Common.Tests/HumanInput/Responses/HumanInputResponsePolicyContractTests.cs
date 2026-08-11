using System.Collections.Immutable;
using System.Runtime.InteropServices;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Common.Tests.HumanInput.Responses;

public sealed class HumanInputResponsePolicyContractTests
{
    public static TheoryData<HumanInputResponsePolicyKind, int?, ImmutableArray<string>?> ValidPolicies => new()
    {
        { HumanInputResponsePolicyKind.FirstValid, null, null },
        { HumanInputResponsePolicyKind.Quorum, 2, null },
        { HumanInputResponsePolicyKind.NamedRoles, null, ImmutableArray.Create("role-two", "role-one") },
        { HumanInputResponsePolicyKind.Merge, 2, ImmutableArray.Create("role-three", "role-one", "role-two") },
        { HumanInputResponsePolicyKind.ManualSelection, null, ImmutableArray.Create("role-selector") }
    };

    [Theory]
    [MemberData(nameof(ValidPolicies))]
    public void Every_authored_policy_shape_validates_and_hashes(HumanInputResponsePolicyKind kind, int? requiredCount, ImmutableArray<string>? roles)
    {
        var request = HumanInputResponseTestData.Request(kind, requiredCount, roles);

        Assert.True(HumanInputValidator.ValidateRequest(request).IsValid);
        Assert.True(HumanInputRequestHash.Matches(request));
    }

    [Fact]
    public void Policy_validation_rejects_unknown_irrelevant_counts_and_unbounded_quorum()
    {
        var variants = new[]
        {
            HumanInputResponseTestData.Request() with { ResponsePolicy = new HumanInputResponsePolicy(HumanInputResponsePolicyKind.Unknown, null, null) },
            HumanInputResponseTestData.Request() with { ResponsePolicy = new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstValid, 1, null) },
            HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.Quorum, 1),
            HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.Quorum, 5),
            HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.Quorum, 2, ImmutableArray.Create("role-one"))
        };

        Assert.All(variants.Select(Rehash), request => Assert.False(HumanInputValidator.ValidateRequest(request).IsValid));
    }

    [Fact]
    public void Named_and_merge_roles_must_be_unique_existing_and_unambiguous()
    {
        var ambiguousRespondents = new[]
        {
            new HumanInputEligibleRespondent("user-one", "role-shared", "route-one"),
            new HumanInputEligibleRespondent("user-two", "role-shared", "route-two")
        };
        var variants = new[]
        {
            HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.NamedRoles, orderedRoleIds: ImmutableArray<string>.Empty),
            HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.NamedRoles, orderedRoleIds: ImmutableArray.Create("role-one", "role-one")),
            HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.NamedRoles, orderedRoleIds: ImmutableArray.Create("role-missing")),
            HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.NamedRoles, orderedRoleIds: ImmutableArray.Create("role-shared"), respondents: ambiguousRespondents),
            HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.Merge, 0, ImmutableArray.Create("role-one")),
            HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.Merge, 3, ImmutableArray.Create("role-one", "role-two")),
            HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.Merge, 1, ImmutableArray.Create("role-shared"), ambiguousRespondents)
        };

        Assert.All(variants, request => Assert.False(HumanInputValidator.ValidateRequest(request).IsValid));
    }

    [Fact]
    public void Manual_selector_roles_allow_multiple_actors_but_reject_duplicates_and_missing_roles()
    {
        var sharedSelectors = new[]
        {
            new HumanInputEligibleRespondent("selector-one", "role-selector", "route-one"),
            new HumanInputEligibleRespondent("selector-two", "role-selector", "route-two")
        };
        var valid = HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.ManualSelection, orderedRoleIds: ImmutableArray.Create("role-selector"), respondents: sharedSelectors);
        var duplicate = HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.ManualSelection, orderedRoleIds: ImmutableArray.Create("role-selector", "role-selector"), respondents: sharedSelectors);
        var missing = HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.ManualSelection, orderedRoleIds: ImmutableArray.Create("role-missing"), respondents: sharedSelectors);

        Assert.True(HumanInputValidator.ValidateRequest(valid).IsValid);
        Assert.False(HumanInputValidator.ValidateRequest(duplicate).IsValid);
        Assert.False(HumanInputValidator.ValidateRequest(missing).IsValid);
    }

    [Fact]
    public void Request_hash_covers_respondent_roles_and_every_policy_parameter_in_authored_order()
    {
        var original = HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.Merge, 2, ImmutableArray.Create("role-one", "role-two"));
        var variants = new[]
        {
            original with { EligibleRespondents = original.EligibleRespondents.Select(item => item.RespondentId == "user-one" ? item with { RespondentRoleId = "role-other" } : item).ToArray() },
            original with { ResponsePolicy = original.ResponsePolicy with { RequiredResponseCount = 1 } },
            original with { ResponsePolicy = original.ResponsePolicy with { OrderedRoleIds = ImmutableArray.Create("role-two", "role-one") } },
            original with { ResponsePolicy = original.ResponsePolicy with { Kind = HumanInputResponsePolicyKind.NamedRoles, RequiredResponseCount = null } }
        };

        Assert.All(variants, variant => Assert.NotEqual(original.RequestHash, HumanInputRequestHash.Compute(variant)));
    }

    [Fact]
    public void Request_hash_and_snapshot_fail_closed_before_enumerating_unbounded_or_default_policy_roles()
    {
        var roles = Enumerable.Range(0, HumanInputLimits.MaxResponsePolicyRoles + 1).Select(index => $"role-{index}").ToImmutableArray();
        var oversized = HumanInputResponseTestData.Request() with { ResponsePolicy = new HumanInputResponsePolicy(HumanInputResponsePolicyKind.NamedRoles, null, roles) };
        var defaultRoles = HumanInputResponseTestData.Request() with { ResponsePolicy = new HumanInputResponsePolicy(HumanInputResponsePolicyKind.NamedRoles, null, default(ImmutableArray<string>)) };

        Assert.Throws<ArgumentException>(() => HumanInputRequestHash.Compute(oversized));
        Assert.Throws<ArgumentException>(() => HumanInputRequestHash.Compute(defaultRoles));
        Assert.False(HumanInputRequestSnapshot.TryCapture(oversized, out _, out var oversizedValidation));
        Assert.False(HumanInputRequestSnapshot.TryCapture(defaultRoles, out _, out var defaultValidation));
        Assert.Contains(oversizedValidation.Errors, error => error.Code == "request_snapshot_unbounded");
        Assert.Contains(defaultValidation.Errors, error => error.Code == "request_snapshot_unbounded");
    }

    [Fact]
    public void Request_hash_rejects_malformed_policy_roles_before_serialization_and_accepts_exact_boundary()
    {
        var request = HumanInputResponseTestData.Request(
            HumanInputResponsePolicyKind.NamedRoles,
            orderedRoleIds: ImmutableArray.Create("role-one"));
        var maximum = new string('r', HumanInputLimits.MaxIdentifierCharacters);
        var boundary = request with
        {
            ResponsePolicy = request.ResponsePolicy with { OrderedRoleIds = ImmutableArray.Create(maximum) },
            RequestHash = string.Empty
        };
        var malformed = new[]
        {
            boundary with { ResponsePolicy = boundary.ResponsePolicy with { OrderedRoleIds = ImmutableArray.Create("\uD800") } },
            boundary with { ResponsePolicy = boundary.ResponsePolicy with { OrderedRoleIds = ImmutableArray.Create("\uDC00") } },
            boundary with { ResponsePolicy = boundary.ResponsePolicy with { OrderedRoleIds = ImmutableArray.Create("e\u0301") } },
            boundary with { ResponsePolicy = boundary.ResponsePolicy with { OrderedRoleIds = ImmutableArray.Create(new string('r', HumanInputLimits.MaxIdentifierCharacters + 1)) } }
        };

        Assert.NotEmpty(HumanInputRequestHash.Compute(boundary));
        Assert.All(malformed, candidate => Assert.Throws<ArgumentException>(() => HumanInputRequestHash.Compute(candidate)));
    }

    [Fact]
    public void Request_snapshot_deep_copies_policy_roles_and_respondent_role_bindings()
    {
        var callerOwnedRoles = new[] { "role-two", "role-one" };
        var request = HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.NamedRoles, orderedRoleIds: ImmutableCollectionsMarshal.AsImmutableArray(callerOwnedRoles));

        Assert.True(HumanInputRequestSnapshot.TryCapture(request, out var snapshot, out var validation));
        callerOwnedRoles[0] = "role-other";
        Assert.True(validation.IsValid);
        Assert.NotNull(snapshot);
        Assert.Equal("role-other", request.ResponsePolicy.OrderedRoleIds!.Value[0]);
        Assert.Equal("role-two", snapshot!.ResponsePolicy.OrderedRoleIds!.Value[0]);
        Assert.NotSame(request.EligibleRespondents, snapshot.EligibleRespondents);
        Assert.Equal("role-one", snapshot.EligibleRespondents[0].RespondentRoleId);
    }

    private static HumanInputRequest Rehash(HumanInputRequest request) => HumanInputRequestHash.Apply(request with { RequestHash = string.Empty });
}
