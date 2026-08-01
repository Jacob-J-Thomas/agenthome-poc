namespace EmbodySense.Core.Application.Credentials;

/// <summary>Copies caller-owned ephemeral credential bytes into provider-owned memory without placing them in a DTO.</summary>
/// <param name="destination">The exact-size provider-owned destination to fill.</param>
public delegate void CredentialSecretWriteCallback(Span<byte> destination);
