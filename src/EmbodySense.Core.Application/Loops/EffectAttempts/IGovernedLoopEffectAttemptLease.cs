namespace EmbodySense.Core.Application.Loops.EffectAttempts;

/// <summary>Retains exclusive mutation ownership for one durable governed-loop effect-attempt generation.</summary>
public interface IGovernedLoopEffectAttemptLease : IDisposable
{
}
