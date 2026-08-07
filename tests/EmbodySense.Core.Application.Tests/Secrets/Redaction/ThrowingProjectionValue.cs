namespace EmbodySense.Core.Application.Tests.Secrets.Redaction;

internal sealed class ThrowingProjectionValue
{
    public override string ToString()
    {
        throw new InvalidOperationException("Arbitrary projection code must not run.");
    }
}
