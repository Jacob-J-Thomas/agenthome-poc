using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
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

    [Fact]
    public async Task GenerateAsync_returns_marker_bound_bounded_cycle_decisions()
    {
        var client = new BrowserExactOutputBoundInferenceClient("gpt-test");
        var options = new LlmInferenceOptions { MaxOutputTokenCount = 1 };
        var instructionContext = new LlmInferenceInstructionContext(
            EmbodySenseDeveloperInstructions.Capture([]),
            [new EmbodySenseTrustedInstruction("provider-inference", "visible-cycle-marker")]);
        LlmInferenceRequest CycleRequest(string prompt) => new([LlmMessage.User(prompt)], options, instructionContext);

        var success = await client.GenerateAsync(CycleRequest("visible-cycle-success"));
        var firstExhaustion = await client.GenerateAsync(CycleRequest("visible-cycle-exhaustion"));
        var secondExhaustion = await client.GenerateAsync(CycleRequest("visible-cycle-exhaustion"));
        var terminalExhaustion = await client.GenerateAsync(CycleRequest("visible-cycle-exhaustion"));

        Assert.Equal("terminal", success.OutputText);
        Assert.Equal("retry", firstExhaustion.OutputText);
        Assert.Equal("retry", secondExhaustion.OutputText);
        Assert.Equal("terminal", terminalExhaustion.OutputText);
    }
}
