namespace EmbodySense.Core.Application.Loops.Sleep;

/// <summary>Defines finite bounds for one local background-work enumeration.</summary>
public static class GovernedLoopBackgroundWorkContractLimits
{
    /// <summary>Gets the largest admitted candidate count for each work family.</summary>
    public const int MaxCandidatesPerFamily = 256;
}
