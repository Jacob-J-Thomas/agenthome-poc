using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Web.Models;

namespace EmbodySense.Web;

/// <summary>Owns the process lifetime of the canonical Startup-composed governed-loop background coordinator.</summary>
/// <remarks>
/// This host deliberately does not accept HTTP, SignalR, or caller cancellation tokens. It defers factory composition
/// until workspace initialization, then retains the exact Web runtime for the whole process lifetime. Startup results are
/// projected only through the non-sensitive status posture. During shutdown it stops durable admission, waits while the
/// Startup contract truthfully reports draining, and releases the pinned runtime once at a safe boundary.
/// </remarks>
internal sealed class WebGovernedLoopBackgroundHostedService : BackgroundService
{
    private static readonly TimeSpan _reconciliationInterval = TimeSpan.FromMilliseconds(250);
    private readonly WebAgentRuntimeHost _host;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private int _stopRequested;
    private bool _startCompleted;
    private bool _startRetryBlocked;

    internal WebGovernedLoopBackgroundHostedService(WebAgentRuntimeHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _host.SignalHostShutdown();
        Interlocked.Exchange(ref _stopRequested, 1);
        _host.SetGovernedLoopBackgroundPosture(WebGovernedLoopBackgroundPosture.Draining);
        await _host.WaitForConversationTurnsAsync().ConfigureAwait(false);
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await DrainAndReleaseAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && Volatile.Read(ref _stopRequested) == 0)
        {
            await _lifecycleGate.WaitAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _stopRequested) == 0)
                {
                    await ReconcileAsync().ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                _startCompleted = false;
                if (Volatile.Read(ref _stopRequested) == 0)
                {
                    _host.SetGovernedLoopBackgroundPosture(WebGovernedLoopBackgroundPosture.Unavailable);
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }

            try
            {
                await Task.Delay(_reconciliationInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ReconcileAsync()
    {
        if (_startRetryBlocked)
        {
            return;
        }

        if (!_startCompleted)
        {
            var start = await _host.StartGovernedLoopLocalBackgroundForProcessAsync().ConfigureAwait(false);
            _host.SetGovernedLoopBackgroundPosture(ToPosture(start.Readiness));
            _startCompleted = start.Readiness == AgentRuntimeGovernedLoopBackgroundReadiness.Ready
                && start.Ownership == AgentRuntimeGovernedLoopBackgroundOwnership.Local;
            _startRetryBlocked = !start.RetryAllowed;
            return;
        }

        var status = await _host.ReadGovernedLoopLocalBackgroundForProcessAsync().ConfigureAwait(false);
        _host.SetGovernedLoopBackgroundPosture(ToPosture(status.Readiness));
        if (status.Readiness != AgentRuntimeGovernedLoopBackgroundReadiness.Ready
            || status.Ownership != AgentRuntimeGovernedLoopBackgroundOwnership.Local)
        {
            _startCompleted = false;
        }
    }

    private async Task DrainAndReleaseAsync()
    {
        var stop = await _host.StopGovernedLoopLocalBackgroundForProcessAsync().ConfigureAwait(false);
        _host.SetGovernedLoopBackgroundPosture(ToPosture(stop.Readiness));
        if (stop.Readiness != AgentRuntimeGovernedLoopBackgroundReadiness.Stopped)
        {
            // Keep the complete runtime composition pinned while the Startup coordinator parks the admitted one-shot
            // or confirms an unavailable boundary. The completion task releases and projects the terminal posture only
            // after that stop task reaches its safe boundary.
            _ = ObserveDeferredStopReleaseAsync();
            return;
        }

        await _host.ReleaseGovernedLoopLocalBackgroundForProcessAsync().ConfigureAwait(false);
        _host.SetGovernedLoopBackgroundPosture(ToPosture(stop.Readiness));
    }

    private async Task ObserveDeferredStopReleaseAsync()
    {
        try
        {
            await _host.BeginGovernedLoopLocalBackgroundStopReleaseAsync().ConfigureAwait(false);
        }
        catch
        {
            // A failed terminal read remains fail closed; do not project Stopped after an unproven cleanup boundary.
            _host.SetGovernedLoopBackgroundPosture(WebGovernedLoopBackgroundPosture.Unavailable);
        }
    }

    private static WebGovernedLoopBackgroundPosture ToPosture(AgentRuntimeGovernedLoopBackgroundReadiness readiness)
    {
        return readiness switch
        {
            AgentRuntimeGovernedLoopBackgroundReadiness.Ready => WebGovernedLoopBackgroundPosture.Ready,
            AgentRuntimeGovernedLoopBackgroundReadiness.Degraded => WebGovernedLoopBackgroundPosture.Degraded,
            AgentRuntimeGovernedLoopBackgroundReadiness.Draining => WebGovernedLoopBackgroundPosture.Draining,
            AgentRuntimeGovernedLoopBackgroundReadiness.Stopped => WebGovernedLoopBackgroundPosture.Stopped,
            _ => WebGovernedLoopBackgroundPosture.Unavailable
        };
    }
}
