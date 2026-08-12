using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;
using EmbodySense.Core.Persistence.Credentials;

namespace EmbodySense.CancellationHost.Credentials;

internal static class WindowsCredentialProviderCrossProcessHost
{
    internal static async Task<int> RunExternalValueAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 2;
        }

        var requests = Requests("workspace-external-process-v1", "credential-external-process-v1");
        byte[] value = [99, 114, 111, 115, 115, 45, 112, 114, 111, 99, 101, 115, 115, 45, 115, 101, 99, 114, 101, 116];
        try
        {
            var result = await new WindowsCredentialValueProvider().CreateAsync(
                requests.Mutation with { ValueByteLength = value.Length },
                destination => Copy(value, destination),
                CancellationToken.None);
            return result.Succeeded ? 0 : 3;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(value);
        }
    }

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

    private static int Copy(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        source.CopyTo(destination);
        return source.Length;
    }

    private sealed record ProviderRequests(
        CredentialProviderMutationRequest Mutation,
        CredentialProviderUseRequest Use,
        CredentialProviderDeleteRequest Delete);
}
