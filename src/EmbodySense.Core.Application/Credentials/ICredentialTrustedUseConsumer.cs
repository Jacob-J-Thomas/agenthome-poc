namespace EmbodySense.Core.Application.Credentials;

/// <summary>Defines the trusted callback boundary for ephemeral credential use without a value-returning API.</summary>
public interface ICredentialTrustedUseConsumer
{
    /// <summary>Uses provider-owned ephemeral bytes synchronously and must not retain them.</summary>
    /// <param name="credential">The provider-owned ephemeral bytes.</param>
    void Use(ReadOnlySpan<byte> credential);
}
