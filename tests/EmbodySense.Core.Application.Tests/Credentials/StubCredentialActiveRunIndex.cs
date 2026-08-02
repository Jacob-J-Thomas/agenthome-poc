using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Common.Credentials.Models;

namespace EmbodySense.Core.Application.Tests.Credentials;

internal sealed class StubCredentialActiveRunIndex : ICredentialActiveRunIndex
{
    internal IReadOnlyList<string> Runs { get; set; } = [];
    internal Exception? Failure { get; set; }
    public Task<IReadOnlyList<string>> CaptureAsync(CredentialCapabilityBinding binding, CancellationToken cancellationToken) => Failure is null ? Task.FromResult(Runs) : Task.FromException<IReadOnlyList<string>>(Failure);
}
