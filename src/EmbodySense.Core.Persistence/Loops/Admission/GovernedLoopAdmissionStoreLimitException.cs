namespace EmbodySense.Core.Persistence.Loops.Admission;

internal sealed class GovernedLoopAdmissionStoreLimitException : Exception
{
    public GovernedLoopAdmissionStoreLimitException()
        : base("The bounded governed-loop admission ledger limit would be exceeded.")
    {
    }
}
