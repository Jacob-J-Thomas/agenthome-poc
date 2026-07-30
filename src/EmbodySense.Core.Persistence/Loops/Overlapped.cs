using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Loops;

[StructLayout(LayoutKind.Sequential)]
internal struct Overlapped
{
    public IntPtr Internal;
    public IntPtr InternalHigh;
    public uint Offset;
    public uint OffsetHigh;
    public IntPtr EventHandle;
}
