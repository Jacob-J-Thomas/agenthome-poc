using EmbodySense.Core.Application.HumanInput.Publication;
using EmbodySense.Core.Application.HumanInput.Publication.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class HumanInputResponseContinuationRecordingPublicationService(
    params HumanInputRequestPublicationStatus[] statuses) : IHumanInputRequestPublicationService
{
    private readonly Queue<HumanInputRequestPublicationStatus> _statuses = new(statuses.Length == 0
        ? [HumanInputRequestPublicationStatus.Published]
        : statuses);

    internal List<HumanInputRequestPublicationRequest> Requests { get; } = [];

    internal HumanInputRequestPublicationHealthStatus HealthStatus { get; set; } = HumanInputRequestPublicationHealthStatus.Ready;

    internal int HealthProbeCount { get; private set; }

    internal Func<CancellationToken, Task<HumanInputRequestPublicationHealthResult>>? ProbeOverride { get; set; }

    internal int PublishCount { get; private set; }

    internal Func<HumanInputRequestPublicationRequest?, CancellationToken, Task<HumanInputRequestPublicationResult>>? PublishOverride { get; set; }

    public Task<HumanInputRequestPublicationHealthResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HealthProbeCount++;
        if (ProbeOverride is not null)
        {
            return ProbeOverride(cancellationToken);
        }

        return Task.FromResult(new HumanInputRequestPublicationHealthResult(HealthStatus));
    }

    public Task<HumanInputRequestPublicationResult> PublishAsync(HumanInputRequestPublicationRequest? request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PublishCount++;
        if (request is not null)
        {
            Requests.Add(request);
        }

        if (PublishOverride is not null)
        {
            return PublishOverride(request, cancellationToken);
        }

        var status = _statuses.Count == 0 ? HumanInputRequestPublicationStatus.Published : _statuses.Dequeue();
        return Task.FromResult(new HumanInputRequestPublicationResult(status));
    }
}
