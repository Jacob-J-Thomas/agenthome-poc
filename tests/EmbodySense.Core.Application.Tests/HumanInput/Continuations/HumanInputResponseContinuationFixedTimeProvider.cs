namespace EmbodySense.Core.Application.Tests.HumanInput.Continuations;

internal sealed class HumanInputResponseContinuationFixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    internal Exception? GetUtcNowException { get; set; }

    public override DateTimeOffset GetUtcNow()
    {
        if (GetUtcNowException is not null)
        {
            throw GetUtcNowException;
        }

        return now;
    }
}
