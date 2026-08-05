using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Capabilities;

[StructLayout(LayoutKind.Sequential)]
internal struct CapabilityCatalogStatxTimestamp
{
    public long Seconds;
    public uint Nanoseconds;
    public int Reserved;
}
