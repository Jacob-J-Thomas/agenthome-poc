using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.Core.Persistence.Tests.Loops;

internal sealed class WindowsBrokerFaultLifecycleObserver : ICustomLoopCancellationBrokerLifecycleObserver, IDisposable
{
    private readonly Action<string>? _afterBrokerReady;
    private readonly ManualResetEventSlim _continueFaultRetirement;

    public WindowsBrokerFaultLifecycleObserver(Action<string>? afterBrokerReady = null, bool blockFaultRetirement = false)
    {
        _afterBrokerReady = afterBrokerReady;
        _continueFaultRetirement = new ManualResetEventSlim(!blockFaultRetirement);
    }

    public TaskCompletionSource<bool> BrokerFaulted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void OnBrokerReadyBeforeOwnerDescriptorPublication(string pipeName) => _afterBrokerReady?.Invoke(pipeName);

    public void OnBrokerFaulted()
    {
        BrokerFaulted.TrySetResult(true);
        _continueFaultRetirement.Wait(TimeSpan.FromSeconds(10));
    }

    public void ContinueFaultRetirement() => _continueFaultRetirement.Set();

    public void Dispose() => _continueFaultRetirement.Dispose();
}
