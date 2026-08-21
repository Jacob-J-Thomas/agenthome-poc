namespace EmbodySense.Core.Application.Loops.Posture;

/// <summary>Retains exclusive execution ownership for one pending operational-control receipt.</summary>
public interface IGovernedLoopOperationalControlLease : IDisposable
{
}
