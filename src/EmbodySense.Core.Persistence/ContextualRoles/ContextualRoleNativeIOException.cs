using EmbodySense.Core.Application.ContextualRoles.Models;

namespace EmbodySense.Core.Persistence.ContextualRoles;

internal sealed class ContextualRoleNativeIOException : IOException
{
    public ContextualRoleNativeIOException(string message, ContextualRoleNativeErrorKind errorKind, long errorCode, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorKind = errorKind;
        ErrorCode = errorCode;
    }

    public ContextualRoleNativeErrorKind ErrorKind { get; }
    public long ErrorCode { get; }
}
