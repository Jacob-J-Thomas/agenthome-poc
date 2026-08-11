using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.Tests.Governance.Authority.Grants;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

public sealed class HumanInputRequestLifecyclePrivacyTests
{
    [Fact]
    public async Task Public_commands_authority_contracts_and_results_omit_private_and_authority_values()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request();
        var command = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Create,
            "create-request-one",
            request.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(harness.Grant),
            request);
        var result = await harness.Service.MutateAsync(command);
        var authorizationRequest = Assert.Single(harness.Authorizer.Requests);
        var authorization = new HumanInputRequestLifecycleActorAuthorization(
            HumanInputRequestLifecycleActorAuthorizationStatus.Authorized,
            command.OperationId,
            command.RequestHash,
            "workspace-one",
            HumanInputRequestLifecycleTestData.Now,
            AuthorityGrantApplicationTestFixture.Actor("human-input-actor"),
            HumanInputRequestLifecycleTestData.Hash('a'));
        var committedMutation = Assert.Single(harness.Store.Commits).Mutation;
        var snapshot = harness.Store.Snapshot(request.RequestId)!;
        var stored = new HumanInputRequestLifecycleStoredOperation(request.RequestId, committedMutation.Operation);
        var portModels = new object[]
        {
            committedMutation,
            snapshot,
            stored,
            new HumanInputRequestLifecycleStoreReadResult(
                HumanInputRequestLifecycleStoreReadStatus.Ready,
                1,
                snapshot,
                null,
                stored),
            new HumanInputRequestLifecycleStoreCommitResult(
                HumanInputRequestLifecycleStoreCommitStatus.Committed,
                1,
                stored,
                snapshot,
                null),
            new HumanInputRequestLifecycleMutationValidationError(
                HumanInputRequestLifecycleMutationValidationErrorCode.InvalidExpectedState,
                "$.expectedRequest",
                "Value-free validation message."),
        };
        var rendered = new[]
        {
            command.ToString(),
            authorizationRequest.ToString(),
            authorization.ToString(),
            result.ToString(),
            result.Proof?.ToString(),
            result.Primary?.ToString(),
            result.DeliveryOpportunity?.ToString(),
        }.Concat(portModels.Select(model => model.ToString())).ToArray();

        foreach (var value in rendered)
        {
            Assert.DoesNotContain(request.Prompt, value, StringComparison.Ordinal);
            Assert.DoesNotContain("private-route-one", value, StringComparison.Ordinal);
            Assert.DoesNotContain("workspace-one", value, StringComparison.Ordinal);
            Assert.DoesNotContain("governed-loop", value, StringComparison.Ordinal);
            Assert.DoesNotContain("revision-1", value, StringComparison.Ordinal);
            Assert.DoesNotContain("node-one", value, StringComparison.Ordinal);
            Assert.DoesNotContain("run-one", value, StringComparison.Ordinal);
            Assert.DoesNotContain("checkpoint-one", value, StringComparison.Ordinal);
            Assert.DoesNotContain("human-input-actor", value, StringComparison.Ordinal);
            Assert.DoesNotContain(HumanInputRequestLifecycleTestData.Hash('a'), value, StringComparison.Ordinal);
            Assert.DoesNotContain(harness.Grant.GrantId.Value, value, StringComparison.Ordinal);
            Assert.DoesNotContain(command.Reason.Value, value, StringComparison.Ordinal);
        }
    }
}
