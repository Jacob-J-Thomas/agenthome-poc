using EmbodySense.Core.Application.Credentials;

namespace EmbodySense.Core.Persistence.Tests.Credentials;

internal sealed class RecordingCredentialConsumer : ICredentialTrustedUseConsumer
{
    internal byte[] Observed { get; private set; } = [];

    public void Use(ReadOnlySpan<byte> credential)
    {
        Observed = credential.ToArray();
    }
}
