namespace EmbodySense.Core.Clients.CodexAppServer;

/// <summary>
/// Exchanges newline-delimited JSON protocol messages with a Codex app-server process.
/// </summary>
/// <remarks>
/// Implementations preserve message boundaries: one call writes and flushes one protocol line, and one read returns one
/// complete line or <see langword="null"/> at end of stream. Cancellation and transport I/O failures propagate to the caller.
/// </remarks>
public interface ICodexAppServerTransport : IAsyncDisposable
{
    /// <summary>
    /// Gets the diagnostic standard-error text retained by the transport.
    /// </summary>
    /// <value>Captured diagnostic text; implementations may bound or truncate it.</value>
    string ErrorOutput { get; }

    /// <summary>
    /// Reads the next complete protocol line.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The next line without its line terminator, or <see langword="null"/> when the server stream has ended.</returns>
    Task<string?> ReadLineAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes and flushes one complete protocol line.
    /// </summary>
    /// <param name="line">The serialized JSON message without a line terminator.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task WriteLineAsync(string line, CancellationToken cancellationToken = default);
}
