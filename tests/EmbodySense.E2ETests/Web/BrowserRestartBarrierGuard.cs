namespace EmbodySense.E2ETests.Web;

internal sealed class BrowserRestartBarrierGuard
{
    private readonly object _gate = new();
    private long _generation;

    public long Prepare(Action prepare)
    {
        lock (_gate)
        {
            prepare();
            return ++_generation;
        }
    }

    public long ReadGeneration()
    {
        lock (_gate)
        {
            return _generation;
        }
    }

    public void Abort(Action abort)
    {
        lock (_gate)
        {
            ++_generation;
            abort();
        }
    }

    public void RunIfCurrent(long generation, Action action)
    {
        lock (_gate)
        {
            if (generation == _generation)
            {
                action();
            }
        }
    }
}
