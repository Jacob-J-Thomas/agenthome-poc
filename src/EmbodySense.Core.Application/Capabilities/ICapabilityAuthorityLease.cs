namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Retains exclusive workspace capability-authority ownership until disposed.</summary>
/// <remarks>The lease is a short-lived transaction fence. Callers must not retain it across long-running model or process execution.</remarks>
public interface ICapabilityAuthorityLease : IAsyncDisposable
{
}
