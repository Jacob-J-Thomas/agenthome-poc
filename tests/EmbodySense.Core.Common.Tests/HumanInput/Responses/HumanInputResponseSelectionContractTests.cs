using System.Collections.Immutable;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Common.Tests.HumanInput.Responses;

public sealed class HumanInputResponseSelectionContractTests
{
    public static TheoryData<HumanInputResponsePolicyKind, int?, ImmutableArray<string>?> EveryPolicy => new()
    {
        { HumanInputResponsePolicyKind.FirstValid, null, null },
        { HumanInputResponsePolicyKind.Quorum, 2, null },
        { HumanInputResponsePolicyKind.NamedRoles, null, ImmutableArray.Create("role-one") },
        { HumanInputResponsePolicyKind.Merge, 1, ImmutableArray.Create("role-one") },
        { HumanInputResponsePolicyKind.ManualSelection, null, ImmutableArray.Create("role-selector") }
    };

    [Fact]
    public void First_valid_selects_the_earliest_active_response_in_durable_order()
    {
        var request = HumanInputResponseTestData.Request();
        var first = HumanInputResponseTestData.Artifact(request, "response-two", "user-two", "role-two", "second-arrival");
        var second = HumanInputResponseTestData.Artifact(request, "response-one", "user-one", "role-one", "first-id-later");
        var valid = HumanInputResponseTestData.Selection(request, [first]);
        var later = HumanInputResponseTestData.Selection(request, [second]);

        Assert.True(HumanInputResponseContractValidator.ValidateSelection(request, valid, [first, second]).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateSelection(request, later, [first, second]).IsValid);
    }

    [Fact]
    public void Quorum_selects_the_first_distinct_actor_set_for_the_earliest_winning_hash()
    {
        var request = HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.Quorum, 2);
        var firstA = HumanInputResponseTestData.Artifact(request, "response-one", "user-one", "role-one", "value-a");
        var firstB = HumanInputResponseTestData.Artifact(request, "response-two", "user-two", "role-two", "value-b");
        var winningA = HumanInputResponseTestData.Artifact(request, "response-three", "user-three", "role-three", "value-a");
        var lateB = HumanInputResponseTestData.Artifact(request, "response-four", "selector-one", "role-selector", "value-b");
        var valid = HumanInputResponseTestData.Selection(request, [firstA, winningA]);
        var wrongHash = HumanInputResponseTestData.Selection(request, [firstB, lateB]);
        var wrongOrder = HumanInputResponseTestData.Selection(request, [winningA, firstA]);

        Assert.True(HumanInputResponseContractValidator.ValidateSelection(request, valid, [firstA, firstB, winningA, lateB]).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateSelection(request, wrongHash, [firstA, firstB, winningA, lateB]).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateSelection(request, wrongOrder, [firstA, firstB, winningA, lateB]).IsValid);
    }

    [Fact]
    public void Quorum_requires_distinct_actors_and_remains_pending_without_a_winner()
    {
        var request = HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.Quorum, 2);
        var first = HumanInputResponseTestData.Artifact(request, "response-one", "user-one", "role-one", "value-a");
        var duplicateActor = HumanInputResponseTestData.Artifact(request, "response-two", "user-one", "role-one", "value-a");
        var selection = HumanInputResponseTestData.Selection(request, [first, duplicateActor]);

        Assert.False(HumanInputResponseContractValidator.ValidateSelection(request, selection, [first, duplicateActor]).IsValid);
    }

    [Theory]
    [MemberData(nameof(EveryPolicy))]
    public void Every_policy_rejects_null_active_items_without_evaluating_policy(
        HumanInputResponsePolicyKind kind,
        int? requiredCount,
        ImmutableArray<string>? roles)
    {
        var request = HumanInputResponseTestData.Request(kind, requiredCount, roles);
        var artifact = HumanInputResponseTestData.Artifact(request);
        var selection = SelectionForPolicy(request, artifact);
        var validation = HumanInputResponseContractValidator.ValidateSelection(request, selection, new HumanInputResponseArtifact[] { null! });

        Assert.NotEmpty(validation.Errors);
        Assert.All(validation.Errors, error => Assert.StartsWith("$.activeResponses", error.Path, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(EveryPolicy))]
    public void Every_policy_rejects_duplicate_authenticated_actors_before_policy_evaluation(
        HumanInputResponsePolicyKind kind,
        int? requiredCount,
        ImmutableArray<string>? roles)
    {
        var request = HumanInputResponseTestData.Request(kind, requiredCount, roles);
        var first = HumanInputResponseTestData.Artifact(request, "response-one", "user-one", "role-one", "value-one");
        var duplicateActor = HumanInputResponseTestData.Artifact(request, "response-two", "user-one", "role-one", "value-two");
        var selection = SelectionForPolicy(request, first);
        var validation = HumanInputResponseContractValidator.ValidateSelection(request, selection, [first, duplicateActor]);

        Assert.NotEmpty(validation.Errors);
        Assert.All(validation.Errors, error => Assert.StartsWith("$.activeResponses", error.Path, StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_active_value_hash_and_actor_are_fully_rejected_before_policy_evaluation()
    {
        var request = HumanInputResponseTestData.Request();
        var artifact = HumanInputResponseTestData.Artifact(request);
        var selection = HumanInputResponseTestData.Selection(request, [artifact]);
        var invalidSets = new[]
        {
            new[] { artifact with { ValueHash = "bad" } },
            new[] { artifact with { ActorId = null! } }
        };

        Assert.All(invalidSets, active =>
        {
            var validation = HumanInputResponseContractValidator.ValidateSelection(request, selection, active);
            Assert.NotEmpty(validation.Errors);
            Assert.All(validation.Errors, error => Assert.StartsWith("$.activeResponses", error.Path, StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Active_response_ids_must_be_unique_independently_of_actor_and_value()
    {
        var request = HumanInputResponseTestData.Request();
        var first = HumanInputResponseTestData.Artifact(request, "response-one", "user-one", "role-one", "value-one");
        var duplicateId = HumanInputResponseTestData.Artifact(request, "response-one", "user-two", "role-two", "value-two");
        var selection = HumanInputResponseTestData.Selection(request, [first]);
        var validation = HumanInputResponseContractValidator.ValidateSelection(request, selection, [first, duplicateId]);

        Assert.Contains(validation.Errors, error => error.Path == "$.activeResponses[1]");
    }

    [Fact]
    public void Named_roles_select_every_required_role_in_authored_order()
    {
        var request = HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.NamedRoles, orderedRoleIds: ImmutableArray.Create("role-two", "role-one"));
        var one = HumanInputResponseTestData.Artifact(request, "response-one", "user-one", "role-one");
        var two = HumanInputResponseTestData.Artifact(request, "response-two", "user-two", "role-two", "different-data");
        var valid = HumanInputResponseTestData.Selection(request, [two, one]);
        var reversed = HumanInputResponseTestData.Selection(request, [one, two]);

        Assert.True(HumanInputResponseContractValidator.ValidateSelection(request, valid, [one, two]).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateSelection(request, reversed, [one, two]).IsValid);
    }

    [Fact]
    public void Merge_selects_all_active_configured_contributors_in_authored_order_without_synthesis()
    {
        var request = HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.Merge, 2, ImmutableArray.Create("role-three", "role-one", "role-two"));
        var one = HumanInputResponseTestData.Artifact(request, "response-one", "user-one", "role-one", "private-one");
        var two = HumanInputResponseTestData.Artifact(request, "response-two", "user-two", "role-two", "private-two");
        var three = HumanInputResponseTestData.Artifact(request, "response-three", "user-three", "role-three", "private-three");
        var thresholdSelection = HumanInputResponseTestData.Selection(request, [one, two]);
        var allSelection = HumanInputResponseTestData.Selection(request, [three, one, two]);
        var omittedActive = HumanInputResponseTestData.Selection(request, [three, one]);

        Assert.True(HumanInputResponseContractValidator.ValidateSelection(request, thresholdSelection, [two, one]).IsValid);
        Assert.True(HumanInputResponseContractValidator.ValidateSelection(request, allSelection, [two, three, one]).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateSelection(request, omittedActive, [one, two, three]).IsValid);
        Assert.DoesNotContain("private-one", allSelection.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-two", allSelection.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_selection_requires_one_exact_active_response_and_authenticated_eligible_selector()
    {
        var request = HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.ManualSelection, orderedRoleIds: ImmutableArray.Create("role-selector"));
        var one = HumanInputResponseTestData.Artifact(request);
        var two = HumanInputResponseTestData.Artifact(request, "response-two", "user-two", "role-two");
        var valid = HumanInputResponseTestData.Selection(request, [one], selectorActorId: "selector-one", selectorRoleId: "role-selector");
        var multiple = HumanInputResponseTestData.Selection(request, [one, two], selectorActorId: "selector-one", selectorRoleId: "role-selector");
        var wrongRole = HumanInputResponseTestData.Selection(request, [one], selectorActorId: "selector-one", selectorRoleId: "role-one");
        var noSelector = HumanInputResponseTestData.Selection(request, [one]);

        Assert.True(HumanInputResponseContractValidator.ValidateSelection(request, valid, [one, two]).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateSelection(request, multiple, [one, two]).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateSelection(request, wrongRole, [one, two]).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateSelection(request, noSelector, [one, two]).IsValid);
    }

    [Fact]
    public void Selection_hash_is_order_sensitive_and_covers_policy_selector_time_and_references()
    {
        var request = HumanInputResponseTestData.Request(HumanInputResponsePolicyKind.ManualSelection, orderedRoleIds: ImmutableArray.Create("role-selector"));
        var one = HumanInputResponseTestData.Artifact(request);
        var two = HumanInputResponseTestData.Artifact(request, "response-two", "user-two", "role-two");
        var selection = HumanInputResponseTestData.Selection(request, [one], selectorActorId: "selector-one", selectorRoleId: "role-selector");
        var variants = new[]
        {
            selection with { SchemaVersion = 2 },
            selection with { SelectionId = "selection-two" },
            selection with { Request = selection.Request with { RequestVersionId = "version-two" } },
            selection with { PolicyKind = HumanInputResponsePolicyKind.FirstValid },
            selection with { Responses = ImmutableArray.Create(HumanInputResponseTestData.Reference(request, two)) },
            selection with { SelectorRoleId = "role-other" },
            selection with { SelectedAtUtc = selection.SelectedAtUtc.AddTicks(1) }
        };

        Assert.True(HumanInputResponseSelectionHash.Matches(selection));
        Assert.All(variants, variant => Assert.NotEqual(selection.SelectionHash, HumanInputResponseSelectionHash.Compute(variant)));
        Assert.Throws<ArgumentNullException>(() => HumanInputResponseSelectionHash.Compute(null!));
        Assert.Throws<ArgumentNullException>(() => HumanInputResponseSelectionHash.Apply(null!));
        Assert.False(HumanInputResponseSelectionHash.Matches(null));
    }

    [Fact]
    public void Selection_hash_rejects_null_and_oversized_nested_request_references_before_writing()
    {
        var request = HumanInputResponseTestData.Request();
        var artifact = HumanInputResponseTestData.Artifact(request);
        var selection = HumanInputResponseTestData.Selection(request, [artifact]);
        var reference = selection.Responses[0];
        var nullRequest = selection with { Responses = ImmutableArray.Create(reference with { Request = null! }) };
        var oversizedRequest = selection with
        {
            Responses = ImmutableArray.Create(reference with { Request = reference.Request with { RequestId = new string('r', HumanInputLimits.MaxIdentifierCharacters + 1) } })
        };
        var oversizedVersion = selection with
        {
            Responses = ImmutableArray.Create(reference with { Request = reference.Request with { RequestVersionId = new string('v', HumanInputLimits.MaxIdentifierCharacters + 1) } })
        };
        var oversizedHash = selection with
        {
            Responses = ImmutableArray.Create(reference with { Request = reference.Request with { RequestHash = new string('a', HumanInputLimits.Sha256HexCharacters + 1) } })
        };

        Assert.Throws<ArgumentException>(() => HumanInputResponseSelectionHash.Compute(nullRequest));
        Assert.Throws<ArgumentException>(() => HumanInputResponseSelectionHash.Compute(oversizedRequest));
        Assert.Throws<ArgumentException>(() => HumanInputResponseSelectionHash.Compute(oversizedVersion));
        Assert.Throws<ArgumentException>(() => HumanInputResponseSelectionHash.Compute(oversizedHash));
        Assert.False(HumanInputResponseSelectionHash.Matches(nullRequest));
        Assert.False(HumanInputResponseSelectionHash.Matches(oversizedRequest));
        Assert.False(HumanInputResponseSelectionHash.Matches(oversizedVersion));
        Assert.False(HumanInputResponseSelectionHash.Matches(oversizedHash));
    }

    [Fact]
    public void Selection_hash_rejects_malformed_nested_strings_and_matches_fails_closed()
    {
        var request = HumanInputResponseTestData.Request(
            HumanInputResponsePolicyKind.ManualSelection,
            orderedRoleIds: ImmutableArray.Create("role-selector"));
        var artifact = HumanInputResponseTestData.Artifact(request);
        var selection = HumanInputResponseTestData.Selection(
            request,
            [artifact],
            selectorActorId: "selector-one",
            selectorRoleId: "role-selector");
        var reference = selection.Responses[0];
        var variants = new[]
        {
            selection with { SelectionId = new string('s', HumanInputLimits.MaxIdentifierCharacters + 1) },
            selection with { Request = selection.Request with { RequestId = "\uD800" } },
            selection with { Request = selection.Request with { RequestVersionId = "Version-One" } },
            selection with { Request = selection.Request with { RequestHash = "bad" } },
            selection with { Responses = ImmutableArray.Create(reference with { ResponseId = "response/unsafe" }) },
            selection with { Responses = ImmutableArray.Create(reference with { Request = reference.Request with { RequestId = "\uDC00" } }) },
            selection with { Responses = ImmutableArray.Create(reference with { ValueHash = "bad" }) },
            selection with { Responses = ImmutableArray.Create(reference with { ResponseHash = "bad" }) },
            selection with { SelectorRoleId = "e\u0301" },
            selection with { SelectorRoleId = new string('r', HumanInputLimits.MaxIdentifierCharacters + 1) },
            selection with { Responses = default }
        };

        Assert.All(variants, variant =>
        {
            Assert.Throws<ArgumentException>(() => HumanInputResponseSelectionHash.Compute(variant));
            Assert.Throws<ArgumentException>(() => HumanInputResponseSelectionHash.Apply(variant));
            Assert.False(HumanInputResponseSelectionHash.Matches(variant));
        });
    }

    [Fact]
    public void Selection_hash_accepts_exact_nested_identifier_boundaries()
    {
        var request = HumanInputResponseTestData.Request();
        var artifact = HumanInputResponseTestData.Artifact(request);
        var selection = HumanInputResponseTestData.Selection(request, [artifact]);
        var maximum = new string('r', HumanInputLimits.MaxIdentifierCharacters);
        var boundary = selection with
        {
            SelectionId = maximum,
            Request = selection.Request with { RequestId = maximum, RequestVersionId = maximum },
            Responses = ImmutableArray.Create(selection.Responses[0] with
            {
                ResponseId = maximum,
                Request = selection.Responses[0].Request with { RequestId = maximum, RequestVersionId = maximum }
            }),
            SelectionHash = string.Empty
        };
        var hashed = HumanInputResponseSelectionHash.Apply(boundary);

        Assert.True(HumanInputResponseSelectionHash.Matches(hashed));
    }

    [Fact]
    public void Selection_validation_rejects_duplicates_missing_active_refs_future_time_hash_and_cross_request()
    {
        var request = HumanInputResponseTestData.Request();
        var artifact = HumanInputResponseTestData.Artifact(request);
        var selection = HumanInputResponseTestData.Selection(request, [artifact]);
        var variants = new[]
        {
            selection with { SchemaVersion = 2 },
            selection with { Responses = ImmutableArray.Create(selection.Responses[0], selection.Responses[0]) },
            selection with { Responses = ImmutableArray.Create(selection.Responses[0] with { ResponseId = "missing" }) },
            selection with { SelectedAtUtc = request.Timing.ExpiresAtUtc.AddTicks(1) },
            selection with { SelectedAtUtc = selection.SelectedAtUtc.ToOffset(TimeSpan.FromHours(1)) },
            selection with { SelectionHash = HumanInputResponseTestData.Hash('d') },
            selection with { Request = selection.Request with { RequestHash = HumanInputResponseTestData.Hash('d') } }
        };

        Assert.All(variants, variant => Assert.False(HumanInputResponseContractValidator.ValidateSelection(request, variant, [artifact]).IsValid));
        Assert.False(HumanInputResponseContractValidator.ValidateSelection(request, null, [artifact]).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateSelection(null, selection, [artifact]).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateSelection(request, selection, null).IsValid);
    }

    [Fact]
    public void Selection_snapshot_and_reference_are_deep_exact_and_privacy_safe()
    {
        var request = HumanInputResponseTestData.Request();
        var artifact = HumanInputResponseTestData.Artifact(request, text: "value-canary", explanation: "explanation-canary");
        var selection = HumanInputResponseTestData.Selection(request, [artifact]);

        Assert.True(HumanInputResponseSelectionSnapshot.TryCapture(request, selection, [artifact], out var snapshot, out var validation));
        Assert.True(validation.IsValid);
        Assert.NotSame(selection, snapshot);
        Assert.False(selection.Responses.Equals(snapshot!.Responses));
        var reference = HumanInputResponseSelectionReference.Create(selection);
        Assert.True(reference.Matches(selection));
        Assert.True(HumanInputResponseContractValidator.ValidateSelectionReference(reference).IsValid);
        Assert.DoesNotContain("value-canary", selection.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("explanation-canary", reference.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => HumanInputResponseSelectionReference.Create(null!));
        Assert.False(reference.Matches(null));

        var boundedButInvalid = HumanInputResponseSelectionHash.Apply(selection with { SchemaVersion = 2, SelectionHash = string.Empty });
        Assert.False(HumanInputResponseSelectionSnapshot.TryCapture(request, boundedButInvalid, [artifact], out var invalidSnapshot, out var invalidValidation));
        Assert.Null(invalidSnapshot);
        Assert.False(invalidValidation.IsValid);
        Assert.False(HumanInputResponseSelectionSnapshot.TryCapture(request, selection with { Responses = default }, [artifact], out _, out var unboundedValidation));
        Assert.False(unboundedValidation.IsValid);
    }

    [Fact]
    public void Selection_reference_validation_and_result_bounds_fail_closed()
    {
        var malformed = new HumanInputResponseSelectionReference(2, "Invalid", null!, "bad");
        var source = Enumerable.Range(0, HumanInputResponseContractLimits.MaxValidationErrors + 4)
            .Select(_ => new HumanInputResponseValidationError(HumanInputResponseValidationErrorCode.InvalidSelectionShape, "$", "Value-free error."))
            .ToList();
        var result = new HumanInputResponseValidationResult(source);
        source.Clear();

        Assert.False(HumanInputResponseContractValidator.ValidateSelectionReference(malformed).IsValid);
        Assert.False(HumanInputResponseContractValidator.ValidateSelectionReference(null).IsValid);
        Assert.Equal(HumanInputResponseContractLimits.MaxValidationErrors, result.Errors.Count);
        Assert.Throws<ArgumentNullException>(() => new HumanInputResponseValidationResult(null!));
    }

    private static HumanInputResponseSelection SelectionForPolicy(HumanInputRequest request, HumanInputResponseArtifact artifact)
        => request.ResponsePolicy.Kind == HumanInputResponsePolicyKind.ManualSelection
            ? HumanInputResponseTestData.Selection(request, [artifact], selectorActorId: "selector-one", selectorRoleId: "role-selector")
            : HumanInputResponseTestData.Selection(request, [artifact]);
}
