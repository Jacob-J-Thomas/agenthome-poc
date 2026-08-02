using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Capabilities;

[StructLayout(LayoutKind.Sequential)]
internal struct CapabilityCatalogMacTimespec
{
    public long Seconds;
    public long Nanoseconds;
}
