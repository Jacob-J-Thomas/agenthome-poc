using EmbodySense.Core.Harness;
using EmbodySense.Core.Inference.Interfaces;
using EmbodySense.Core.Inference.Models;

namespace EmbodySense.Tests;

public sealed class AgentHarnessSessionTests
{
    [Fact]
    public async Task SendUserMessageAsync_stores_user_and_completed_assistant_response()
    {
        var client = new ScriptedInferenceClient("completed response");
        var session = new AgentHarnessSession(client);

        var response = await session.SendUserMessageAsync("hello");

        Assert.Equal("completed response", response.OutputText);
        Assert.Single(client.Requests);
        Assert.Collection(
            session.Messages,
            message =>
            {
                Assert.Equal(LlmMessageRole.User, message.Role);
                Assert.Equal("hello", message.Content);
            },
            message =>
            {
                Assert.Equal(LlmMessageRole.Assistant, message.Role);
                Assert.Equal("completed response", message.Content);
            });
    }

    [Fact]
    public async Task SendUserMessageAsync_streams_chunks_through_single_inference_path()
    {
        var client = new ScriptedInferenceClient("streamed response");
        var session = new AgentHarnessSession(client);
        var chunks = new List<string>();

        var response = await session.SendUserMessageAsync("hello", (chunk, _) =>
        {
            chunks.Add(chunk);
            return Task.CompletedTask;
        });

        Assert.Equal("streamed response", response.OutputText);
        Assert.Equal("streamed response", string.Concat(chunks));
        Assert.Single(client.Requests);
        Assert.Contains(session.Messages, message => message.Role == LlmMessageRole.Assistant && message.Content == "streamed response");
    }

    private sealed class ScriptedInferenceClient(params string[] outputs) : ILlmInferenceClient
    {
        private readonly Queue<string> _outputs = new(outputs);

        public List<IReadOnlyList<LlmMessage>> Requests { get; } = [];

        public async Task<LlmInferenceResponse> GenerateAsync(
            LlmInferenceRequest request,
            Func<string, CancellationToken, Task>? responseChunkHandler = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request.Messages.ToArray());
            var output = _outputs.Dequeue();

            if (responseChunkHandler is not null)
            {
                var midpoint = output.Length / 2;
                if (midpoint > 0)
                {
                    await responseChunkHandler(output[..midpoint], cancellationToken);
                    await responseChunkHandler(output[midpoint..], cancellationToken);
                }
                else
                {
                    await responseChunkHandler(output, cancellationToken);
                }
            }

            return new LlmInferenceResponse(output, LlmInferenceSurface.OpenAiCodex);
        }
    }
}
