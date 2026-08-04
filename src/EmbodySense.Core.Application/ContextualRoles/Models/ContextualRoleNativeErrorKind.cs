namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Classifies a bounded native error code reported by contextual-role persistence.</summary>
public enum ContextualRoleNativeErrorKind
{
    /// <summary>No native error code was available for the failed stage.</summary>
    None = 0,
    /// <summary>The code is a Win32 <c>GetLastError</c> value.</summary>
    Win32 = 1,
    /// <summary>The code is an unsigned Windows NT status value.</summary>
    NtStatus = 2,
    /// <summary>The code is a POSIX <c>errno</c> value.</summary>
    PosixErrno = 3
}
