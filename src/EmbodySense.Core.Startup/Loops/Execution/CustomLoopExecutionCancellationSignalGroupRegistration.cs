namespace EmbodySense.Core.Startup.Loops.Execution;

internal sealed class CustomLoopExecutionCancellationSignalGroupRegistration(
    IDisposable primary,
    IDisposable secondary) : IDisposable
{
    private IDisposable? _primary = primary;
    private IDisposable? _secondary = secondary;

    public void Dispose()
    {
        Interlocked.Exchange(ref _secondary, null)?.Dispose();
        Interlocked.Exchange(ref _primary, null)?.Dispose();
    }
}
