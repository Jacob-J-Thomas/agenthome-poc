using EmbodySense.Core.Inference.Interfaces;
using EmbodySense.Core.Inference.Models;
using EmbodySense.Core.Tools;
using EmbodySense.Core.Tools.Models;

namespace EmbodySense.Core.Harness;

public sealed class AgentHarnessSession
{
    private readonly ILlmInferenceClient _inferenceClient;
    private readonly IToolBroker? _toolBroker;
    private readonly int _maxToolRounds;
    private readonly List<LlmMessage> _messages = [];

    public AgentHarnessSession(ILlmInferenceClient inferenceClient, IToolBroker? toolBroker = null, int maxToolRounds = 4)
    {
        ArgumentNullException.ThrowIfNull(inferenceClient);

        _inferenceClient = inferenceClient;
        _toolBroker = toolBroker;
        _maxToolRounds = maxToolRounds > 0 ? maxToolRounds : throw new ArgumentOutOfRangeException(nameof(maxToolRounds), maxToolRounds, "Max tool rounds must be greater than zero.");

        if (_toolBroker is not null)
        {
            _messages.Add(LlmMessage.System(AgentToolProtocol.SystemInstructions));
        }
    }

    public IReadOnlyList<LlmMessage> Messages => _messages;

    public async Task<LlmInferenceResponse> SendUserMessageAsync(string input, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        _messages.Add(LlmMessage.User(input));

        for (var toolRound = 0; toolRound < _maxToolRounds; toolRound++)
        {
            var response = await _inferenceClient.GenerateAsync(new LlmInferenceRequest(_messages), cancellationToken);
            _messages.Add(LlmMessage.Assistant(response.OutputText));

            if (_toolBroker is null)
            {
                return response;
            }

            IReadOnlyList<ToolRequest> toolRequests;

            try
            {
                toolRequests = AgentToolProtocol.ParseRequests(response.OutputText);
            }
            catch (Exception exception) when (exception is FormatException or System.Text.Json.JsonException)
            {
                _messages.Add(LlmMessage.Tool($"Tool request parse failed: {exception.Message}"));
                continue;
            }

            if (toolRequests.Count == 0)
            {
                return response;
            }

            var toolResults = new List<ToolResult>();

            foreach (var toolRequest in toolRequests)
            {
                toolResults.Add(await _toolBroker.ExecuteAsync(toolRequest, cancellationToken));
            }

            _messages.Add(LlmMessage.Tool(AgentToolProtocol.FormatResults(toolResults)));
        }

        var limitResponse = new LlmInferenceResponse(
            "Tool round limit reached before the assistant produced a final non-tool response.",
            LlmInferenceSurface.Unknown);
        _messages.Add(LlmMessage.Assistant(limitResponse.OutputText));
        return limitResponse;
    }
}
