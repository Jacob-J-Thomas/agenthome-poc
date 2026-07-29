namespace EmbodySense.Core.Application.Loops.Execution.Custom;

internal sealed class ProviderDispatchState
{
    private int _providerWasInvoked;

    public bool ProviderWasInvoked => Volatile.Read(ref _providerWasInvoked) != 0;

    public void MarkProviderRequestStarted() => Interlocked.Exchange(ref _providerWasInvoked, 1);
}
