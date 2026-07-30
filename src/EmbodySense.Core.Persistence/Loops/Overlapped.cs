using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Represents an overlapped.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Overlapped
{
    /// <summary>
    /// Identifies the internal overlapped.
    /// </summary>
    public IntPtr Internal;
    /// <summary>
    /// Identifies the internal high overlapped.
    /// </summary>
    public IntPtr InternalHigh;
    /// <summary>
    /// Identifies the offset overlapped.
    /// </summary>
    public uint Offset;
    /// <summary>
    /// Identifies the offset high overlapped.
    /// </summary>
    public uint OffsetHigh;
    /// <summary>
    /// Identifies the event handle overlapped.
    /// </summary>
    public IntPtr EventHandle;
}
