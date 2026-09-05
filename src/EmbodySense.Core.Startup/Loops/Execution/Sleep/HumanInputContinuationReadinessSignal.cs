using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Retains the current executable posture of the Startup-composed Human Input continuation worker.</summary>
/// <remarks>
/// Composition alone never makes the node executable. One bounded Human Input worker probe must first establish both
/// canonical policy-source health and a durable normal work outcome. Corrupt or unavailable dependency evidence
/// immediately clears the posture; caller cancellation is intentionally not observed here because it does not establish
/// a dependency failure.
/// </remarks>
public sealed class HumanInputContinuationReadinessSignal
{
    private int _isExecutable;

    /// <summary>Creates a signal that remains non-executable until a healthy bounded worker probe succeeds.</summary>
    public HumanInputContinuationReadinessSignal()
    {
    }

    /// <summary>Gets whether the current composed worker has a clean, policy-source-healthy bounded probe outcome.</summary>
    public bool IsExecutable => Volatile.Read(ref _isExecutable) != 0;

    internal void Invalidate() => Volatile.Write(ref _isExecutable, 0);

    internal void Observe(GovernedLoopLocalWorkResult? result)
    {
        switch (result?.Status)
        {
            case GovernedLoopLocalWorkResultStatus.Completed:
            case GovernedLoopLocalWorkResultStatus.Empty:
                Volatile.Write(ref _isExecutable, 1);
                break;
            case GovernedLoopLocalWorkResultStatus.Corrupt:
            case GovernedLoopLocalWorkResultStatus.Unavailable:
                Volatile.Write(ref _isExecutable, 0);
                break;
        }
    }
}
