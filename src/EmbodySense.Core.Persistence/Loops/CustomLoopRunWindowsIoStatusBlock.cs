using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Loops;

[StructLayout(LayoutKind.Sequential)]
internal struct CustomLoopRunWindowsIoStatusBlock
{
    public IntPtr Status;
    public IntPtr Information;
}
