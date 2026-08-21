namespace EmbodySense.Core.Application.Credentials.Leases;

/// <summary>Retains exclusive in-process ownership for one durable credential-use generation.</summary>
public interface ICredentialLeaseAttemptLease : IDisposable
{
}
