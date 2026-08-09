using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Capabilities;

[StructLayout(LayoutKind.Sequential)]
internal struct CapabilityCatalogWindowsUnicodeString
{
    public ushort Length;
    public ushort MaximumLength;
    public IntPtr Buffer;
}
