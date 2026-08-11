using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Common.Tests.HumanInput.Lifecycle;

public sealed class HumanInputRequestLifecyclePrivacyTests
{
    [Fact]
    public void Validation_errors_are_value_free_for_private_candidate_content()
    {
        const string PromptCanary = "secret-prompt-canary";
        const string RouteCanary = "secret-route-canary";
        var previous = HumanInputLifecycleTestData.Request();
        var candidate = HumanInputLifecycleTestData.Rehash(previous with
        {
            RequestVersionId = "version-two",
            Prompt = PromptCanary,
            EligibleRespondents = [new HumanInputEligibleRespondent("user-two", RouteCanary)]
        });
        var previousHead = HumanInputLifecycleTestData.Head(previous);
        var resultHead = HumanInputLifecycleTestData.Head(candidate, lifecycleVersion: 2, operationId: "operation-two", updatedAtUtc: HumanInputLifecycleTestData.Now.AddMinutes(1));
        var evidence = HumanInputLifecycleTestData.Evidence(HumanInputRequestLifecycleOperationKind.Amend, previousHead, resultHead, candidate);

        var validation = HumanInputRequestLifecycleValidator.ValidateCommittedTransition(evidence, previous, candidate);
        var text = string.Join('\n', validation.Errors.Select(error => $"{error.Path}: {error.Message}"));

        Assert.False(validation.IsValid);
        Assert.DoesNotContain(PromptCanary, text, StringComparison.Ordinal);
        Assert.DoesNotContain(RouteCanary, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Lifecycle_evidence_carries_only_request_references_not_private_request_values()
    {
        var request = HumanInputLifecycleTestData.Request(prompt: "private-prompt", respondents: [new("user-one", "private-route")]);
        var previous = HumanInputLifecycleTestData.Head(request);
        var result = previous with { LifecycleVersion = 2, ReminderCount = 1, LastOperationId = "operation-two", UpdatedAtUtc = HumanInputLifecycleTestData.Now.AddMinutes(1) };
        var evidence = HumanInputLifecycleTestData.Evidence(HumanInputRequestLifecycleOperationKind.Remind, previous, result);

        Assert.Null(evidence.CandidateRequest);
        Assert.DoesNotContain("private-prompt", evidence.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-route", evidence.ToString(), StringComparison.Ordinal);
        Assert.True(HumanInputRequestLifecycleValidator.ValidateEvidence(evidence).IsValid);
    }
}
