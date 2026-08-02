using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Capabilities;

[StructLayout(LayoutKind.Sequential)]
internal struct CapabilityCatalogWindowsIoStatusBlock
{
    public IntPtr Status;
    public nuint Information;
}
