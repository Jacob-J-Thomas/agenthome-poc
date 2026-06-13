using EmbodySense.Core.Inference.Interfaces;
using EmbodySense.Core.Inference.Models;

namespace EmbodySense.Core.Harness;

public sealed class AgentHarnessSession
{
    private readonly ILlmInferenceClient _inferenceClient;
    private readonly List<LlmMessage> _messages = [];

    public AgentHarnessSession(ILlmInferenceClient inferenceClient)
    {
        ArgumentNullException.ThrowIfNull(inferenceClient);

        _inferenceClient = inferenceClient;
    }

    public IReadOnlyList<LlmMessage> Messages => _messages;

    public async Task<LlmInferenceResponse> SendUserMessageAsync(string input, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        _messages.Add(LlmMessage.User(input));

        var response = await _inferenceClient.GenerateAsync(new LlmInferenceRequest(_messages), cancellationToken);

        _messages.Add(LlmMessage.Assistant(response.OutputText));

        return response;
    }
}
