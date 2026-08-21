namespace EmbodySense.Core.Application.Loops.Wait.Models;

/// <summary>Reports bounded recovery work over authoritative Wait runtimes.</summary>
/// <param name="Inspected">The number of exact candidates inspected.</param>
/// <param name="Recovered">The number advanced to a conclusive parked, resumed, or completed state.</param>
/// <param name="NeedsReview">The number conservatively classified for durable attention.</param>
public sealed record GovernedLoopWaitRecoveryResult(int Inspected, int Recovered, int NeedsReview);
