using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Projects the surface-neutral current-operator coordinator repair workflow through the composed Startup runtime.</summary>
/// <remarks>
/// The facade owns no Web, CLI, or persistence state. It first delegates authority and append-only repair admission to
/// Application, then asks the one canonical background host to perform its existing fresh fenced startup path.
/// </remarks>
public sealed class GovernedLoopCoordinatorRepairFacade
{
    private readonly IGovernedLoopCoordinatorRepairStartupPort _host;
    private readonly IGovernedLoopCoordinatorRepairService _service;

    /// <summary>Creates one surface-neutral repair facade over current-operator admission and the canonical background host.</summary>
    /// <param name="service">The authority-bound append-only repair admission service.</param>
    /// <param name="host">The sole fenced background-host start port used only after an accepted repair.</param>
    public GovernedLoopCoordinatorRepairFacade(
        IGovernedLoopCoordinatorRepairService service,
        IGovernedLoopCoordinatorRepairStartupPort host)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>Builds a read-only exact repair preview for the authenticated current runtime operator.</summary>
    public Task<GovernedLoopCoordinatorRepairPreview> PreviewAsync(
        GovernedLoopCoordinatorRepairPreviewRequest request,
        CancellationToken cancellationToken = default)
        => _service.PreviewAsync(request, cancellationToken);

    /// <summary>Appends or exactly replays an approved repair, then performs only the canonical fresh fenced startup path.</summary>
    public async Task<GovernedLoopCoordinatorRepairExecutionResult> SubmitAsync(
        GovernedLoopCoordinatorRepairSubmitRequest request,
        CancellationToken cancellationToken = default)
    {
        var submitted = await _service.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
        if (submitted.Status is not (GovernedLoopCoordinatorRepairSubmitStatus.Accepted or GovernedLoopCoordinatorRepairSubmitStatus.Replayed))
        {
            return new GovernedLoopCoordinatorRepairExecutionResult(Map(submitted.Status), submitted, null);
        }

        AgentRuntimeGovernedLoopBackgroundStartResult started;
        try
        {
            started = await _host.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new GovernedLoopCoordinatorRepairExecutionResult(
                GovernedLoopCoordinatorRepairExecutionStatus.Unavailable,
                submitted,
                null);
        }

        var status = started.Status switch
        {
            AgentRuntimeGovernedLoopBackgroundStartStatus.Started or AgentRuntimeGovernedLoopBackgroundStartStatus.AlreadyRunning
                => submitted.Status == GovernedLoopCoordinatorRepairSubmitStatus.Accepted
                    ? GovernedLoopCoordinatorRepairExecutionStatus.Repaired
                    : GovernedLoopCoordinatorRepairExecutionStatus.Replayed,
            AgentRuntimeGovernedLoopBackgroundStartStatus.OwnedByLivePeer => GovernedLoopCoordinatorRepairExecutionStatus.Conflict,
            AgentRuntimeGovernedLoopBackgroundStartStatus.Unavailable => GovernedLoopCoordinatorRepairExecutionStatus.Unavailable,
            _ => GovernedLoopCoordinatorRepairExecutionStatus.Conflict
        };
        return new GovernedLoopCoordinatorRepairExecutionResult(status, submitted, started);
    }

    private static GovernedLoopCoordinatorRepairExecutionStatus Map(GovernedLoopCoordinatorRepairSubmitStatus status)
        => status switch
        {
            GovernedLoopCoordinatorRepairSubmitStatus.Replayed => GovernedLoopCoordinatorRepairExecutionStatus.Replayed,
            GovernedLoopCoordinatorRepairSubmitStatus.Invalid => GovernedLoopCoordinatorRepairExecutionStatus.Invalid,
            GovernedLoopCoordinatorRepairSubmitStatus.Unauthorized => GovernedLoopCoordinatorRepairExecutionStatus.Unauthorized,
            GovernedLoopCoordinatorRepairSubmitStatus.Conflict => GovernedLoopCoordinatorRepairExecutionStatus.Conflict,
            GovernedLoopCoordinatorRepairSubmitStatus.Corrupt => GovernedLoopCoordinatorRepairExecutionStatus.Corrupt,
            _ => GovernedLoopCoordinatorRepairExecutionStatus.Unavailable
        };
}
