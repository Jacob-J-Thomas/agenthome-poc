using System.Runtime.InteropServices;

namespace EmbodySense.Core.Persistence.Capabilities;

[StructLayout(LayoutKind.Sequential)]
internal struct CapabilityCatalogMacVolumeCapabilitiesBuffer
{
    public uint Length;
    public uint FormatCapabilities;
    public uint InterfaceCapabilities;
    public uint ReservedCapability1;
    public uint ReservedCapability2;
    public uint ValidFormatCapabilities;
    public uint ValidInterfaceCapabilities;
    public uint ValidReservedCapability1;
    public uint ValidReservedCapability2;
}
