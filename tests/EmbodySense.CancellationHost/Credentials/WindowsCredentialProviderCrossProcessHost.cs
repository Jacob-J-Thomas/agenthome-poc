using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;
using EmbodySense.Core.Persistence.Credentials;

namespace EmbodySense.CancellationHost.Credentials;

internal static class WindowsCredentialProviderCrossProcessHost
{
    internal static async Task<int> RunMutexContentionAsync(string contentionId)
    {
        if (!OperatingSystem.IsWindows() || !Guid.TryParseExact(contentionId, "N", out _))
        {
            return 2;
        }

        var requests = Requests(
            "workspace-global-contention-" + contentionId,
            "credential-global-contention-" + contentionId);
        var callbackInvoked = false;
        var result = await new WindowsCredentialValueProvider().CreateAsync(
            requests.Mutation,
            _ =>
            {
                callbackInvoked = true;
                return requests.Mutation.ValueByteLength;
            },
            CancellationToken.None);
        return result.Failure?.Code == CredentialFailureCode.Unavailable && !callbackInvoked ? 0 : 3;
    }

    private static ProviderRequests Requests(string workspaceId, string referenceId)
    {
        if (!CredentialReferenceId.TryParse(referenceId, out var reference, out _)
            || !CredentialProviderId.TryParse("org.embodysense.windows", out var provider, out _)
            || !CredentialContractId.TryParse("operation-1", out var operation, out _))
        {
            throw new InvalidOperationException("The Windows credential-provider host identifiers are invalid.");
        }

        return new ProviderRequests(
            new CredentialProviderMutationRequest(workspaceId, reference!, provider!, operation!, 16),
            new CredentialProviderUseRequest(workspaceId, reference!, provider!, operation!),
            new CredentialProviderDeleteRequest(workspaceId, reference!, provider!, operation!));
    }

    private sealed record ProviderRequests(
        CredentialProviderMutationRequest Mutation,
        CredentialProviderUseRequest Use,
        CredentialProviderDeleteRequest Delete);
}
