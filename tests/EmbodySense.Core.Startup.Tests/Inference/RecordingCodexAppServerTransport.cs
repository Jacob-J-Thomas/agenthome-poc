using EmbodySense.Core.Clients.CodexAppServer;

namespace EmbodySense.Core.Startup.Tests.Inference;

internal sealed class RecordingCodexAppServerTransport : ICodexAppServerTransport
{
    public string ErrorOutput => "";

    public List<string> Writes { get; } = [];

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public Task<string?> ReadLineAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(null);
    }

    public Task WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        Writes.Add(line);
        return Task.CompletedTask;
    }
}
