using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.E2ETests.Web;

internal sealed class HumanReviewResponseLossRecordingCapabilityAdmissionService(ICapabilityAdmissionService inner) : ICapabilityAdmissionService
{
    public CapabilityRevalidationResult? Revalidation { get; private set; }

    public string? ExceptionType { get; private set; }

    public Task<CapabilityAdmissionResult> AdmitAsync(CapabilityDependencyManifest requirements, IReadOnlyCollection<CapabilityId> allowedCapabilityIds, CancellationToken cancellationToken = default)
        => inner.AdmitAsync(requirements, allowedCapabilityIds, cancellationToken);

    public async Task<CapabilityRevalidationResult> RevalidateAsync(CapabilityAdmissionSnapshot snapshot, IReadOnlyCollection<CapabilityId> allowedCapabilityIds, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await inner.RevalidateAsync(snapshot, allowedCapabilityIds, cancellationToken).ConfigureAwait(false);
            Revalidation = result;
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            ExceptionType = exception.GetType().Name;
            throw;
        }
    }
}
