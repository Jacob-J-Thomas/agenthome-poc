using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Capabilities;

[StructLayout(LayoutKind.Sequential)]
internal struct CapabilityCatalogWindowsObjectAttributes
{
    public int Length;
    public IntPtr RootDirectory;
    public IntPtr ObjectName;
    public uint Attributes;
    public IntPtr SecurityDescriptor;
    public IntPtr SecurityQualityOfService;
}
