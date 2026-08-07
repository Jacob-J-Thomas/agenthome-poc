using EmbodySense.Core.Application.Credentials;

namespace EmbodySense.Core.Application.Tests.Credentials;

internal sealed class RecordingConsumer : ICredentialTrustedUseConsumer
{
    internal byte[] Observed { get; private set; } = [];

    public void Use(ReadOnlySpan<byte> credential)
    {
        Observed = credential.ToArray();
    }
}
