using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Loops;

[StructLayout(LayoutKind.Sequential)]
internal struct CustomLoopRunWindowsObjectAttributes
{
    public int Length;
    public IntPtr RootDirectory;
    public IntPtr ObjectName;
    public uint Attributes;
    public IntPtr SecurityDescriptor;
    public IntPtr SecurityQualityOfService;
}
