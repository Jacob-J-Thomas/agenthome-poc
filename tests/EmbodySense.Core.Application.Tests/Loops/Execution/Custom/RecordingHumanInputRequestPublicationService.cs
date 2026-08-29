using EmbodySense.Core.Application.HumanInput.Publication;
using EmbodySense.Core.Application.HumanInput.Publication.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

internal sealed class RecordingHumanInputRequestPublicationService(
    HumanInputRequestPublicationStatus status = HumanInputRequestPublicationStatus.Published) : IHumanInputRequestPublicationService
{
    internal List<HumanInputRequestPublicationRequest> Requests { get; } = [];

    internal HumanInputRequestPublicationStatus Status { get; set; } = status;

    public Task<HumanInputRequestPublicationHealthResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HumanInputRequestPublicationHealthResult(HumanInputRequestPublicationHealthStatus.Ready));
    }

    public Task<HumanInputRequestPublicationResult> PublishAsync(HumanInputRequestPublicationRequest? request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is not null)
        {
            Requests.Add(request);
        }

        return Task.FromResult(new HumanInputRequestPublicationResult(Status));
    }
}
