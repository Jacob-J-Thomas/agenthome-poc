using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanInput.Lifecycle;
using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Responses;

public sealed class HumanInputResponseLifecycleContractTests
{
    [Fact]
    public async Task Command_hash_binds_every_response_intent_field()
    {
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync();
        var request = harness.Request;
        var head = harness.Store.CurrentSnapshot!.Request.Head;
        var baseline = HumanInputResponseLifecycleTestData.Submit(request, head, "hash-all-fields", "response-one", explanation: "baseline");
        var target = new HumanInputResponseReference(1, "target-one", baseline.ExpectedRequest, HumanInputResponseLifecycleTestData.Hash('b'), HumanInputResponseLifecycleTestData.Hash('c'));
        var variants = new[]
        {
            baseline with { OperationId = "hash-other-operation" },
            baseline with { Kind = HumanInputResponseOperationKind.Withdraw, ResponseId = null, Value = null, Explanation = null, TargetResponses = [target] },
            baseline with { RequestId = "other-request", ExpectedRequest = baseline.ExpectedRequest with { RequestId = "other-request" } },
            baseline with { ExpectedLifecycleVersion = baseline.ExpectedLifecycleVersion + 1 },
            baseline with { ExpectedLifecycleStatus = HumanInputRequestLifecycleStatus.Cancelled },
            baseline with { ExpectedRequest = baseline.ExpectedRequest with { RequestVersionId = "other-version" } },
            baseline with { ExpectedBinding = baseline.ExpectedBinding with { RunId = "other-run" } },
            baseline with { ResponseId = "response-two" },
            baseline with { Value = HumanInputResponseLifecycleTestData.Text("other value") },
            baseline with { Explanation = "other explanation" },
        };

        Assert.All(variants, variant => Assert.NotEqual(baseline.CommandHash, HumanInputResponseLifecycleCommandHash.Compute(variant)));
    }

    [Fact]
    public async Task Command_hash_rejects_malformed_unicode_and_noncanonical_serialized_fields()
    {
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync();
        var baseline = HumanInputResponseLifecycleTestData.Submit(
            harness.Request,
            harness.Store.CurrentSnapshot!.Request.Head,
            "hash-malformed-text",
            "response-one");
        var malformedExplanation = baseline with { Explanation = "private\ud800text", CommandHash = string.Empty };
        var malformedIdentifier = baseline with { ResponseId = "response\ud800one", CommandHash = string.Empty };
        var uppercaseHash = baseline with { ExpectedRequest = baseline.ExpectedRequest with { RequestHash = HumanInputResponseLifecycleTestData.Hash('A') }, CommandHash = string.Empty };

        Assert.Throws<ArgumentException>(() => HumanInputResponseLifecycleCommandHash.Compute(malformedExplanation));
        Assert.Throws<ArgumentException>(() => HumanInputResponseLifecycleCommandHash.Apply(malformedIdentifier));
        Assert.Throws<ArgumentException>(() => HumanInputResponseLifecycleCommandHash.Compute(uppercaseHash));
        Assert.False(HumanInputResponseLifecycleCommandHash.Matches(malformedExplanation with { CommandHash = baseline.CommandHash }));
    }

    [Fact]
    public async Task Command_validator_reports_bounded_value_free_errors()
    {
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync();
        var valid = HumanInputResponseLifecycleTestData.Submit(
            harness.Request,
            harness.Store.CurrentSnapshot!.Request.Head,
            "validate-command",
            "response-one");

        Assert.Empty(HumanInputResponseLifecycleCommandValidator.Validate(valid));
        Assert.Equal(
            HumanInputResponseLifecycleMutationValidationErrorCode.CommandRequired,
            Assert.Single(HumanInputResponseLifecycleCommandValidator.Validate(null)).Code);
        var errors = HumanInputResponseLifecycleCommandValidator.Validate(valid with
        {
            SchemaVersion = 2,
            OperationId = "INVALID",
            ExpectedLifecycleStatus = HumanInputRequestLifecycleStatus.Cancelled,
            CommandHash = HumanInputResponseLifecycleTestData.Hash('f'),
        });
        Assert.Contains(errors, error => error.Code == HumanInputResponseLifecycleMutationValidationErrorCode.UnsupportedSchemaVersion);
        Assert.Contains(errors, error => error.Code == HumanInputResponseLifecycleMutationValidationErrorCode.InvalidIdentifier);
        Assert.Contains(errors, error => error.Code == HumanInputResponseLifecycleMutationValidationErrorCode.InvalidExpectedState);
        Assert.Contains(errors, error => error.Code == HumanInputResponseLifecycleMutationValidationErrorCode.InvalidCommandHash);
        Assert.DoesNotContain("valid response", string.Join('|', errors.Select(error => error.Message)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authentication_contracts_never_format_response_actor_or_evidence_secrets()
    {
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync();
        var command = HumanInputResponseLifecycleTestData.Submit(
            harness.Request,
            harness.Store.CurrentSnapshot!.Request.Head,
            "auth-redaction",
            "response-one",
            HumanInputResponseLifecycleTestData.Text("private response value"),
            "private explanation");
        var request = new HumanInputResponseActorAuthenticationRequest(
            command.OperationId,
            command.Kind,
            command.RequestId,
            command.CommandHash,
            "workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            HumanInputResponseLifecycleTestData.Now);
        var authentication = new HumanInputResponseActorAuthentication(
            HumanInputResponseActorAuthenticationStatus.Authenticated,
            command.OperationId,
            command.CommandHash,
            "workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            HumanInputResponseLifecycleTestData.Now,
            HumanInputResponseLifecycleTestData.Actor("secret-actor"),
            HumanInputResponseLifecycleTestData.Hash('e'));

        Assert.DoesNotContain("private response value", request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private explanation", request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-actor", authentication.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(HumanInputResponseLifecycleTestData.Hash('e'), authentication.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Lifecycle_command_hash_binds_roles_counts_and_ordered_policy_roles()
    {
        var baselineRequest = HumanInputResponseLifecycleTestData.Request(
            HumanInputResponsePolicyKind.ManualSelection,
            orderedRoleIds: ["role-one", "role-two"]);
        var baseline = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Create,
            "lifecycle-response-policy-hash",
            baselineRequest.RequestId,
            null,
            baselineRequest);
        var changedRole = HumanInputRequestHash.Apply(baselineRequest with
        {
            EligibleRespondents = baselineRequest.EligibleRespondents
                .Select((respondent, index) => index == 0 ? respondent with { RespondentRoleId = "role-changed" } : respondent)
                .ToArray(),
            ResponsePolicy = new HumanInputResponsePolicy(HumanInputResponsePolicyKind.ManualSelection, null, ["role-changed", "role-two"]),
            RequestHash = string.Empty,
        });
        var reversedRoles = HumanInputRequestHash.Apply(baselineRequest with
        {
            ResponsePolicy = new HumanInputResponsePolicy(HumanInputResponsePolicyKind.ManualSelection, null, ["role-two", "role-one"]),
            RequestHash = string.Empty,
        });
        var quorumTwo = HumanInputResponseLifecycleTestData.Request(HumanInputResponsePolicyKind.Quorum, 2);
        var quorumThree = HumanInputRequestHash.Apply(quorumTwo with
        {
            ResponsePolicy = new HumanInputResponsePolicy(HumanInputResponsePolicyKind.Quorum, 3, null),
            RequestHash = string.Empty,
        });

        var baselineHash = HumanInputRequestLifecycleCommandHash.Compute(baseline);
        Assert.NotEqual(baselineHash, HumanInputRequestLifecycleCommandHash.Compute(baseline with { CandidateRequest = changedRole }));
        Assert.NotEqual(baselineHash, HumanInputRequestLifecycleCommandHash.Compute(baseline with { CandidateRequest = reversedRoles }));
        var quorumCommand = baseline with { CandidateRequest = quorumTwo };
        Assert.NotEqual(
            HumanInputRequestLifecycleCommandHash.Compute(quorumCommand),
            HumanInputRequestLifecycleCommandHash.Compute(quorumCommand with { CandidateRequest = quorumThree }));
    }

    [Fact]
    public void Store_snapshot_defensively_copies_exact_response_reference_and_collections()
    {
        var request = HumanInputResponseLifecycleTestData.Request();
        var head = new HumanInputRequestLifecycleHead(
            1,
            request.RequestId,
            1,
            HumanInputRequestLifecycleStatus.Pending,
            HumanInputResponseLifecycleTestData.Reference(request),
            0,
            null,
            null,
            "create-request",
            HumanInputResponseLifecycleTestData.Now);
        var lifecycle = new EmbodySense.Core.Application.HumanInput.Lifecycle.Models.HumanInputRequestLifecycleStoreSnapshot(head, [request], []);
        var responses = new List<EmbodySense.Core.Common.HumanInput.Responses.Models.HumanInputResponseArtifact>();
        var operations = new List<HumanInputResponseOperationEvidence>();
        var snapshot = new HumanInputResponseLifecycleStoreSnapshot(lifecycle, head.CurrentRequest, responses, operations, null);

        responses.Clear();
        operations.Clear();
        Assert.Equal(head.CurrentRequest, snapshot.ResponseRequest);
        Assert.Empty(snapshot.Responses);
        Assert.Empty(snapshot.Operations);
        Assert.Contains("ResponseVersionId", snapshot.ToString(), StringComparison.Ordinal);
        Assert.False(HumanInputResponseOperationCausality.Matches(null, snapshot));
        Assert.False(HumanInputResponseOperationCausality.Matches(null, null, snapshot));
    }
}
