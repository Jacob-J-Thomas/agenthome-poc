using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Loops;

[StructLayout(LayoutKind.Sequential)]
internal struct CustomLoopRunWindowsUnicodeString
{
    public ushort Length;
    public ushort MaximumLength;
    public IntPtr Buffer;
}
