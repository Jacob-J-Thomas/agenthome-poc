using System.Collections;

namespace EmbodySense.Core.Application.Tests.Secrets.Redaction;

internal sealed class ThrowingDataException : Exception
{
    public override IDictionary Data => throw new InvalidOperationException("Hostile data getter.");
}
