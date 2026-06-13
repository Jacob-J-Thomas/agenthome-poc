namespace EmbodySense.Core.Inference.Interfaces;

public interface ICodexAppServerTransport : IAsyncDisposable
{
    string ErrorOutput { get; }

    Task<string?> ReadLineAsync(CancellationToken cancellationToken = default);

    Task WriteLineAsync(string line, CancellationToken cancellationToken = default);
}
