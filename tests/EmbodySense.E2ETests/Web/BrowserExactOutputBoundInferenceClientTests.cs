using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.E2EBrowserHost;

namespace EmbodySense.E2ETests.Web;

public sealed class BrowserExactOutputBoundInferenceClientTests
{
    [Theory]
    [InlineData("gpt-test")]
    [InlineData("gpt-secondary")]
    public async Task GenerateAsync_returns_the_exact_admitted_profile_model(string modelId)
    {
        var client = new BrowserExactOutputBoundInferenceClient(modelId);
        var request = LlmInferenceRequest.FromUserText(
            "return the exact bounded response",
            new LlmInferenceOptions { MaxOutputTokenCount = 1 });

        var response = await client.GenerateAsync(request);

        Assert.Equal(modelId, response.Model);
        Assert.Equal(1, response.Usage.OutputTokens.Value);
        Assert.Equal(GovernedModelUsageEvidenceStatus.Authoritative, response.Usage.OutputTokens.Status);
    }
}
