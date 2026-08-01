using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace EmbodySense.Web.Tests;

internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = [];

    public IReadOnlyCollection<string> Messages => _messages.ToArray();

    public ILogger CreateLogger(string categoryName)
    {
        return new RecordingLogger(categoryName, _messages);
    }

    public void Dispose()
    {
    }
}
