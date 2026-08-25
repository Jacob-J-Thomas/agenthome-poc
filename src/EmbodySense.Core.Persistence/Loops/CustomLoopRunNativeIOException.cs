using EmbodySense.Core.Application.Loops.Models;

namespace EmbodySense.Core.Persistence.Loops;

internal sealed class CustomLoopRunNativeIOException : IOException
{
    public CustomLoopRunNativeIOException(string message, CustomLoopRunPersistenceNativeErrorKind errorKind, long errorCode, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorKind = errorKind;
        ErrorCode = errorCode;
    }

    public CustomLoopRunPersistenceNativeErrorKind ErrorKind { get; }

    public long ErrorCode { get; }
}
