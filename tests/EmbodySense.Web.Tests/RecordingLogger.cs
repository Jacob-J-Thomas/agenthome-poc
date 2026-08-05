using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace EmbodySense.Web.Tests;

internal sealed class RecordingLogger(string categoryName, ConcurrentQueue<string> messages) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var renderedException = exception is null ? string.Empty : $"{Environment.NewLine}{exception}";
        messages.Enqueue($"{categoryName}: {formatter(state, exception)}{renderedException}");
    }
}
