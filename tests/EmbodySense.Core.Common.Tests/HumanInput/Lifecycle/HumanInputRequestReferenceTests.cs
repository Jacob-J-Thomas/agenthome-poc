using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Common.Tests.HumanInput.Lifecycle;

public sealed class HumanInputRequestReferenceTests
{
    [Fact]
    public void Reference_creation_and_matching_require_one_exact_valid_request()
    {
        var request = HumanInputLifecycleTestData.Request();

        Assert.True(HumanInputRequestReference.TryCreate(request, out var reference, out var validation));
        Assert.True(validation.IsValid);
        Assert.True(reference!.Matches(request));
        Assert.False(reference.Matches(HumanInputLifecycleTestData.Request(requestVersionId: "version-two")));
        Assert.False(reference.Matches(request with { RequestHash = new string('f', 64) }));
        Assert.False(reference.Matches(null));

        Assert.False(HumanInputRequestReference.TryCreate(null, out var missing, out var missingValidation));
        Assert.Null(missing);
        Assert.False(missingValidation.IsValid);
        Assert.False(HumanInputRequestReference.TryCreate(request with { RequestHash = new string('f', 64) }, out var invalid, out _));
        Assert.Null(invalid);
        Assert.Contains(request.RequestId, reference.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Reference_validator_rejects_every_malformed_scalar_boundary()
    {
        var valid = HumanInputLifecycleTestData.Reference(HumanInputLifecycleTestData.Request());
        var variants = new[]
        {
            valid with { SchemaVersion = 2 },
            valid with { RequestId = "Invalid" },
            valid with { RequestVersionId = "-invalid" },
            valid with { RequestHash = new string('a', 63) },
            valid with { RequestHash = new string('A', 64) }
        };

        Assert.True(HumanInputRequestLifecycleValidator.ValidateReference(valid).IsValid);
        Assert.All(variants, variant => Assert.False(HumanInputRequestLifecycleValidator.ValidateReference(variant).IsValid));
        Assert.False(HumanInputRequestLifecycleValidator.ValidateReference(null).IsValid);
    }

    [Fact]
    public void Public_request_text_omits_private_contract_values()
    {
        const string PromptCanary = "private-prompt-canary";
        const string RouteCanary = "private-route-canary";
        const string PurposeCanary = "private-purpose-canary";
        var request = HumanInputLifecycleTestData.Request(
            purpose: PurposeCanary,
            prompt: PromptCanary,
            respondents: [new HumanInputEligibleRespondent("user-one", "role-one", RouteCanary)]);

        var text = request.ToString();

        Assert.DoesNotContain(PromptCanary, text, StringComparison.Ordinal);
        Assert.DoesNotContain(RouteCanary, text, StringComparison.Ordinal);
        Assert.DoesNotContain(PurposeCanary, text, StringComparison.Ordinal);
        Assert.Contains(request.RequestId, text, StringComparison.Ordinal);
        Assert.Contains(request.RequestHash, text, StringComparison.Ordinal);
    }
}
