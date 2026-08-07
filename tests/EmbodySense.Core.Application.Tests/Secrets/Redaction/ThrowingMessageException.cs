namespace EmbodySense.Core.Application.Tests.Secrets.Redaction;

internal sealed class ThrowingMessageException : Exception
{
    public override string Message => throw new InvalidOperationException("Hostile message getter.");
}
